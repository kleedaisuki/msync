using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

namespace Msync.Cli;

internal sealed class ReplSession(ExecutionOptions options, int optimization, string color)
{
    private readonly CompilerSession compiler = new(optimization: optimization);
    private readonly ExecutionSession execution = new(options);
    private readonly SubmissionSyntax syntax = new();
    private readonly object cancellationGate = new();
    private CancellationTokenSource? active;
    private string pending = "";
    private bool interruptedInput;
    private bool editing;

    public async Task<int> RunAsync()
    {
        await using InteractivePrompt? prompt = InteractivePrompt.IsSupported
            ? new(syntax.IsIncomplete, () => compiler.BindingNames, color: TerminalPresentation.Colors(color), describe: DescribePromptBindings, analyze: source => compiler.AnalyzeEditor(source, execution.GetLocalValue), colorPolicy: color) : null;
        editing = prompt is not null;
        if (!Console.IsInputRedirected)
            Console.WriteLine(TerminalPresentation.Paint("msync 0.8.0", TerminalPresentation.HeadingColor, TerminalPresentation.Colors(color))
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
        finally { if (!editing) Console.CancelKeyPress -= Cancel; }
    }

    private IReadOnlyList<SemanticMember> DescribePromptBindings() => compiler.DescribeBindings()
        .Concat(compiler.DescribeConcepts()).DistinctBy(member => member.Name, StringComparer.Ordinal).ToArray();

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
                compiler.AcceptSubmission(execution.GetLocalValue);
                if (result is not NullValue) Console.WriteLine(result);
            }
            catch
            {
                compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                throw;
            }
        }
        finally { lock (cancellationGate) active = null; }
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
        if (command == "/pwd") { Console.WriteLine(Environment.CurrentDirectory); return true; }
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
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(HostContext.HomeDirectory, path[2..]);
        Environment.CurrentDirectory = path;
        return true;
    }

}
