namespace Msync.Cli;

internal static class SshTerminalPrompt
{
    public static Task<string?> ReadAsync(SshPrompt request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) throw new IOException("Native SSH authentication needs an interactive terminal or a configured SSH askpass handler.");
        Console.Error.WriteLine($"SSH {request.Endpoint}");
        Console.Error.Write(request.Prompt);
        if (request.Kind == "notice") { Console.Error.WriteLine(); return Task.FromResult<string?>(""); }
        ForegroundLine line = ForegroundLineInput.Read(request.Kind == "secret" ? null : Console.Error,
            ForegroundLineMode.Credential, cancellationToken);
        Console.Error.WriteLine();
        return Task.FromResult(line.End == ForegroundLineEnd.Enter ? line.Text : null);
    }
}
