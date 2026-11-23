using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallPath(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, HpirPathAnchors anchors, ExecutionOptions? options,
        CancellationToken token)
    {
        var (op, leftText, rightText) = HpirPathImports.Decode(import.Binding);
        if (op is HpirPathOp.WorkingDirectory or HpirPathOp.HomeDirectory)
        {
            nint text = op == HpirPathOp.WorkingDirectory ? anchors.Directory : anchors.Home;
            Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), anchors.Capture(text));
            return;
        }
        bool returnsPath = HpirPathImports.ReturnsPath(op);
        int first = returnsPath ? 1 : 0;
        if (op == HpirPathOp.FromLiteral)
        {
            var literal = anchors.CaptureLiteral(Read<nint>(frame, call.Arguments[first]));
            Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), literal);
            return;
        }
        if (op == HpirPathOp.Qualify)
        {
            CallPathQualification(program, ref heap, active, diagnostics, counts, pc, call, frame, rightText, anchors, options);
            return;
        }
        HpirPathValue left = PathOperand(frame, call.Arguments[first], leftText, anchors);
        if (op == HpirPathOp.Canonical)
        {
            work ??= new(token);
            var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
            var query = work.SubmitFileMetadata<NativePathValue>(HpirFileMetadata.Canonical, left.ToHost(contextual: true),
                options, anchors.Binding(program.Context), site, NativeBuiltins.ReadCanonicalPath);
            var resolved = query.Ready ? query.Value : work.Demand<NativePathValue>(query.JobId, token, trace: null);
            if (left.Local) left.Directory = anchors.Directory;
            StorePathProjection(program, ref heap, active, pc, diagnostics, counts, ref left, resolved.Raw, resolved.LocalSpelling);
            Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), left);
            return;
        }
        if (op == HpirPathOp.SetWorkingDirectory)
        {
            var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
            try
            {
                nint changed = anchors.Authority!.Change(left.ToHost(contextual: true), site, publish: true);
                Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), anchors.Capture(changed));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or MsyncException { Span: null })
            {
                throw new MsyncException(error.Message, site.Source, error);
            }
            return;
        }
        if (op is HpirPathOp.Equal or HpirPathOp.NotEqual)
        {
            // Language equality compares effective spelling, not resolved resource identity.
            nint a = left.NativeText, b = PathOperand(frame, call.Arguments[1], rightText, anchors).NativeText;
            bool equal = a == b || HpirTextStorage.Read(a).SequenceEqual(HpirTextStorage.Read(b));
            WriteBool(frame, destination, equal == (op == HpirPathOp.Equal));
            return;
        }
        if (op == HpirPathOp.FromText)
        {
            Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), left);
            return;
        }
        if (returnsPath)
        {
            HpirPathValue right = HpirPathImports.OperandCount(op) == 2
                ? PathOperand(frame, call.Arguments[first + 1], rightText, anchors) : default;
            var value = NativeBuiltins.PathLexicalValue(op, left.ToHost(),
                HpirPathImports.OperandCount(op) == 2 ? right.ToHost() : default);
            StorePathProjection(program, ref heap, active, pc, diagnostics, counts, ref left, value.Raw, value.LocalSpelling);
            Unsafe.WriteUnaligned((byte*)Read<nint>(frame, call.Arguments[0]), left);
            return;
        }
        if (HpirPathImports.ReturnsText(op))
        {
            nint text = op == HpirPathOp.NativeString || op == HpirPathOp.Display && left.Local ? left.NativeText
                : StorePathText(program, ref heap, active, pc, diagnostics, counts,
                    NativeBuiltins.PathLexicalString(op, left.ToHost()), left.Raw);
            Write(frame, destination, text);
            return;
        }
        WriteBool(frame, destination, NativeBuiltins.PathLexicalPredicate(op, left.ToHost()));
    }

    private static HpirPathValue PathOperand(byte* frame, int operand, bool text, HpirPathAnchors anchors)
    {
        nint data = Read<nint>(frame, operand);
        return text ? anchors.Capture(data) : Unsafe.ReadUnaligned<HpirPathValue>((byte*)data);
    }

    private static nint StorePathText(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active, int pc,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, string result, nint previous)
    {
        if (HpirTextStorage.Read(previous).SequenceEqual(result)) return previous;
        nint text = Allocate(program, ref heap, active, pc, program.TextLayout, HpirTextStorage.Elements(result.Length),
            diagnostics, counts, clear: false);
        HpirTextStorage.Write(text, result);
        return text;
    }

    private static void StorePathProjection(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active, int pc,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, ref HpirPathValue path, string raw, string? spelling)
    {
        bool replaceRaw = !HpirTextStorage.Read(path.Raw).SequenceEqual(raw);
        bool replaceSpelling = spelling is not null && (path.Spelling == 0
            || !HpirTextStorage.Read(path.Spelling).SequenceEqual(spelling));
        int rawElements = replaceRaw ? HpirTextStorage.Elements(raw.Length) : 0;
        // Both projected strings are published together. No collection can
        // encounter a partly materialized caller result.
        rawElements = checked((rawElements + 1) & ~1);
        int spellingElements = replaceSpelling ? HpirTextStorage.Elements(spelling!.Length) : 0;
        int elements = checked(rawElements + spellingElements);
        if (elements != 0)
        {
            nint buffer = Allocate(program, ref heap, active, pc, program.TextLayout, elements, diagnostics, counts, clear: false);
            if (replaceRaw)
            {
                HpirTextStorage.Write(buffer, raw);
                path.Raw = buffer;
            }
            if (replaceSpelling)
            {
                path.Spelling = buffer + rawElements * HpirScalarImports.TextElementBytes;
                HpirTextStorage.Write(path.Spelling, spelling);
            }
        }
        if (spelling is null) path.Spelling = 0;
    }
}
