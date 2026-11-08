using System.Text.RegularExpressions;
using Msync.Core;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;

namespace Msync.Cli;

internal enum TerminalInputKind { Text, Cancelled, EndOfInput }

internal readonly record struct TerminalInput(TerminalInputKind Kind, string Text, CancellationToken CancellationToken);

internal sealed partial class InteractivePrompt : IAsyncDisposable
{
    private readonly Prompt prompt;
    private readonly Callbacks callbacks;
    private readonly TextWriter error;

    public static bool IsSupported => !Console.IsInputRedirected && !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    public InteractivePrompt(Func<string, bool> incomplete, Func<IReadOnlyCollection<string>> names,
        string? historyDirectory = null, TextWriter? error = null, bool color = true)
    {
        this.error = error ?? Console.Error;
        string? history = HistoryPath(historyDirectory, this.error);
        KeyBindings keys = new();
        callbacks = new(incomplete, names, color, keys);
        prompt = new(history, callbacks, configuration: new PromptConfiguration(
            prompt: "> ", keyBindings: keys, completionBoxBorderFormat: color ? null : ConsoleFormat.None,
            selectedCompletionItemMarkSymbol: color ? null : new FormattedString(">")));
    }

    public async ValueTask<TerminalInput> ReadAsync()
    {
        callbacks.Submission = null;
        PromptResult result = await prompt.ReadLineAsync();
        TerminalInputKind kind = result is EndOfInputResult ? TerminalInputKind.EndOfInput
            : result.IsSuccess ? TerminalInputKind.Text : TerminalInputKind.Cancelled;
        // PromptResult rewrites line endings on Windows; the compiler consumes the editor buffer.
        string source = kind == TerminalInputKind.Text ? callbacks.Submission ?? result.Text : result.Text;
        return new(kind, source, result.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try { await prompt.DisposeAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { error.WriteLine($"msync: cannot save terminal history: {exception.Message}"); }
    }

    private static string? HistoryPath(string? directory, TextWriter error)
    {
        if (directory is null && HostContext.HomeDirectory.Length == 0) return null;
        directory ??= Path.Combine(HostContext.HomeDirectory, ".msync");
        string path = Path.Combine(directory, "history-v08");
        try
        {
            Directory.CreateDirectory(directory);
            using FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"msync: terminal history '{path}' is unavailable: {exception.Message}");
            return null;
        }
    }

    private sealed class EndOfInputResult() : KeyPressCallbackResult("", null);

    private sealed partial class Callbacks(Func<string, bool> incomplete, Func<IReadOnlyCollection<string>> names, bool color, KeyBindings keys) : PromptCallbacks
    {
        internal string? Submission { get; set; }
        private static readonly string[] Keywords = ["let", "fn", "scope", "native", "if", "else", "for", "in", "match", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", "Bool", "Range", "String", "Function", "Scope", "Path", "Process", "Connection", "Null", "Number"];
        private static readonly string[] Builtins = ["$std", "$local", "$wsl", "$pwd", "$file"];
        private static readonly string[] Commands = ["/help", "/syntax", "/quit", "/exit", "/clear", "/pwd", "/cd"];

        protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)> GetKeyPressCallbacks()
        {
            yield return (new(ConsoleModifiers.Control, ConsoleKey.D), EndOfInput);
            yield return (new(ConsoleModifiers.Control, ConsoleKey.Z), EndOfInput);
        }

        private static Task<KeyPressCallbackResult?> EndOfInput(string text, int caret, CancellationToken cancellationToken) =>
            Task.FromResult<KeyPressCallbackResult?>(text.Length == 0 ? new EndOfInputResult() : null);

        protected override Task<KeyPress> TransformKeyPressAsync(string text, int caret, KeyPress key, CancellationToken cancellationToken)
        {
            if (key.ConsoleKeyInfo.Key == ConsoleKey.Enter && key.ConsoleKeyInfo.Modifiers == 0 && incomplete(text))
                key = new(new ConsoleKeyInfo('\n', ConsoleKey.Enter, shift: true, alt: false, control: false));
            else if (keys.SubmitPrompt.Matches(key.ConsoleKeyInfo))
                Submission = text;
            else if (key.ConsoleKeyInfo.Key == ConsoleKey.Tab && key.ConsoleKeyInfo.Modifiers == 0)
                key = new(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: true));
            return Task.FromResult(key);
        }

        protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
            => Task.FromResult(false);

        protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
        {
            int start = caret, end = caret;
            while (start > 0 && !IsDelimiter(text[start - 1])) start--;
            while (end < text.Length && !IsDelimiter(text[end])) end++;
            return Task.FromResult(TextSpan.FromBounds(start, end));
        }

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
        {
            string prefix = text[spanToBeReplaced.Start..caret];
            if (spanToBeReplaced.Start > 0 && text[spanToBeReplaced.Start - 1] == '"')
                return Task.FromResult<IReadOnlyList<CompletionItem>>([]);
            IEnumerable<string> words = prefix.StartsWith('$') ? Builtins : Keywords.Concat(names());
            if (spanToBeReplaced.Start == 0 && prefix.StartsWith('/')) words = Commands;
            List<CompletionItem> items = words.Where(word => word.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).Order(TextSemantics.Ordinal).Select(word => new CompletionItem(word)).ToList();
            string qualifier = prefix.StartsWith("$local::", StringComparison.Ordinal) ? "$local::"
                : prefix.StartsWith("$pwd::", StringComparison.Ordinal) ? "$pwd::" : "";
            if (qualifier.Length > 0) AddPaths(prefix[qualifier.Length..], items, qualifier);
            else if (!prefix.Contains("::", StringComparison.Ordinal) && !prefix.StartsWith('$')) AddPaths(prefix, items, "");
            return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
        }

        private static bool IsDelimiter(char character) => char.IsWhiteSpace(character) || "{}()[],;=|<>?\"".Contains(character, StringComparison.Ordinal);

        private static void AddPaths(string prefix, ICollection<CompletionItem> items, string qualifier)
        {
            int separator = prefix.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            string parent = separator < 0 ? "" : prefix[..(separator + 1)];
            string leaf = prefix[(separator + 1)..];
            string directory = parent.Length == 0 ? Environment.CurrentDirectory : ExpandHome(parent);
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(TextSemantics.Ordinal))
                {
                    string name = Path.GetFileName(entry);
                    if (!name.StartsWith(leaf, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    bool childDirectory = Directory.Exists(entry);
                    string raw = parent + name + (childDirectory ? Path.DirectorySeparatorChar : "");
                    string replacement = qualifier.Length == 0 ? PathExpression(raw) : qualifier + Quote(raw);
                    items.Add(new CompletionItem(replacement, displayText: raw, filterText: qualifier + prefix));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        private static string ExpandHome(string path) => path == "~" ? HostContext.HomeDirectory
            : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(HostContext.HomeDirectory, path[2..]) : path;

        private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        private static string PathExpression(string raw)
        {
            if (OperatingSystem.IsWindows()) return "$local::" + Quote(Path.GetFullPath(ExpandHome(raw)));
            string prefix = raw.StartsWith('/') ? "/" : raw.StartsWith("~/", StringComparison.Ordinal) ? "~/" : "./";
            string tail = prefix == "~/" ? raw[2..] : raw.TrimStart('/');
            if (tail.StartsWith("./", StringComparison.Ordinal)) tail = tail[2..];
            return prefix + string.Join('/', tail.Split('/').Select(part => part.Length == 0 || part is "." or ".." ? part : "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        }

        protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(string text, CancellationToken cancellationToken)
        {
            if (!color) return Task.FromResult<IReadOnlyCollection<FormatSpan>>([]);
            List<FormatSpan> spans = [];
            foreach (Match token in Tokens().Matches(text))
            {
                AnsiColor? color = token.Groups["comment"].Success ? AnsiColor.BrightBlack
                    : token.Groups["string"].Success ? AnsiColor.Green
                    : Keywords.Contains(token.Value, StringComparer.Ordinal) ? char.IsUpper(token.Value[0]) ? AnsiColor.Magenta : AnsiColor.Cyan : null;
                if (color is { } value) spans.Add(new(token.Index, token.Length, value));
            }
            return Task.FromResult<IReadOnlyCollection<FormatSpan>>(spans);
        }

        [GeneratedRegex("""(?<comment>(?<!\S)(?:\#|//)[^\r\n]*)|(?<string>"(?:[^"]|"")*(?:"|$))|[\p{L}_][\p{L}\p{N}_]*""", RegexOptions.CultureInvariant)]
        private static partial Regex Tokens();
    }
}
