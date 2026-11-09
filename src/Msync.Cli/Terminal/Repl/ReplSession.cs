using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

namespace Msync.Cli;

internal sealed class ReplSession(ExecutionOptions options, int optimization, string color)
{
    private static readonly string ShellPrelude = CreateShellPrelude();
    private readonly CompilerSession compiler = new(optimization: optimization);
    private readonly ExecutionSession execution = new(options);
    private readonly SubmissionSyntax syntax = new();
    private readonly object cancellationGate = new();
    private CancellationTokenSource? active;
    private string pending = "";
    private bool interruptedInput;
    private bool editing;

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
        await ExecuteAsync(ShellPrelude, CancellationToken.None);
        await using InteractivePrompt? prompt = InteractivePrompt.IsSupported
            ? new(syntax.IsIncomplete, PromptNames, color: TerminalPresentation.Colors(color), describe: DescribePromptBindings, analyze: source => compiler.AnalyzeEditor(source), colorPolicy: color,
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
            await execution.DisposeAsync();
        }
    }

    private IReadOnlyList<SemanticMember> DescribePromptBindings() => compiler.DescribeBindings()
        .Concat(compiler.DescribeConcepts()).DistinctBy(member => member.Name, StringComparer.Ordinal).ToArray();

    private IReadOnlyCollection<string> PromptNames() => compiler.BindingNames.Where(name => !name.StartsWith('$')).ToArray();

    private async ValueTask<TerminalInput> ReadAsync(InteractivePrompt? prompt)
    {
        if (prompt is not null) return await prompt.ReadAsync();
        if (!Console.IsInputRedirected) Console.Write(pending.Length == 0 ? "> " : "· ");
        string? line = Console.ReadLine();
        if (line is null) return new(TerminalInputKind.EndOfInput, "", CancellationToken.None);
        if (Volatile.Read(ref interruptedInput)) { pending = ""; Volatile.Write(ref interruptedInput, false); }
        return new(TerminalInputKind.Text, pending + line + "\n", CancellationToken.None);
    }

    private async ValueTask<bool> SubmitAsync(TerminalInput input)
    {
        string source = input.Text;
        if (source.Trim() is "/quit" or "/exit") return true;
        try
        {
            if (!Command(source)) await ExecuteAsync(source, input.CancellationToken);
            pending = "";
        }
        catch (SyntaxException error) when (!editing && error.IsIncomplete) { pending = source; }
        catch (OperationCanceledException)
        {
            TerminalPresentation.Cancelled(color, interactive: true);
            pending = "";
        }
        catch (Exception error) when (error is SyntaxException or CompileException or MsyncException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            TerminalPresentation.Error(error, color);
            pending = "";
        }
        return false;
    }

    private async ValueTask ExecuteAsync(string source, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (cancellationGate) active = cancellation;
        try
        {
            Module module;
            try { module = compiler.Compile(source, syntax.Take(source)); }
            catch
            {
                compiler.ReconcileSubmission(_ => false);
                throw;
            }
            try
            {
                Value result = await execution.ExecuteAsync(module, cancellationToken: cancellation.Token);
                if (result is not NullValue) await DisplayAsync(result, cancellation.Token);
                compiler.AcceptSubmission(execution.GetLocalValue);
            }
            catch
            {
                compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                throw;
            }
        }
        finally { lock (cancellationGate) active = null; }
    }

    private static async ValueTask DisplayAsync(Value value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is RangeValue range)
        {
            await foreach (Value item in range.Items.WithCancellation(cancellationToken))
                await DisplayAsync(item, cancellationToken);
            return;
        }
        string text = FormatValue(value);
        if (text.EndsWith('\n')) Console.Write(text);
        else Console.WriteLine(text);
    }

    private static string FormatValue(Value value, HashSet<ScopeValue>? path = null) => value switch
    {
        ScopeValue scope => FormatScope(scope, path ?? []),
        ProcessValue process => FormatProcess(process),
        StringValue text => text.Data,
        _ => value.ToString() ?? "null"
    };

    private static string FormatProcess(ProcessValue process)
    {
        if (process.Completion is not { } completion) return process.ToString();
        if (completion.IsCompletedSuccessfully) return completion.Result.ToString();
        string state = completion.IsCanceled ? "cancelled" : completion.IsFaulted ? "failed" : "pending";
        return process.ProcessId is { } id ? $"Process({state}, pid={id})" : $"Process({state})";
    }

    private static string FormatScope(ScopeValue scope, HashSet<ScopeValue> path)
    {
        if (!path.Add(scope)) return "scope { ...cycle... }";
        string result = "scope { " + string.Join(", ", scope.Select(member => member.Key + ": "
            + (member.Value is StringValue text ? "\"" + text.Data.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : FormatValue(member.Value, path)))) + " }";
        path.Remove(scope);
        return result;
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
