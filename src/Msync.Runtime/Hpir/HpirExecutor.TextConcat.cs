using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct HpirTextConcat(HpirTextCompletion Header, nint Right);

internal static unsafe partial class HpirExecutor
{
    private readonly record struct TextOperand(nint Address, bool Pending);

    private static void MakeTextConcat(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination)
    {
        nint left = Read<nint>(frame, call.Arguments[0]), right = Read<nint>(frame, call.Arguments[1]);
        int mask = Read<int>(frame, call.Arguments[2]);
        nint cell = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
        *(HpirTextConcat*)cell = new(new(0, left, mask, HpirTextCompletionKind.Concat), right);
        Write(frame, destination, cell);
    }

    internal static nint DemandTextConcat(nint address, HpirTextDemandContext context, HpirWork? work,
        RuntimeTimelineScope? trace, CancellationToken token)
    {
        var root = (HpirTextConcat*)address;
        var (left, right) = ConcatOperands(*root);
        nint result;
        if (!IsTextConcat(left) && !IsTextConcat(right))
        {
            Span<nint> words = stackalloc nint[2];
            words[0] = ReadTextOperand(left, context, work, trace, token);
            words[1] = ReadTextOperand(right, context, work, trace, token);
            result = PackConcatText(words, context);
        }
        else
        {
            List<nint> words = [];
            var toRead = new ConcatTraversal(left, right);
            while (toRead.Next(out var operand)) words.Add(ReadTextOperand(operand, context, work, trace, token));
            result = PackConcatText(CollectionsMarshal.AsSpan(words), context);
        }
        *root = new(new(result), 0);
        return result;
    }

    internal static Task? WaitTextConcat(nint address, HpirTextDemandContext context, HpirWork? work)
    {
        var (left, right) = ConcatOperands(*(HpirTextConcat*)address);
        var reads = new ConcatTraversal(left, right);
        while (reads.Next(out var operand))
            if (operand.Pending && context.Waiting(operand.Address, work) is { } pending) return pending;
        return null;
    }

    private static HpirTransferTextInput BorrowConcatTransferText(HpirWork work, nint address)
    {
        var (left, right) = ConcatOperands(*(HpirTextConcat*)address);
        var toRead = new ConcatTraversal(left, right);
        List<HpirTransferTextInput> parts = [];
        while (toRead.Next(out var operand))
            parts.Add(operand.Pending ? BorrowTransferText(work, operand.Address) : new(CopyText(operand.Address)));
        return new(null, Parts: parts.ToArray());
    }

    private static nint ReadTextOperand(TextOperand operand, HpirTextDemandContext context, HpirWork? work,
        RuntimeTimelineScope? trace, CancellationToken token)
    {
        nint text = operand.Pending ? context.Demand(operand.Address, work, trace, token) : operand.Address;
        if (text == 0) throw new MsyncException("Expected String, received Null.");
        return text;
    }

    private static nint PackConcatText(ReadOnlySpan<nint> words, HpirTextDemandContext context)
    {
        int length = 0, nonempty = 0;
        nint sole = words[0];
        foreach (nint word in words)
        {
            int chars = HpirTextStorage.Read(word).Length;
            if (chars == 0) continue;
            length = checked(length + chars);
            ++nonempty;
            sole = word;
        }
        if (nonempty < 2) return sole;
        nint result = context.ReserveText(length, out Span<char> output);
        foreach (nint word in words)
        {
            var part = HpirTextStorage.Read(word);
            part.CopyTo(output);
            output = output[part.Length..];
        }
        return result;
    }

    private static bool IsTextConcat(TextOperand operand) => operand.Pending && operand.Address != 0
        && ((HpirTextCompletion*)operand.Address)->Kind == HpirTextCompletionKind.Concat;

    private static (TextOperand Left, TextOperand Right) ConcatOperands(HpirTextConcat node) =>
        (new(node.Header.Dependency, (node.Header.Job & 1) != 0), new(node.Right, (node.Header.Job & 2) != 0));

    private struct ConcatTraversal(TextOperand left, TextOperand right)
    {
        private TextOperand first = left;
        private TextOperand second = right;
        private Stack<TextOperand>? nested;

        internal bool Next(out TextOperand operand)
        {
            while (true)
            {
                if (first.Address != 0 || first.Pending) { operand = first; first = default; }
                else if (nested?.TryPop(out operand) != true)
                {
                    operand = second;
                    second = default;
                }
                if (operand.Address == 0 && !operand.Pending) return false;
                if (!IsTextConcat(operand)) return true;
                var (previous, tail) = ConcatOperands(*(HpirTextConcat*)operand.Address);
                (nested ??= new()).Push(tail);
                first = previous;
            }
        }
    }
}
