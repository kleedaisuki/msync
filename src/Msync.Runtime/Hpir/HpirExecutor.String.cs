using System.Text;
using Msync.Core;
using Msync.Core.Hpir;
using static Msync.Runtime.ScalarText;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallString(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirStringOp operation,
        IReadOnlyList<int> arguments, byte* frame, int destination, CancellationToken token)
    {
        try
        {
            if (operation is HpirStringOp.UniqueSnapshot or HpirStringOp.SortSnapshot)
            {
                Write(frame, destination, OrderTextSnapshot(operation, Read<nint>(frame, arguments[0]),
                    Read<nint>(frame, arguments[1]), checked((int)Read<ulong>(frame, arguments[2])), token));
                return;
            }
            if (operation == HpirStringOp.JoinSnapshot)
            {
                var join = new TextAllocation(program, ref heap, active, diagnostics, counts, pc);
                nint result = JoinTextSnapshot(Read<nint>(frame, arguments[0]), checked((int)Read<ulong>(frame, arguments[1])),
                    Read<nint>(frame, arguments[2]), ref join, token);
                Write(frame, destination, result);
                return;
            }
            nint first = Read<nint>(frame, arguments[0]);
            ReadOnlySpan<char> text = HpirTextStorage.Read(first);
            if (!HpirStringImports.ProducesText(operation))
            {
                QueryString(operation, text, arguments, frame, destination);
                return;
            }
            var allocation = new TextAllocation(program, ref heap, active, diagnostics, counts, pc);
            Write(frame, destination, BuildString(operation, first, text, arguments, frame, ref allocation));
        }
        catch (MsyncException error) when (error.Span is null)
        {
            throw new MsyncException(error.Message, diagnostics.Source(active->Function, pc), error);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new MsyncException($"{HpirStringImports.Name(operation)}: {error.Message}",
                diagnostics.Source(active->Function, pc), error);
        }
    }

    private static void QueryString(HpirStringOp operation, ReadOnlySpan<char> text, IReadOnlyList<int> arguments,
        byte* frame, int destination)
    {
        switch (operation)
        {
            case HpirStringOp.Length: Write(frame, destination, (double)ScalarLength(text)); return;
            case HpirStringOp.Utf8Size: Write(frame, destination, (double)Encoding.UTF8.GetByteCount(text)); return;
            case HpirStringOp.LineFeedCount: Write(frame, destination, (double)text.Count('\n')); return;
            case HpirStringOp.WordCount: Write(frame, destination, (double)WordCount(text)); return;
            case HpirStringOp.IsAscii: WriteBool(frame, destination, Ascii.IsValid(text)); return;
            case HpirStringOp.Empty: WriteBool(frame, destination, text.IsEmpty); return;
        }

        ReadOnlySpan<char> other = HpirTextStorage.Read(Read<nint>(frame, arguments[1]));
        if (operation == HpirStringOp.Compare)
        {
            Write(frame, destination, (double)Math.Sign(TextSemantics.CompareOrdinal(text, other)));
            return;
        }
        bool value = operation switch
        {
            HpirStringOp.Contains => text.Contains(other, StringComparison.Ordinal),
            HpirStringOp.StartsWith => text.StartsWith(other, StringComparison.Ordinal),
            HpirStringOp.EndsWith => text.EndsWith(other, StringComparison.Ordinal),
            HpirStringOp.Equal => text.SequenceEqual(other),
            HpirStringOp.NotEqual => !text.SequenceEqual(other),
            HpirStringOp.Less => TextSemantics.CompareOrdinal(text, other) < 0,
            HpirStringOp.LessEqual => TextSemantics.CompareOrdinal(text, other) <= 0,
            HpirStringOp.Greater => TextSemantics.CompareOrdinal(text, other) > 0,
            HpirStringOp.GreaterEqual => TextSemantics.CompareOrdinal(text, other) >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        WriteBool(frame, destination, value);
    }

    private static nint BuildString(HpirStringOp operation, nint first, ReadOnlySpan<char> text,
        IReadOnlyList<int> arguments, byte* frame, ref TextAllocation allocation)
    {
        switch (operation)
        {
            case HpirStringOp.Trim: return allocation.Slice(first, text.Length, text.Trim());
            case HpirStringOp.TrimStart: return allocation.Slice(first, text.Length, text.TrimStart());
            case HpirStringOp.TrimEnd: return allocation.Slice(first, text.Length, text.TrimEnd());
            case HpirStringOp.ToLower: return MapCase(first, text, ref allocation, upper: false);
            case HpirStringOp.ToUpper: return MapCase(first, text, ref allocation, upper: true);
            case HpirStringOp.Concat:
                nint other = Read<nint>(frame, arguments[1]);
                ReadOnlySpan<char> suffix = HpirTextStorage.Read(other);
                if (text.IsEmpty) return allocation.Reuse(other);
                if (suffix.IsEmpty) return allocation.Reuse(first);
                nint joined = allocation.Reserve(checked(text.Length + suffix.Length), out Span<char> buffer);
                text.CopyTo(buffer);
                suffix.CopyTo(buffer[text.Length..]);
                return joined;
            case HpirStringOp.Replace:
                ReadOnlySpan<char> search = HpirTextStorage.Read(Read<nint>(frame, arguments[1]));
                ReadOnlySpan<char> replacement = HpirTextStorage.Read(Read<nint>(frame, arguments[2]));
                int size = ReplacementLength(text, search, replacement.Length, out bool matches);
                if (!matches) return allocation.Reuse(first);
                nint replaced = allocation.Reserve(size, out Span<char> changed);
                ScalarText.Replace(text, search, replacement, changed);
                return replaced;
            case HpirStringOp.CharAt:
                return allocation.Slice(first, text.Length,
                    CharacterAt(text, Natural(Read<double>(frame, arguments[1]), "string.char_at index")));
            case HpirStringOp.Substring:
                return allocation.Slice(first, text.Length, Substring(text,
                    Natural(Read<double>(frame, arguments[1]), "string.substring start"),
                    Natural(Read<double>(frame, arguments[2]), "string.substring count")));
            case HpirStringOp.Repeat:
                int length = RepeatLength(text.Length, Natural(Read<double>(frame, arguments[1]), "string.repeat count"));
                if (length == text.Length) return allocation.Reuse(first);
                nint repeated = allocation.Reserve(length, out Span<char> result);
                for (int offset = 0; offset < length; offset += text.Length) text.CopyTo(result[offset..]);
                return repeated;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static nint MapCase(nint first, ReadOnlySpan<char> text, ref TextAllocation allocation, bool upper)
    {
        bool ascii = Ascii.IsValid(text);
        allocation.Case(ascii);
        if (ascii)
        {
            if (!(upper ? text.ContainsAnyInRange('a', 'z') : text.ContainsAnyInRange('A', 'Z')))
                return allocation.Reuse(first);
            nint folded = allocation.Reserve(text.Length, out Span<char> destination);
            _ = upper ? Ascii.ToUpper(text, destination, out _) : Ascii.ToLower(text, destination, out _);
            return folded;
        }

        nint unicode = allocation.Reserve(UnicodeCasing.ChangeSpan(text, default, upper), out Span<char> mapped);
        UnicodeCasing.ChangeSpan(text, mapped, upper);
        return unicode;
    }

    private ref struct TextAllocation
    {
        private readonly HpirProgram program;
        private ref HpirHeap? heap;
        private readonly HpirStack.Frame* active;
        private readonly HpirDiagnostics diagnostics;
        private readonly RuntimeProfile.HpirFunctionCensus? counts;
        private readonly int pc;

        internal TextAllocation(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
            HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc)
        {
            this.program = program;
            this.heap = ref heap;
            this.active = active;
            this.diagnostics = diagnostics;
            this.counts = counts;
            this.pc = pc;
        }

        internal nint Reserve(int chars, out Span<char> destination)
        {
            nint text = Allocate(program, ref heap, active, pc, program.TextLayout, HpirTextStorage.Elements(chars),
                diagnostics, counts, clear: false);
            destination = HpirTextStorage.Initialize(text, chars);
            return text;
        }

        internal nint Reuse(nint text)
        {
            counts?.StringReuse();
            return text;
        }

        internal void Case(bool ascii) => counts?.StringCase(ascii);

        internal void Join(int items, int chars) => counts?.StringJoin(items, chars);

        internal nint Slice(nint original, int length, ReadOnlySpan<char> span)
        {
            if (span.Length == length) return Reuse(original);
            nint text = Reserve(span.Length, out Span<char> destination);
            span.CopyTo(destination);
            return text;
        }
    }
}
