using Msync.Compiler;
using Msync.Compiler.Syntax;
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
    private readonly SourceCompletionService completion;
    private readonly Callbacks callbacks;
    private readonly TextWriter error;
    private readonly string errorColor;
    private readonly Func<IReadOnlyList<SemanticMember>>? describe;

    public static bool IsSupported => !Console.IsInputRedirected && !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    public InteractivePrompt(Func<string, bool> incomplete, Func<IReadOnlyCollection<string>> names,
        string? historyDirectory = null, TextWriter? error = null, bool color = true,
        Func<IReadOnlyList<SemanticMember>>? describe = null, Func<string, EditorAnalysis>? analyze = null, string? colorPolicy = null,
        Func<string>? workingDirectory = null)
    {
        this.error = error ?? Console.Error;
        errorColor = colorPolicy ?? (color ? "auto" : "never");
        this.describe = describe;
        string? history = HistoryPath(historyDirectory, this.error);
        KeyBindings keys = new();
        completion = new(AuthenticateAsync);
        callbacks = new(incomplete, names, color, keys, completion, ReportAsync, analyze, workingDirectory ?? (() => Environment.CurrentDirectory));
        prompt = new(history, callbacks, configuration: new PromptConfiguration(
            prompt: "> ", keyBindings: keys, completionBoxBorderFormat: color ? null : ConsoleFormat.None,
            selectedCompletionItemMarkSymbol: color ? null : new FormattedString(">"))
        { EnableHints = color });
    }

    private Task<string?> AuthenticateAsync(SshPrompt request, CancellationToken cancellationToken) =>
        prompt.RunExternalInteractionAsync(() => SshTerminalPrompt.ReadAsync(request, cancellationToken));

    private async Task ReportAsync(Exception error)
    {
        await prompt.RunExternalInteractionAsync(() =>
        {
            TerminalPresentation.Error(error, errorColor);
            return Task.FromResult(0);
        });
    }

    public async ValueTask<TerminalInput> ReadAsync()
    {
        callbacks.Submission = null;
        callbacks.RefreshBindings(describe?.Invoke() ?? []);
        PromptResult result = await prompt.ReadLineAsync();
        TerminalInputKind kind = result is EndOfInputResult ? TerminalInputKind.EndOfInput
            : result.IsSuccess ? TerminalInputKind.Text : TerminalInputKind.Cancelled;
        // PromptResult rewrites line endings on Windows; the compiler consumes the editor buffer.
        string source = kind == TerminalInputKind.Text ? callbacks.Submission ?? result.Text : result.Text;
        return new(kind, source, result.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await completion.DisposeAsync();
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

    private sealed partial class Callbacks(Func<string, bool> incomplete, Func<IReadOnlyCollection<string>> names, bool color, KeyBindings keys, SourceCompletionService completion, Func<Exception, Task> report, Func<string, EditorAnalysis>? analyze,
        Func<string> workingDirectory) : PromptCallbacks
    {
        internal string? Submission { get; set; }
        private string? notice;
        private IReadOnlyDictionary<string, SemanticMember> bindings = new Dictionary<string, SemanticMember>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, SemanticMember>? completionBindings;
        private EditorContext? insight;
        private IReadOnlySet<string> boundNames = new HashSet<string>(StringComparer.Ordinal);

        internal void RefreshBindings(IReadOnlyList<SemanticMember> values)
        {
            bindings = Flatten(values);
            completionBindings = null;
            insight = null;
            boundNames = bindings.Values.Where(member => !member.IsConstraint).Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, SemanticMember> Flatten(IReadOnlyList<SemanticMember> values)
        {
            Dictionary<string, SemanticMember> current = new(StringComparer.Ordinal);
            foreach (var value in EditorCompiler.BuiltinBindings) Add(value.Name, value);
            foreach (var value in values) Add(value.Name, value);
            return current;

            void Add(string name, SemanticMember value)
            {
                current[name] = value;
                if (value.IsConstraint || value.Members is null) return;
                foreach (var member in value.Members) Add(name + "::" + member.Name, member);
            }
        }

        protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)> GetKeyPressCallbacks()
        {
            yield return (new(ConsoleModifiers.Control, ConsoleKey.D), EndOfInput);
            yield return (new(ConsoleModifiers.Control, ConsoleKey.Z), EndOfInput);
        }

        private static Task<KeyPressCallbackResult?> EndOfInput(string text, int caret, CancellationToken cancellationToken) =>
            Task.FromResult<KeyPressCallbackResult?>(text.Length == 0 ? new EndOfInputResult() : null);

        protected override Task<KeyPress> TransformKeyPressAsync(string text, int caret, KeyPress key, CancellationToken cancellationToken)
        {
            if (key.ConsoleKeyInfo.Key is not (ConsoleKey.Tab or ConsoleKey.Spacebar)) notice = null;
            bool completionKey = key.ConsoleKeyInfo is { Key: ConsoleKey.Tab, Modifiers: 0 }
                or { Key: ConsoleKey.Spacebar, Modifiers: ConsoleModifiers.Control };
            if (completionKey && analyze is not null)
            {
                insight = analyze(text).At(caret);
                completionBindings = Flatten(insight.Bindings);
                boundNames = completionBindings.Values.Where(member => !member.IsConstraint).Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
            }
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
            if (insight?.IsTypePosition == true)
            {
                var selected = TypeCompletion.Find(text, caret);
                return Task.FromResult(new TextSpan(selected.Span.Start, selected.Span.Length));
            }
            if (insight?.IsGlobalImport == true)
            {
                var name = NameSyntax.SpanAt(text, caret);
                return Task.FromResult(new TextSpan(name.Start, name.Length));
            }
            if (BuiltinCompletion.Find(text, caret, insight) is { } builtin)
            {
                if (BuiltinCompletion.IncludesDirectoryEntries(builtin) && FindSource(text, caret) is { } physical)
                    return Task.FromResult(new TextSpan(physical.Start, physical.Length));
                return Task.FromResult(new TextSpan(builtin.Start, builtin.Length));
            }
            if (FindSource(text, caret) is { } context)
                return Task.FromResult(new TextSpan(context.Start, context.Length));
            int start = caret, end = caret;
            while (start > 0 && !IsDelimiter(text[start - 1])) start--;
            while (end < text.Length && !IsDelimiter(text[end])) end++;
            if (start == 0 && text.StartsWith('/')) return Task.FromResult(TextSpan.FromBounds(start, end));
            var atom = NameSyntax.SpanAt(text, caret);
            return Task.FromResult(new TextSpan(atom.Start, atom.Length));
        }

        private SourceCompletionContext? FindSource(string text, int caret)
        {
            SemanticMember? receiver = insight?.Receiver;
            PathValue? path = receiver?.KnownPath ?? (receiver?.KnownText is { } value ? new PathValue(value) : null);
            bool unknown = receiver is not null && path is null
                && (receiver.Type.Contains("Path", StringComparison.Ordinal) || receiver.Type.Contains("String", StringComparison.Ordinal) || receiver.Type == "Unknown");
            return SourceCompletion.Find(text, caret, includeBarePaths: true,
                receiverPath: path, unknownPathReceiver: unknown, physicalEndpoint: insight?.PhysicalEndpoint,
                unknownPhysicalOwner: insight?.HasPhysicalOwner == true && insight.PhysicalEndpoint is null,
                boundNames: boundNames);
        }

        protected override async Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
        {
            if (insight?.IsTypePosition == true)
                return TypeCompletion.Candidates(TypeCompletion.Find(text, caret), insight)
                    .Select(member => new CaretCompletionItem(member.Name,
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(member.Signature + "\n" + member.Description)))
                    .ToArray();
            if (insight?.IsGlobalImport == true)
            {
                string importPrefix = text[spanToBeReplaced.Start..caret];
                return insight.Bindings.Where(member => member.Name.StartsWith(importPrefix, StringComparison.Ordinal))
                    .OrderBy(member => member.Name, TextSemantics.Ordinal)
                    .Select(member => new CaretCompletionItem(member.Name,
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(member.Signature)))
                    .ToArray();
            }
            if (BuiltinCompletion.Find(text, caret, insight) is { } builtin)
            {
                List<CompletionItem> builtinItems = BuiltinCompletion.Complete(builtin, workingDirectory())
                    .Select(candidate => (CompletionItem)new CaretCompletionItem(candidate.Replacement, displayText: candidate.Label,
                        filterText: candidate.FilterText ?? candidate.Label,
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(candidate.Detail)))
                    .ToList();
                if (BuiltinCompletion.IncludesDirectoryEntries(builtin) && FindSource(text, caret) is { } physical)
                    foreach (var candidate in await CompleteSourceAsync(physical, cancellationToken))
                        if (!builtinItems.Any(item => item.DisplayText == candidate.Label))
                            builtinItems.Add(new CaretCompletionItem(candidate.Replacement, displayText: candidate.Label,
                                filterText: candidate.FilterText ?? candidate.Label,
                                getExtendedDescription: _ => Task.FromResult<FormattedString>(candidate.Detail)));
                return builtinItems;
            }
            string prefix = text[spanToBeReplaced.Start..caret];
            SourceCompletionContext? sourceContext = FindSource(text, caret);
            if (sourceContext is null && PromptSyntax.InsideLiteral(text, caret)) return [];
            IReadOnlyDictionary<string, SemanticMember> symbols = completionBindings ?? bindings;
            IReadOnlyList<SemanticMember>? members = insight?.Receiver?.Members;
            if (members is not null) symbols = members.ToDictionary(member => member.Name, StringComparer.Ordinal);
            IEnumerable<string> runtimeNames = symbols.Where(pair => !pair.Value.IsConstraint).Select(pair => pair.Key);
            IEnumerable<string> words = members is not null ? runtimeNames
                : prefix.StartsWith('$') ? PromptSyntax.Builtins.Concat(runtimeNames) : PromptSyntax.Keywords.Concat(names()).Concat(runtimeNames);
            if (spanToBeReplaced.Start == 0 && prefix.StartsWith('/')) words = PromptSyntax.Commands;
            List<CompletionItem> items = words.Where(word => word.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).Order(TextSemantics.Ordinal).Select(word => (CompletionItem)new CaretCompletionItem(word,
                    getExtendedDescription: _ => Task.FromResult<FormattedString>(symbols.TryGetValue(word, out var binding) ? binding.Signature : word))).ToList();
            if (members is not null) return items;
            if (sourceContext is { } context)
                foreach (var candidate in await CompleteSourceAsync(context, cancellationToken))
                    items.Add(new CaretCompletionItem(candidate.Replacement, displayText: candidate.Label,
                        filterText: candidate.FilterText ?? prefix + candidate.Label[context.Prefix.Length..].Replace("\"", "\"\"", StringComparison.Ordinal),
                        getExtendedDescription: _ => Task.FromResult<FormattedString>(candidate.Detail)));
            return items;
        }

        private async Task<IReadOnlyList<SourceCompletionItem>> CompleteSourceAsync(SourceCompletionContext context, CancellationToken cancellationToken)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
            bool captured = Console.TreatControlCAsInput;
            Console.CancelKeyPress += cancel;
            Console.TreatControlCAsInput = false;
            try { return await completion.CompleteAsync(context, workingDirectory(), HostContext.HomeDirectory, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { notice = "completion cancelled"; return []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException)
            { notice = error.Message; await report(error); return []; }
            finally { Console.TreatControlCAsInput = captured; Console.CancelKeyPress -= cancel; }
        }

        private static bool IsDelimiter(char character) => char.IsWhiteSpace(character) || "{}()[],;=|<>?\"@".Contains(character, StringComparison.Ordinal);

        protected override Task<PromptHint?> GetHintAsync(string text, int caret, CancellationToken cancellationToken) =>
            Task.FromResult(!color ? null : notice is not null ? new PromptHint("  [" + notice + "]", Insertable: false) : PromptSyntax.Hint(text, caret, bindings));

        protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<FormatSpan>>(color ? PromptSyntax.Highlight(text, bindings) : []);
    }
}
