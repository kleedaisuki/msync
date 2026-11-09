using System.Text;
using Msync.Core;

namespace Msync.Cli;

internal enum SourceCompletionKind { Path, Ssh }
internal sealed record SourceCompletionContext(int Start, int Length, string Prefix, bool Quoted,
    string Qualifier, SourceCompletionKind Kind, ConnectionValue? Endpoint = null, PathValue? LookupBase = null,
    bool InsideString = false, bool CloseQuote = false, bool FollowingSeparator = false, bool PreserveQuote = false,
    bool LiteralWindowsAtom = false, IReadOnlySet<string>? BoundNames = null);
internal sealed record SourceCompletionItem(string Label, string Replacement, string Detail, bool IsDirectory = false, string? FilterText = null);

internal static class SourceCompletion
{
    private static readonly string[] WordBoundaries = ["and", "or", "is", "not", "in", "else", "if", "fn", "scope", "let", "match", "for", "exists", "return", "break", "loop", "native", "concept", "type", "as"];

    public static SourceCompletionContext? Find(string text, int caret, bool includeBarePaths = false,
        PathValue? receiverPath = null, bool unknownPathReceiver = false, ConnectionValue? physicalEndpoint = null, bool unknownPhysicalOwner = false, IReadOnlySet<string>? boundNames = null, string? sourceFile = null)
    {
        int start = 0;
        bool quoted = false, comment = false, regex = false, escaped = false, characterClass = false, namedAngle = false;
        for (int index = 0; index < caret; index++)
        {
            char character = text[index];
            if (comment)
            {
                if (character is '\r' or '\n') { comment = false; start = index + 1; }
                continue;
            }
            if (quoted)
            {
                if (character != '"') continue;
                if (index + 1 < caret && text[index + 1] == '"') index++;
                else quoted = false;
                continue;
            }
            if (regex)
            {
                if (escaped) { escaped = false; continue; }
                if (character == '\\')
                {
                    if (!characterClass && index + 2 < text.Length && text[index + 1] == 'k' && text[index + 2] == '<')
                    { namedAngle = true; index += 2; continue; }
                    escaped = true;
                    continue;
                }
                if (character == '[') characterClass = true;
                if (character == ']') characterClass = false;
                if (character == '(' && !characterClass && index + 3 < text.Length && text[index + 1] == '?' && text[index + 2] == '<'
                    && text[index + 3] is not ('=' or '!')) { namedAngle = true; index += 2; continue; }
                if (character != '>' || characterClass) continue;
                if (namedAngle) { namedAngle = false; continue; }
                if (RegexBoundary(text, index + 1)) { regex = false; start = index + 1; }
                continue;
            }
            if (character == '"') { quoted = true; continue; }
            if ((character == '#' || character == '/' && index + 1 < text.Length && text[index + 1] == '/')
                && (index == 0 || char.IsWhiteSpace(text[index - 1])))
            { comment = true; continue; }
            if (character == '<' && RegexStart(text, index))
            { regex = true; continue; }
            if (character is ' ' or '\t' && PathFragment(text.AsSpan(start, index - start))) continue;
            if (index > start && char.IsWhiteSpace(text[index - 1]) && PathFragment(text.AsSpan(start, index - start)))
            {
                int length = WordBoundary(text.AsSpan(index));
                if (length != 0) { start = index + length; index += length - 1; continue; }
            }
            bool uri = text.AsSpan(start, index - start).StartsWith("ssh://", StringComparison.Ordinal)
                || text.AsSpan(start, index - start).StartsWith("wsl://", StringComparison.Ordinal);
            if (character == ':' && !uri && !text.AsSpan(index).StartsWith("://", StringComparison.Ordinal)
                && (index == 0 || text[index - 1] != ':') && (index + 1 == text.Length || text[index + 1] != ':')
                && !(index == start + 1 && char.IsLetter(text[start]) && index + 1 < text.Length && text[index + 1] is '/' or '\\'))
            { start = index + 1; continue; }
            if (Boundary(character) && !(uri && character is '[' or ']' or '@')) start = index + 1;
        }
        if (comment || regex) return null;
        string fragment = text[start..caret];
        int end = TokenEnd(text, caret, quoted, fragment.StartsWith("ssh://", StringComparison.Ordinal) || fragment.StartsWith("wsl://", StringComparison.Ordinal));
        if (fragment.StartsWith("ssh://", StringComparison.Ordinal) && LastSelection(fragment) < 0)
            return HostEdit(new(start, end - start, fragment, false, "", SourceCompletionKind.Ssh), text, caret);
        if (unknownPhysicalOwner || physicalEndpoint is not null && !PhysicalDirectory(physicalEndpoint, sourceFile)) return null;
        int pathJoin = LastPathJoin(fragment);
        if (pathJoin >= 0 && receiverPath is not null)
        {
            if (physicalEndpoint is not null) receiverPath = PathSemantics.Qualify(receiverPath, physicalEndpoint);
            if (receiverPath.Connection is { Kind: ConnectionKind.Local, Address: "file" })
            {
                if (sourceFile is null) return null;
                receiverPath = receiverPath with { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFile)) };
            }
            return PathEdit(new(start, end - start, Decode(fragment[(pathJoin + 1)..]), false, "", SourceCompletionKind.Path,
                receiverPath.Connection, receiverPath, BoundNames: boundNames), text, caret);
        }
        if (pathJoin >= 0 && unknownPathReceiver) return null;
        if (LastSelection(fragment) >= 0) return null;
        string qualifier = "";
        ConnectionValue? endpoint = physicalEndpoint;
        string value = fragment;
        bool isString = value.StartsWith('"');
        string raw = Decode(value);
        bool path = endpoint is not null || raw.Contains('/') || OperatingSystem.IsWindows() && raw.Contains('\\')
            || raw is "~" or "." or "..";
        if (!path && !(includeBarePaths && raw.Length > 0 && !raw.StartsWith('$'))) return null;
        PathValue? lookupBase = endpoint is { Kind: ConnectionKind.Local, Address: "file" }
            ? new PathValue(Path.GetDirectoryName(Path.GetFullPath(sourceFile!))!) : null;
        return PathEdit(new(start, end - start, raw, isString, qualifier, SourceCompletionKind.Path, endpoint,
            LookupBase: lookupBase, BoundNames: boundNames), text, caret);
    }

    public static IReadOnlyList<SourceCompletionItem> Complete(SourceCompletionContext context, string workingDirectory, string homeDirectory)
    {
        if (context.Kind == SourceCompletionKind.Ssh) return Hosts(context, homeDirectory);
        string prefix = context.Prefix;
        int separator = LastSeparator(prefix);
        string parent = separator < 0 ? "" : prefix[..(separator + 1)];
        string leaf = prefix[(separator + 1)..];
        if (prefix == "~" && context.LookupBase is null) { parent = "~/"; leaf = ""; }
        string directory;
        if (context.LookupBase is { } lookup)
        {
            var path = parent.Length == 0 ? lookup : PathSemantics.JoinValue(lookup, new StringValue(parent));
            directory = path.LocalPath(workingDirectory, homeDirectory);
        }
        else
        {
            directory = parent.Length == 0 ? workingDirectory : ExpandHome(parent, homeDirectory);
            if (!Path.IsPathRooted(directory)) directory = Path.Combine(workingDirectory, directory);
        }
        if (OperatingSystem.IsWindows())
        {
            // An incomplete UNC authority has no directory to enumerate yet.
            if (IncompleteUnc(directory)) return [];
            if (!Path.IsPathFullyQualified(directory)) directory = Path.GetFullPath(directory, context.LookupBase?.WorkingDirectory ?? workingDirectory);
        }
        char slash = separator >= 0 ? prefix[separator] : context.LookupBase is not null ? '/' : Path.DirectorySeparatorChar;
        List<SourceCompletionItem> items = [];
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!entry.Name.StartsWith(leaf, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                string replacement = ItemText(context, entry.Name, isDirectory, slash);
                string type = context.Quoted && context.Qualifier.Length == 0 && context.Endpoint is null ? "String" : "Path";
                items.Add(new(entry.Name + (isDirectory ? slash : ""), replacement,
                    $"{type} · {(isDirectory ? "directory" : "file")}", isDirectory, replacement));
            }
        }
        catch (DirectoryNotFoundException) { return []; }
        items.Sort((left, right) => TextSemantics.Ordinal.Compare(left.Label, right.Label));
        return items;
    }

    private static bool PhysicalDirectory(ConnectionValue endpoint, string? sourceFile) => endpoint.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
        || endpoint is { Kind: ConnectionKind.Local, Address: not "file" } || endpoint.Kind == ConnectionKind.Local && sourceFile is not null;

    private static IReadOnlyList<SourceCompletionItem> Hosts(SourceCompletionContext context, string homeDirectory)
    {
        string authority = context.Prefix[6..];
        if (authority.Contains('/')) return [];
        int at = authority.LastIndexOf('@');
        string user = at < 0 ? "" : authority[..(at + 1)];
        string hostPrefix = authority[(at + 1)..];
        return SshHostCatalog.Read(homeDirectory)
            .Where(host => host.Host.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(host => new SourceCompletionItem(host.Host, host.Host,
                $"Connection · SSH host ({host.Origin})"))
            .OrderBy(item => item.Label, TextSemantics.Ordinal).ToArray();
    }

    private static int LastSelection(string text)
    {
        int depth = 0, result = -1;
        bool quoted = false;
        for (int index = 0; index + 1 < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (quoted && text[index + 1] == '"') index++;
                else quoted = !quoted;
                continue;
            }
            if (quoted) continue;
            if (text[index] == '[') depth++;
            if (text[index] == ']') depth--;
            if (depth == 0 && text[index] == ':' && text[index + 1] == ':') { result = index; index++; }
        }
        return result;
    }

    private static int LastPathJoin(string text)
    {
        int result = -1;
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            else if (!quoted && text[index] == '/') result = index;
        }
        return result;
    }

    private static bool RegexStart(string text, int index)
    {
        int previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
        return previous < 0 || text[previous] is '/' or '{' or ',' or '['
            || text[previous] == '>' && previous > 0 && text[previous - 1] == '=';
    }

    private static bool PathFragment(ReadOnlySpan<char> text)
    {
        bool uri = text.StartsWith("ssh://", StringComparison.Ordinal) || text.StartsWith("wsl://", StringComparison.Ordinal);
        bool qualified = text.LastIndexOf("::".AsSpan()) > text.LastIndexOf(']');
        return (!uri || qualified) && (text.Contains('/') || OperatingSystem.IsWindows() && text.Contains('\\'));
    }

    private static int WordBoundary(ReadOnlySpan<char> text)
    {
        foreach (string word in WordBoundaries)
            if (text.StartsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]) && text[word.Length] != '_'))
                return word.Length;
        return 0;
    }

    private static bool RegexBoundary(string text, int next) => next == text.Length || char.IsWhiteSpace(text[next])
        || "/,;}|)".Contains(text[next], StringComparison.Ordinal)
        || text.AsSpan(next).StartsWith("=>", StringComparison.Ordinal) || text.AsSpan(next).StartsWith("->", StringComparison.Ordinal);

    private static int TokenEnd(string text, int caret, bool quoted, bool uri)
    {
        int end = caret;
        for (; end < text.Length; end++)
        {
            char character = text[end];
            if (character == '"')
            {
                if (quoted && end + 1 < text.Length && text[end + 1] == '"') { end++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && Boundary(character) && !(uri && character is '[' or ']' or '@')) break;
        }
        return end;
    }

    private static bool Boundary(char character) => char.IsWhiteSpace(character) || "{}()[],;=|<>?@".Contains(character, StringComparison.Ordinal);
    internal static int LastSeparator(string path) => OperatingSystem.IsWindows() ? path.LastIndexOfAny(['/', '\\']) : path.LastIndexOf('/');
    private static string ExpandHome(string path, string home) => path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..]) : path;
    internal static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Decode(string text)
    {
        StringBuilder result = new();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '"') { result.Append(text[index]); continue; }
            if (quoted && index + 1 < text.Length && text[index + 1] == '"') { result.Append('"'); index++; }
            else quoted = !quoted;
        }
        return result.ToString();
    }

    internal static string ItemText(SourceCompletionContext context, string name, bool directory, char separator)
    {
        string suffix = directory && !context.FollowingSeparator ? separator.ToString() : "";
        if (context.PreserveQuote) return Quote(name + suffix);
        if (context.InsideString)
            return name.Replace("\"", "\"\"", StringComparison.Ordinal) + suffix + (context.CloseQuote ? "\"" : "");
        bool binding = !context.LiteralWindowsAtom && context.BoundNames?.Contains(name) == true;
        bool numericAtom = LastSeparator(context.Prefix) < 0 && (NumberSyntax.TryParse(name, out _)
            || name.StartsWith('-') && NumberSyntax.TryParse(name[1..], out _));
        return (NeedsQuotes(name) || binding || numericAtom ? Quote(name) : name) + suffix;
    }

    private static bool NeedsQuotes(string name) => name.Any(character => char.IsWhiteSpace(character)
        || "{}()[],;?\"$|<>!=:+*%@".Contains(character, StringComparison.Ordinal))
        || name is "true" or "false" or "null" or "fn" or "scope" or "if" or "else" or "match" or "for"
            or "in" or "is" or "not" or "and" or "or" or "exists" or "decltype" or "return" or "break" or "loop" or "native" or "concept" or "type" or "as"
            or "Bool" or "Range" or "String" or "Function" or "Scope" or "Path" or "Process" or "Connection" or "Null" or "Type" or "Number";

    private static SourceCompletionContext HostEdit(SourceCompletionContext context, string text, int caret)
    {
        int start = context.Start + 6;
        int at = text.LastIndexOf('@', caret - 1, caret - start);
        if (at >= start) start = at + 1;
        int end = start, brackets = 0;
        while (end < text.Length && (!Boundary(text[end]) || text[end] is '[' or ']'))
        {
            if (text[end] == '[') brackets++;
            if (text[end] == ']') brackets--;
            if (brackets == 0 && text[end] is ':' or '/') break;
            end++;
        }
        return context with { Start = start, Length = end - start };
    }

    private static SourceCompletionContext PathEdit(SourceCompletionContext context, string text, int caret)
    {
        int start = context.Start;
        int valueStart = start + context.Qualifier.Length;
        bool windows = OperatingSystem.IsWindows() && context.Endpoint?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl);
        bool Separator(char character) => character == '/' || windows && character == '\\';
        int quote = -1, lastSeparator = valueStart - 1;
        bool quoted = false;
        for (int index = valueStart; index < caret; index++)
        {
            if (text[index] == '\"')
            {
                if (quoted && index + 1 < caret && text[index + 1] == '\"') { index++; continue; }
                quoted = !quoted;
                if (quoted) quote = index;
            }
            else if (Separator(text[index])) lastSeparator = index;
        }
        bool inside = quote >= valueStart && (quoted || caret > quote && text[caret - 1] == '\"');
        start = Math.Max(lastSeparator + 1, inside ? quote + 1 : valueStart);
        int end = Math.Max(start, caret);
        bool close = false;
        if (inside && !quoted) { end = caret; close = true; }
        else
        {
            while (end < text.Length)
            {
                if (Separator(text[end])) break;
                if (inside && text[end] == '\"')
                {
                    if (end + 1 < text.Length && text[end + 1] == '\"') { end += 2; continue; }
                    end++; close = true; break;
                }
                if (!inside && Boundary(text[end])) break;
                end++;
            }
            if (inside && !close && end == text.Length) close = true;
        }
        bool following = end < text.Length && Separator(text[end]);
        bool preserveQuote = inside && close && quote > lastSeparator;
        if (preserveQuote) start = quote;
        return context with
        {
            Start = start,
            Length = end - start,
            InsideString = inside && !preserveQuote,
            CloseQuote = close,
            FollowingSeparator = following,
            PreserveQuote = preserveQuote,
            LiteralWindowsAtom = text.AsSpan(valueStart, Math.Max(0, caret - valueStart)).StartsWith("\\", StringComparison.Ordinal)
                || caret - valueStart >= 3 && char.IsLetter(text[valueStart]) && text[valueStart + 1] == ':' && text[valueStart + 2] is '/' or '\\'
        };
    }

    private static bool IncompleteUnc(string directory)
    {
        if (directory.Length < 2 || directory[0] is not ('/' or '\\') || directory[1] is not ('/' or '\\')) return false;
        string tail = directory[2..];
        if (tail.StartsWith("?\\", StringComparison.Ordinal) || tail.StartsWith(".\\", StringComparison.Ordinal))
        {
            tail = tail[2..];
            if (tail.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) tail = tail[4..];
            else return tail.Length < 3 || tail[1] != ':';
        }
        var components = tail.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return components.Length < 2;
    }

}

