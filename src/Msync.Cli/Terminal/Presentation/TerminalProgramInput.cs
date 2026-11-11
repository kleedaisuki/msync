using System.Text;

namespace Msync.Cli;

// The REPL has finished editing before a program borrows the foreground terminal.
internal sealed class TerminalProgramInput : TextReader
{
    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ForegroundLine line = Read(cancellationToken);
        return ValueTask.FromResult<string?>(line.End == ForegroundLineEnd.EndOfInput && line.Text.Length == 0
            ? null : line.Text);
    }

    public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        while (true)
        {
            ForegroundLine line = Read(cancellationToken);
            text.Append(line.Text);
            if (line.End == ForegroundLineEnd.EndOfInput) return Task.FromResult(text.ToString());
            text.AppendLine();
        }
    }

    private static ForegroundLine Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TextWriter? echo = !Console.IsOutputRedirected ? Console.Out
            : !Console.IsErrorRedirected ? Console.Error : null;
        try
        {
            ForegroundLine line = ForegroundLineInput.Read(echo, ForegroundLineMode.Text, token);
            if (line.End == ForegroundLineEnd.Dismissed) throw new OperationCanceledException(token);
            return line;
        }
        finally { echo?.WriteLine(); }
    }
}
