using Msync.Compiler;
using Msync.Core;

namespace Msync.Cli;

internal enum BuiltinCompletionKind { Root, Environment, SearchPath, WorkingDirectory, SourceFile }
internal sealed record BuiltinCompletionContext(int Start, int Length, string Prefix, BuiltinCompletionKind Kind,
    bool Quoted = false, IReadOnlySet<string>? BoundNames = null);

internal static class BuiltinCompletion
{
    public static bool IncludesDirectoryEntries(BuiltinCompletionContext context) =>
        context.Kind is BuiltinCompletionKind.WorkingDirectory or BuiltinCompletionKind.SourceFile;

    public static BuiltinCompletionContext? Find(string text, int caret, EditorContext? insight)
    {
        int start = caret;
        while (start > 0 && IdentifierPart(text[start - 1])) start--;
        if (start > 0 && text[start - 1] == '$' && !PromptSyntax.InsideLiteral(text, start - 1))
        {
            int end = caret;
            while (end < text.Length && IdentifierPart(text[end])) end++;
            return new(start, end - start, text[start..caret], BuiltinCompletionKind.Root);
        }

        if (insight?.PhysicalEndpoint is not { } endpoint || insight.Receiver is not null)
            return null;
        BuiltinCompletionKind? kind = endpoint.Kind switch
        {
            ConnectionKind.Environment => BuiltinCompletionKind.Environment,
            ConnectionKind.SearchPath => BuiltinCompletionKind.SearchPath,
            ConnectionKind.Local when endpoint.Address == "pwd" => BuiltinCompletionKind.WorkingDirectory,
            ConnectionKind.Local when endpoint.Address == "file" => BuiltinCompletionKind.SourceFile,
            _ => null
        };
        if (kind is null) return null;
        int selection = caret == 0 ? -1 : text.LastIndexOf('@', caret - 1);
        while (selection >= 0 && PromptSyntax.InsideLiteral(text, selection))
            selection = selection == 0 ? -1 : text.LastIndexOf('@', selection - 1);
        if (selection < 0) return null;
        start = selection + 1;
        while (start < caret && char.IsWhiteSpace(text[start])) start++;
        bool quoted = start < caret && text[start] == '"';
        var boundNames = insight.Bindings.Where(item => !item.IsConstraint).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!quoted)
        {
            foreach (char character in text.AsSpan(start, caret - start))
                if (!MemberPart(character)) return null;
            int end = caret;
            while (end < text.Length && MemberPart(text[end])) end++;
            return new(start, end - start, text[start..caret], kind.Value, BoundNames: boundNames);
        }
        if (!TryQuotedPrefix(text, start, caret, out string prefix, out int quotedEnd)) return null;
        return new(start, quotedEnd - start, prefix, kind.Value, Quoted: true, BoundNames: boundNames);
    }

    public static IReadOnlyList<SourceCompletionItem> Complete(BuiltinCompletionContext context, string workingDirectory, string? sourceFile = null)
    {
        if (context.Kind == BuiltinCompletionKind.Root)
            return EditorCompiler.BuiltinBindings
                .Where(binding => binding.Name.StartsWith('$') && binding.Name.AsSpan(1).StartsWith(context.Prefix, StringComparison.Ordinal))
                .Select(binding => new SourceCompletionItem(binding.Name[1..], binding.Name[1..], binding.Signature + "\n" + binding.Description,
                    FilterText: binding.Name[1..]))
                .OrderBy(item => item.Label, TextSemantics.Ordinal).ToArray();
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (context.Kind == BuiltinCompletionKind.Environment)
            return Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(name => name.StartsWith(context.Prefix, comparison))
                .Order(TextSemantics.Ordinal)
                .Select(name => new SourceCompletionItem(name, MemberSpelling(name, context),
                    "Path · environment variable\nRead this process's environment with $env @ name. The value is interpreted as a Path.", FilterText: name))
                .ToArray();
        if (context.Kind == BuiltinCompletionKind.WorkingDirectory)
            return "pwd".StartsWith(context.Prefix, comparison)
                ? [new("pwd", MemberSpelling("pwd", context), "Path · current working directory", IsDirectory: true, FilterText: "pwd")]
                : [];
        if (context.Kind == BuiltinCompletionKind.SourceFile)
            return sourceFile is null ? [] : new[]
            {
                new SourceCompletionItem("this", MemberSpelling("this", context), "Path · this source file", FilterText: "this"),
                new SourceCompletionItem("parent", MemberSpelling("parent", context), "Path · source file directory", IsDirectory: true, FilterText: "parent")
            }.Where(item => item.Label.StartsWith(context.Prefix, comparison)).ToArray();
        return Programs(context, workingDirectory, comparison);
    }

    private static IReadOnlyList<SourceCompletionItem> Programs(BuiltinCompletionContext context, string workingDirectory, StringComparison comparison)
    {
        HashSet<string> names = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (string configured in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = Path.IsPathRooted(configured) ? configured : Path.Combine(workingDirectory, configured);
            try
            {
                foreach (FileInfo entry in new DirectoryInfo(directory).EnumerateFiles())
                {
                    if (!entry.Name.StartsWith(context.Prefix, comparison)) continue;
                    if (!Executable(entry, extensions)) continue;
                    names.Add(entry.Name);
                }
            }
            catch (Exception error) when (error is DirectoryNotFoundException or UnauthorizedAccessException) { }
        }
        return names.Order(TextSemantics.Ordinal).Select(name => new SourceCompletionItem(name, MemberSpelling(name, context),
            "Path · executable on PATH\nResolve an existing executable through the native PATH and PATHEXT search.", FilterText: name)).ToArray();
    }

    private static bool Executable(FileInfo entry, string[] extensions)
    {
        if (OperatingSystem.IsWindows()) return extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase);
        try { return (entry.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (FileNotFoundException) { return false; }
    }

    private static bool IdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
    private static bool MemberPart(char value) => IdentifierPart(value) || value is '.' or '-';

    private static string MemberSpelling(string name, BuiltinCompletionContext context) =>
        context.Quoted || name.Length == 0 || context.BoundNames?.Contains(name) == true
            || SourceCompletion.NeedsQuotes(name) || NumberSyntax.TryParse(name, out _)
            || name.Any(value => value is '/' or '\\' or '-')
            ? SourceCompletion.Quote(name) : name;

    private static bool TryQuotedPrefix(string text, int start, int caret, out string prefix, out int end)
    {
        int position = start + 1;
        System.Text.StringBuilder value = new();
        while (position < caret)
        {
            if (text[position] != '"') { value.Append(text[position++]); continue; }
            if (position + 1 < caret && text[position + 1] == '"') { value.Append('"'); position += 2; continue; }
            prefix = ""; end = caret; return false;
        }
        prefix = value.ToString();
        end = caret;
        while (end < text.Length)
        {
            if (text[end++] != '"') continue;
            if (end < text.Length && text[end] == '"') { end++; continue; }
            break;
        }
        return true;
    }
}
