using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static readonly ScopeLayout RegexMatchLayout = new(["value", "index", "length", "groups"]);
    private static readonly ScopeLayout RegexGroupLayout = new(["name", "value", "index", "length", "success"]);

    private static Value RegexOperation(string operation, IReadOnlyList<Value> args)
    {
        string text = Text(args[0]);
        if (operation == "escape") return new StringValue(Regex.Escape(text));
        string pattern = Text(args[1]);
        try
        {
            return operation switch
            {
                "is_match" => Bool(Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant)),
                "find" => RegexFind(text, pattern),
                "matches" => new RangeValue(RegexMatches(text, Regex.Matches(text, pattern, RegexOptions.CultureInvariant)), TypeRef.Scope),
                "replace" => new StringValue(Regex.Replace(text, pattern, Text(args[2]), RegexOptions.CultureInvariant)),
                "split" => new RangeValue(Regex.Split(text, pattern, RegexOptions.CultureInvariant)
                    .Select(value => (Value)new StringValue(value)), TypeRef.String),
                _ => throw new MsyncException($"Unknown regex native '{operation}'")
            };
        }
        catch (ArgumentException exception)
        {
            throw new MsyncException($"regex.{operation}: {exception.Message}");
        }
    }

    private static Value RegexFind(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success ? RegexMatchRecord(text, match, ScalarLength(text.AsSpan(0, match.Index))) : Value.Null;
    }

    private static IEnumerable<Value> RegexMatches(string text, MatchCollection matches)
    {
        int offset = 0, index = 0;
        foreach (Match match in matches)
        {
            index += ScalarLength(text.AsSpan(offset, match.Index - offset));
            offset = match.Index;
            yield return RegexMatchRecord(text, match, index);
        }
    }

    private static ScopeValue RegexMatchRecord(string text, Match match, int index) => new(RegexMatchLayout,
        [
            new StringValue(match.Value),
            new NumberValue(index),
            new NumberValue(ScalarLength(match.Value)),
            new RangeValue(match.Groups.Cast<Group>()
                .Select(group => (Value)RegexGroupRecord(text, match.Index, index, group)).ToArray(), TypeRef.Scope)
        ]);

    private static ScopeValue RegexGroupRecord(string text, int matchOffset, int matchIndex, Group group) => new(RegexGroupLayout,
        [
            new StringValue(group.Name),
            new StringValue(group.Value),
            new NumberValue(group.Success ? group.Index >= matchOffset
                ? matchIndex + ScalarLength(text.AsSpan(matchOffset, group.Index - matchOffset))
                : ScalarLength(text.AsSpan(0, group.Index)) : -1),
            new NumberValue(ScalarLength(group.Value)),
            Bool(group.Success)
        ]);
}