internal sealed class SourceCompletionService(Func<SshPrompt, CancellationToken, Task<string?>>? prompt = null) : IAsyncDisposable
{
    private readonly RemoteDirectoryCompletion remote = new(prompt);

    public async Task<IReadOnlyList<SourceCompletionItem>> CompleteAsync(SourceCompletionContext context,
        string workingDirectory, string homeDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Endpoint is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } endpoint)
            return SourceCompletion.Complete(context, workingDirectory, homeDirectory);
        string prefix = context.Prefix;
        int separator = prefix.LastIndexOf('/');
        string parent = separator < 0 ? "" : prefix[..(separator + 1)];
        string leaf = prefix[(separator + 1)..];
        if (prefix == "~" && context.LookupBase is null) { parent = "~/"; leaf = ""; }
        string directory = context.LookupBase is { } lookup
            ? (parent.Length == 0 ? lookup : PathSemantics.JoinValue(lookup, new StringValue(parent))).Raw
            : parent.Length == 0 ? "." : parent;
        var entries = await remote.ListAsync(endpoint, directory, leaf, cancellationToken);
        List<SourceCompletionItem> items = [];
        foreach (var entry in entries)
        {
            if (!entry.Name.StartsWith(leaf, StringComparison.Ordinal)) continue;
            string replacement = SourceCompletion.ItemText(context, entry.Name, entry.IsDirectory, '/');
            items.Add(new(entry.Name + (entry.IsDirectory ? "/" : ""), replacement,
                $"Path · remote {(entry.IsDirectory ? "directory" : "file")} ({endpoint})", entry.IsDirectory, replacement));
        }
        items.Sort((left, right) => TextSemantics.Ordinal.Compare(left.Label, right.Label));
        return items;
    }

    public ValueTask DisposeAsync() => remote.DisposeAsync();
}
