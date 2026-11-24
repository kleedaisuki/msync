using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;
using Msync.Core.Hpir;
using Msync.Runtime.Hpir;
using System.Globalization;

namespace Msync.Cli;

internal sealed class ReplSession
{
    private static readonly string ShellPrelude = CreateShellPrelude();
    private readonly CompilerSession compiler;
    private readonly HpirExecutionSession execution;
    private readonly TextReader? inputReader;
    private readonly string color;
    private readonly TelemetrySession? telemetry;
    private readonly RuntimeProfile? profile;
    private long submissionIndex;
    private readonly SubmissionSyntax syntax = new();
    private readonly object cancellationGate = new();
    private CancellationTokenSource? active;
    private string pending = "";
    private bool interruptedInput;
    private bool editing;

    public ReplSession(ExecutionOptions options, int optimization, string color, TelemetrySession? telemetry)
    {
        this.color = color;
        this.telemetry = telemetry;
        profile = options.Profile;
        compiler = new(optimization: optimization);
        execution = new(options with { ReportError = ReportTransferError });
        inputReader = Console.IsInputRedirected ? options.Input : null;
    }

    private static string CreateShellPrelude()
    {
        var assembly = typeof(ReplSession).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".shell.msync", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        Document document = Parser.Parse(reader.ReadToEnd(), "msync-stdlib:///0.8.0/shell.msync");
        return string.Join('\n', document.Statements.Select(statement => statement switch
        {
            FunctionDeclaration function => function.Name,
            NativeDeclaration native => native.Name,
            ScopeDeclaration scope => scope.Name,
            LetStatement { Pattern: NamePattern binding } => binding.Name,
            _ => null
        }).OfType<string>().Select(name => $"let {name} = $std::shell::{name};"));
    }

    public async Task<int> RunAsync()
    {
        int status;
        try { status = await RunPromptAsync(); }
        catch (Exception error)
        {
            try { Close(); }
            catch (Exception cleanup) { throw new AggregateException("REPL execution and cleanup failed.", error, cleanup); }
            throw;
        }
        try { Close(); }
        catch (Exception error) when (TerminalPresentation.IsUserFailure(error))
        {
            TerminalPresentation.Error(error, color, resolve: ResolveSource);
            return 1;
        }
        return status;
    }

    private void Close()
    {
        using TelemetrySpan? close = telemetry?.Begin("repl.close");
        using IDisposable? owner = close is null ? null : telemetry?.Timeline?.Attach(close.Id);
        try
        {
            execution.Dispose();
            close?.Complete();
        }
        catch (Exception error) { close?.Fail(error); throw; }
    }

    private async Task<int> RunPromptAsync()
    {
        await ExecuteAsync(new("<repl:prelude>", ShellPrelude), CancellationToken.None, prelude: true);
        await using InteractivePrompt? prompt = InteractivePrompt.IsSupported
            ? new(syntax.IsIncomplete, PromptNames, color: TerminalPresentation.Colors(color), describe: DescribePromptBindings, analyze: AnalyzePrompt, colorPolicy: color,
                workingDirectory: () => execution.WorkingDirectory) : null;
        editing = prompt is not null;
        if (!Console.IsInputRedirected)
            Console.WriteLine(TerminalPresentation.Paint("msync " + ProductVersion.Current, TerminalPresentation.HeadingColor, TerminalPresentation.Colors(color))
                + (editing ? " — Tab to complete; Shift+Enter for a newline; /help for commands"
                    : " — basic input; /help for commands; /quit to leave"));
        if (!editing) Console.CancelKeyPress += Cancel;
        try
        {
            while (true)
            {
                TerminalInput input = await ReadAsync(prompt);
                if (input.Kind == TerminalInputKind.EndOfInput) return EndOfInput();
                if (input.Kind == TerminalInputKind.Cancelled) { pending = ""; continue; }
                if (await SubmitAsync(input)) return 0;
            }
        }
        finally
        {
            if (!editing) Console.CancelKeyPress -= Cancel;
        }
    }

    private EditorAnalysis AnalyzePrompt(string source)
    {
        using TelemetrySpan? span = telemetry?.Begin("repl.analysis", TelemetryLevel.Phases);
        return span is null ? compiler.AnalyzeEditor(source)
            : span.Measure((source, trace: span.CompilerTrace),
                state => compiler.AnalyzeEditor(state.source, state.trace));
    }

    private IReadOnlyList<SemanticMember> DescribePromptBindings() => compiler.DescribeBindings()
        .Concat(compiler.DescribeConcepts()).DistinctBy(member => member.Name, StringComparer.Ordinal).ToArray();

    private IReadOnlyCollection<string> PromptNames() => compiler.BindingNames.Where(name => !name.StartsWith('$')).ToArray();

    private async ValueTask<TerminalInput> ReadAsync(InteractivePrompt? prompt)
    {
        if (prompt is not null) return await prompt.ReadAsync();
        if (!Console.IsInputRedirected) Console.Write(pending.Length == 0 ? "> " : "· ");
        string? line = await (inputReader ?? Console.In).ReadLineAsync(CancellationToken.None);
        if (line is null) return new(TerminalInputKind.EndOfInput, "", CancellationToken.None);
        if (Volatile.Read(ref interruptedInput)) { pending = ""; Volatile.Write(ref interruptedInput, false); }
        return new(TerminalInputKind.Text, pending + line + "\n", CancellationToken.None);
    }

