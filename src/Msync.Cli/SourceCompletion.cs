using System.Text;
using Msync.Core;

namespace Msync.Cli;

internal enum SourceCompletionKind { Path, Ssh }
internal sealed record SourceCompletionContext(int Start, int Length, string Prefix, bool Quoted,
    string Qualifier, SourceCompletionKind Kind, ConnectionValue? Endpoint = null, bool TransferSource = false,
    PathValue? LookupBase = null, string? ExpressionBase = null, string LiteralBase = "", string? SourcePrefix = null);
internal sealed record SourceCompletionItem(string Label, string Replacement, string Detail, bool IsDirectory = false, string? FilterText = null);

internal static class SourceCompletion
{
    private static readonly string[] WordBoundaries = ["and", "or", "is", "not", "in", "else", "if", "fn", "scope", "let", "match", "for", "exists", "return", "break", "loop", "native"];

    public static SourceCompletionContext? Find(string text, int caret, bool includeBarePaths = false, Func<string, ConnectionValue?>? resolveEndpoint = null,
        PathValue? receiverPath = null, bool unknownPathReceiver = false, ConnectionValue? physicalEndpoint = null, bool unknownPhysicalOwner = false)
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
            if (Boundary(character) && !(uri && character is '[' or ']')) start = index + 1;
        }
        if (comment || regex) return null;
        string fragment = text[start..caret];
        int end = TokenEnd(text, caret, quoted, fragment.StartsWith("ssh://", StringComparison.Ordinal) || fragment.StartsWith("wsl://", StringComparison.Ordinal));
        if (fragment.StartsWith("ssh://", StringComparison.Ordinal) && LastSelection(fragment) < 0)
            return new(start, end - start, fragment, false, "", SourceCompletionKind.Ssh, SourcePrefix: fragment);
        if (unknownPhysicalOwner || physicalEndpoint is not null && !PhysicalDirectory(physicalEndpoint)) return null;
        int pathJoin = LastPathJoin(fragment);
        if (pathJoin >= 0 && receiverPath is not null)
        {
            if (physicalEndpoint is not null) receiverPath = PathSemantics.Qualify(receiverPath, physicalEndpoint);
            string sourceBase = fragment[..(pathJoin + 1)];
            bool preserve = receiverPath.Connection is not null || receiverPath.WorkingDirectory is not null || receiverPath.HomeDirectory is not null
                || receiverPath.Raw != Decode(fragment[..pathJoin]);
            return new(start, end - start, Decode(fragment[(pathJoin + 1)..]), false, "", SourceCompletionKind.Path,
                receiverPath.Connection, FollowedByTransfer(text, end), receiverPath,
                preserve ? sourceBase : null, preserve ? "" : Decode(sourceBase), fragment);
        }
        if (pathJoin >= 0 && unknownPathReceiver) return null;
        int selection = LastSelection(fragment);
        string qualifier = selection < 0 ? "" : fragment[..(selection + 2)];
        ConnectionValue? endpoint = physicalEndpoint;
        if (selection >= 0)
        {
            string name = fragment[..selection];
            var writtenEndpoint = name switch
            {
                "$local" => new ConnectionValue(ConnectionKind.Local),
                "$pwd" => new ConnectionValue(ConnectionKind.Local, "pwd"),
                "$wsl" => new ConnectionValue(ConnectionKind.Wsl),
                _ when name.StartsWith("ssh://", StringComparison.Ordinal) => new ConnectionValue(ConnectionKind.Ssh, name[6..]),
                _ when name.StartsWith("wsl://", StringComparison.Ordinal) => new ConnectionValue(ConnectionKind.Wsl, name[6..]),
                _ => resolveEndpoint?.Invoke(name)
            };
            endpoint ??= writtenEndpoint;
            if (endpoint is null) return null;
            if (!PhysicalDirectory(endpoint)) return null;
        }
        string value = fragment[qualifier.Length..];
        bool isString = value.StartsWith('"');
        string raw = Decode(value);
        bool path = endpoint is not null || raw.Contains('/') || OperatingSystem.IsWindows() && raw.Contains('\\')
            || raw is "~" or "." or "..";
        if (!path && !(includeBarePaths && raw.Length > 0 && !raw.StartsWith('$'))) return null;
        return new(start, end - start, raw, isString, qualifier, SourceCompletionKind.Path, endpoint, FollowedByTransfer(text, end), SourcePrefix: fragment);
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
        char slash = separator >= 0 ? prefix[separator] : context.LookupBase is not null ? '/' : Path.DirectorySeparatorChar;
        List<SourceCompletionItem> items = [];
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!entry.Name.StartsWith(leaf, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                string raw = context.LiteralBase + parent + entry.Name + (isDirectory ? slash : "");
                bool stringValue = context.Quoted || !context.TransferSource && raw.Any(char.IsWhiteSpace);
                string replacement = context.ExpressionBase is { } expression
                    ? expression + Quote(parent + entry.Name) + (isDirectory ? "/" : "")
                    : context.Qualifier + (stringValue || context.Qualifier.Length != 0 ? Quote(raw) : PathExpression(raw));
                string type = context.ExpressionBase is null && stringValue && context.Qualifier.Length == 0 && context.Endpoint is null ? "String" : "Path";
                string label = context.ExpressionBase is { } sourceBase ? sourceBase + parent + entry.Name + (isDirectory ? "/" : "") : raw;
                string? filter = context.SourcePrefix is { } sourcePrefix ? sourcePrefix + entry.Name[leaf.Length..] + (isDirectory ? slash : "") : null;
                items.Add(new(label, replacement, $"{type} · {(isDirectory ? "directory" : "file")}", isDirectory, filter));
            }
        }
        catch (DirectoryNotFoundException) { return []; }
        items.Sort((left, right) => TextSemantics.Ordinal.Compare(left.Label, right.Label));
        return items;
    }

    private static bool PhysicalDirectory(ConnectionValue endpoint) => endpoint.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
        || endpoint is { Kind: ConnectionKind.Local, Address: not "file" };

    private static IReadOnlyList<SourceCompletionItem> Hosts(SourceCompletionContext context, string homeDirectory)
    {
        string authority = context.Prefix[6..];
        if (authority.Contains('/')) return [];
        int at = authority.LastIndexOf('@');
        string user = at < 0 ? "" : authority[..(at + 1)];
        string hostPrefix = authority[(at + 1)..];
        return SshHostCatalog.Read(homeDirectory)
            .Where(host => host.Host.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(host => new SourceCompletionItem("ssh://" + user + host.Host, "ssh://" + user + host.Host,
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

    private static bool FollowedByTransfer(string text, int offset)
    {
        while (offset < text.Length)
        {
            if (char.IsWhiteSpace(text[offset])) { offset++; continue; }
            bool comment = text[offset] == '#' || text.AsSpan(offset).StartsWith("//", StringComparison.Ordinal);
            if (!comment) return text.AsSpan(offset).StartsWith("->", StringComparison.Ordinal);
            while (offset < text.Length && text[offset] is not ('\r' or '\n')) offset++;
        }
        return false;
    }

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
            else if (!quoted && Boundary(character) && !(uri && character is '[' or ']')) break;
        }
        return end;
    }

    private static bool Boundary(char character) => char.IsWhiteSpace(character) || "{}()[],;=|<>?".Contains(character, StringComparison.Ordinal);
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

    internal static string PathExpression(string raw, bool remote = false)
    {
        if (!remote && OperatingSystem.IsWindows()) return "$local::" + Quote(raw);
        string prefix = raw.StartsWith('/') ? "/" : raw.StartsWith("~/", StringComparison.Ordinal) ? "~/" : "./";
        string tail = prefix == "~/" ? raw[2..] : raw.TrimStart('/');
        if (tail.StartsWith("./", StringComparison.Ordinal)) tail = tail[2..];
        return prefix + string.Join('/', tail.Split('/').Select(part => part.Length == 0 || part is "." or ".." ? part : Quote(part)));
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
            string raw = context.LiteralBase + parent + entry.Name + (entry.IsDirectory ? "/" : "");
            string insertion = context.Quoted || !context.TransferSource && raw.Any(char.IsWhiteSpace)
                ? SourceCompletion.Quote(raw) : SourceCompletion.PathExpression(raw, remote: true);
            string replacement = context.ExpressionBase is { } expression
                ? expression + SourceCompletion.Quote(parent + entry.Name) + (entry.IsDirectory ? "/" : "") : context.Qualifier + insertion;
            string label = context.ExpressionBase is { } sourceBase ? sourceBase + parent + entry.Name + (entry.IsDirectory ? "/" : "") : raw;
            string? filter = context.SourcePrefix is { } sourcePrefix ? sourcePrefix + entry.Name[leaf.Length..] + (entry.IsDirectory ? "/" : "") : null;
            items.Add(new(label, replacement,
                $"Path · remote {(entry.IsDirectory ? "directory" : "file")} ({endpoint})", entry.IsDirectory, filter));
        }
        items.Sort((left, right) => TextSemantics.Ordinal.Compare(left.Label, right.Label));
        return items;
    }

    public ValueTask DisposeAsync() => remote.DisposeAsync();
}
