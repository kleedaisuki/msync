using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly NativeLayout RegexMatchLayout = NativeBuiltinLayouts.RegexMatch.Layout;
    private static readonly NativeLayout RegexGroupLayout = NativeBuiltinLayouts.RegexGroup.Layout;

    private static NativeValue RegexOperation(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context)
    {
        string text = Text(args[0]);
        if (operation == "escape") return NativeValue.FromString(Regex.Escape(text));
        string pattern = Text(args[1]);
        try
        {
            return operation switch
            {
                "is_match" => Bool(Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant)),
                "find" => RegexFind(text, pattern, context),
                "matches" => NativeValue.FromRange(new(RegexMatches(text, Regex.Matches(text, pattern, RegexOptions.CultureInvariant), context), TypeRef.Scope)),
                "replace" => NativeValue.FromString(Regex.Replace(text, pattern, Text(args[2]), RegexOptions.CultureInvariant)),
                "split" => StringSlices(Regex.Split(text, pattern, RegexOptions.CultureInvariant).Select(value => new TextSlice(value))),
                _ => throw new MsyncException($"Unknown regex native '{operation}'")
            };
        }
        catch (ArgumentException error) { throw new MsyncException($"regex.{operation}: {error.Message}"); }
    }

    private static NativeValue RegexFind(string text, string pattern, NativeRuntimeContext context)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success ? RegexMatchRecord(text, match, ScalarLength(text.AsSpan(0, match.Index)), context) : NativeValue.Null;
    }

    private static async IAsyncEnumerable<NativeValue> RegexMatches(string text, MatchCollection matches, NativeRuntimeContext context)
    {
        int offset = 0, index = 0;
        foreach (Match match in matches)
        {
            index += ScalarLength(text.AsSpan(offset, match.Index - offset));
            offset = match.Index;
            yield return RegexMatchRecord(text, match, index, context);
        }
        await Task.CompletedTask;
    }

    private static NativeValue RegexMatchRecord(string text, Match match, int index, NativeRuntimeContext context) => Record(context, RegexMatchLayout,
        NativeValue.FromString(match.Value), NativeValue.FromNumber(index), NativeValue.FromNumber(ScalarLength(match.Value)),
        NativeValue.FromRange(context.Snapshot(match.Groups.Cast<Group>()
            .Select(group => RegexGroupRecord(text, match.Index, index, group, context)), TypeRef.Scope)));

    private static NativeValue RegexGroupRecord(string text, int matchOffset, int matchIndex, Group group, NativeRuntimeContext context) => Record(context, RegexGroupLayout,
        NativeValue.FromString(group.Name), NativeValue.FromString(group.Value),
        NativeValue.FromNumber(group.Success ? group.Index >= matchOffset
            ? matchIndex + ScalarLength(text.AsSpan(matchOffset, group.Index - matchOffset))
            : ScalarLength(text.AsSpan(0, group.Index)) : -1),
        NativeValue.FromNumber(ScalarLength(group.Value)), Bool(group.Success));
}