    private async ValueTask<bool> SubmitAsync(TerminalInput input)
    {
        string source = input.Text;
        if (source.Trim() is "/quit" or "/exit") return true;
        SourceSnapshot snapshot = new("<repl:" + (++submissionIndex).ToString(CultureInfo.InvariantCulture) + ">", source);
        try
        {
            if (!Command(source)) await ExecuteAsync(snapshot, input.CancellationToken);
            pending = "";
        }
        catch (SyntaxException error) when (!editing && error.IsIncomplete) { pending = source; }
        catch (OperationCanceledException)
        {
            TerminalPresentation.Cancelled(color, interactive: true);
            pending = "";
        }
        catch (Exception error) when (TerminalPresentation.IsUserFailure(error))
        {
            TerminalPresentation.Error(error, color, snapshot, resolve: ResolveSource);
            pending = "";
        }
        return false;
    }

    private SourceSnapshot? ResolveSource(string identity) =>
        compiler.SourceText(identity) is { } text ? new(identity, text) : null;

    private void ReportTransferError(Exception error) => TerminalPresentation.Error(error, color,
        help: "Check the source, destination and permissions, then retry. Completed transfers are retained.",
        resolve: ResolveSource);

    private async ValueTask ExecuteAsync(SourceSnapshot source, CancellationToken cancellationToken, bool prelude = false)
    {
        using TelemetrySpan? submission = telemetry?.Begin("repl.submission");
        if (submission is not null)
        {
            submission.SetMetric("submission.index", submissionIndex);
            submission.SetAttribute("submission.kind", prelude ? "prelude" : "input");
            submission.SetAttribute("submission.source", source.Name);
        }
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (cancellationGate) active = cancellation;
        try
        {
            HpirSessionSubmission module;
            using (TelemetrySpan? compile = submission?.Begin("compile"))
            {
                try
                {
                    module = compiler.CompilePhysical(source.Text, syntax.Take(source.Text), compile?.CompilerTrace, source.Name);
                    compile?.Complete();
                }
                catch (Exception error)
                {
                    compile?.Fail(error);
                    compiler.ReconcilePhysicalSubmission(_ => false, execution.ObserveBinding);
                    throw;
                }
            }
            using TelemetrySpan? execute = submission?.Begin("execute");
            using IDisposable? owner = execute is null ? null : telemetry?.Timeline?.Attach(execute.Id);
            execute?.SetAttribute("vm.driver", "hpir");
            ProcessCounters? before = execute?.MeasureProcess();
            try
            {
                HpirSubmissionOutcome outcome = execution.ExecuteWithOutcome(module, cancellation.Token);
                execute?.SetMetric("transfers.failed", outcome.FailedTransfers);
                submission?.SetMetric("transfers.failed", outcome.FailedTransfers);
                if (outcome.Value.Kind != TypeKind.Null)
                {
                    using TelemetrySpan? display = execute?.Begin("repl.display");
                    try
                    {
                        await DisplayAsync(outcome.Value, display, cancellation.Token);
                        display?.Complete();
                    }
                    catch (Exception error) { display?.Fail(error); throw; }
                }
                compiler.AcceptPhysicalSubmission(execution.ObserveBinding, execute?.CompilerTrace);
                execute?.Complete(outcome.FailedTransfers == 0);
                submission?.Complete(outcome.FailedTransfers == 0);
            }
            catch (Exception error)
            {
                execute?.Fail(error);
                compiler.ReconcilePhysicalSubmission(execution.IsInitialized, execution.ObserveFailedBinding, execute?.CompilerTrace);
                throw;
            }
            finally
            {
                execute?.RecordProcess(before);
                if (execute is not null) telemetry!.PublishProfile(profile, execute.Id);
            }
        }
        catch (Exception error) { submission?.Fail(error); throw; }
        finally { lock (cancellationGate) active = null; }
    }

    private static ValueTask DisplayAsync(HpirSessionValue value, TelemetrySpan? display, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        display?.SetAttribute("result.kind", value.Kind.ToString());
        HpirSessionPresentation.Write(Console.Out, value);
        return ValueTask.CompletedTask;
    }


    private void Cancel(object? sender, ConsoleCancelEventArgs error)
    {
        error.Cancel = true;
        lock (cancellationGate)
            if (active is not null) active.Cancel();
            else Volatile.Write(ref interruptedInput, true);
    }

    private int EndOfInput()
    {
        if (pending.Length == 0) return 0;
        try { Parser.Parse(pending, "<repl>"); }
        catch (SyntaxException error) { TerminalPresentation.Error(error, color); }
        return 1;
    }

    private bool Command(string source)
    {
        string command = source.Trim();
        if (command is "/help" or "/?")
        {
            TerminalHelp.Repl(color);
            return true;
        }
        if (command == "/syntax") { TerminalHelp.Syntax(TerminalPresentation.Colors(color)); return true; }
        if (command == "/pwd") { Console.WriteLine(execution.WorkingDirectory); return true; }
        if (command == "/clear") { if (!Console.IsOutputRedirected) Console.Clear(); return true; }
        if (command != "/cd" && !command.StartsWith("/cd ", StringComparison.Ordinal)) return false;
        string path = command.Length == 3 ? HostContext.HomeDirectory : command[4..].Trim();
        if (path.Length == 0) throw new IOException("The home directory is unavailable; use /cd PATH.");
        if (path.StartsWith('"'))
        {
            var parsed = Parser.Parse(path, "<cd>");
            if (parsed.Statements is not [ExpressionStatement { Expression: StringExpr value }])
                throw new ArgumentException("/cd requires a path or a quoted String.");
            path = value.Value;
        }
        if (path == "~") path = HostContext.HomeDirectory;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(HostContext.HomeDirectory, path[2..]);
        execution.ChangeWorkingDirectory(Path.GetFullPath(path, execution.WorkingDirectory));
        return true;
    }

}
