using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Hpir;
using static Msync.Core.Hpir.HpirRegexResultImports;
using static Msync.Runtime.ScalarText;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallRegexFind(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> arguments, byte* frame, int destination)
    {
        nint input = Read<nint>(frame, arguments[0]);
        bool literal = (import.Binding & 1) != 0;
        string text = CopyText(input);
        string pattern = literal ? program.TextLiterals[Read<int>(frame, arguments[1])] : CopyText(Read<nint>(frame, arguments[1]));
        using RuntimeTimelineScope? trace = diagnostics.BeginRegexFind(active->Function, pc, literal, text.Length, pattern.Length);
        try
        {
            Match match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            trace?.SetMetric("regex.matched", match.Success ? 1 : 0);
            if (!match.Success)
            {
                Write(frame, destination, (nint)0);
                return;
            }

            int groupLayout = Read<int>(frame, arguments[2]);
            int vectorLayout = Read<int>(frame, arguments[3]);
            int pointerLayout = Read<int>(frame, arguments[4]);
            nint result = Allocate(program, ref heap, active, pc, import.Binding >> 1, 1, diagnostics, counts, clear: false);
            // This host call has one guest safepoint, before its result exists.
            // The completely initialized record graph is published together.
            var publish = new RegexMatchPublisher(program, heap!, active, diagnostics, counts, pc);
            publish.Store(result, input, text, match, groupLayout, vectorLayout, pointerLayout);
            trace?.SetMetric("regex.groups", match.Groups.Count);
            Write(frame, destination, result);
        }
        catch (ArgumentException error)
        {
            trace?.Fail(error);
            throw new MsyncException($"regex.find: {error.Message}", diagnostics.Source(active->Function, pc), error);
        }
    }

    private ref struct RegexMatchPublisher(HpirProgram program, HpirHeap heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc)
    {
        private nint empty;

        internal void Store(nint result, nint input, ReadOnlySpan<char> text, Match match,
            int groupLayout, int vectorLayout, int pointerLayout)
        {
            int count = match.Groups.Count, index = ScalarLength(text[..match.Index]);
            int length = ScalarLength(match.ValueSpan);
            nint value = match.Length == text.Length ? input : Text(match.ValueSpan);
            nint data = Reserve(pointerLayout, count);
            nint vector = Reserve(vectorLayout);
            Put(vector, 0, (ulong)count);
            Put(vector, HpirTransformImports.VectorData, data);
            int ordinal = 0;
            foreach (Group group in match.Groups)
            {
                nint item = Reserve(groupLayout);
                Put(data, ordinal * sizeof(nint), item);
                StoreGroup(item, group, text, match.Index, index,
                    ordinal == 0 ? value : Text(group.ValueSpan), ordinal == 0 ? length : ScalarLength(group.ValueSpan));
                ++ordinal;
            }
            Put(result, MatchValue, value);
            Put(result, MatchIndex, (double)index);
            Put(result, MatchLength, (double)length);
            Put(result, MatchGroups, vector);
        }

        private void StoreGroup(nint item, Group group, ReadOnlySpan<char> text, int matchOffset, int matchIndex, nint value, int length)
        {
            int index = !group.Success ? -1 : group.Index < matchOffset ? ScalarLength(text[..group.Index])
                : matchIndex + ScalarLength(text[matchOffset..group.Index]);
            Put(item, GroupName, Text(group.Name));
            Put(item, GroupValue, value);
            Put(item, GroupIndex, (double)index);
            Put(item, GroupLength, (double)length);
            Put(item, GroupSuccess, group.Success ? (byte)1 : (byte)0);
        }

        private nint Text(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty && empty != 0) return empty;
            nint value = Reserve(program.TextLayout, HpirTextStorage.Elements(text.Length));
            HpirTextStorage.Write(value, text);
            if (text.IsEmpty) empty = value;
            return value;
        }

        private nint Reserve(int layout, int count = 1) =>
            AllocatePayload(program, heap, active, pc, layout, count, diagnostics, counts, clear: false);

        private static void Put<T>(nint address, int offset, T value) where T : unmanaged =>
            Unsafe.WriteUnaligned((byte*)address + offset, value);
    }
}
