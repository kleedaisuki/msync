using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Msync.Cli;

internal sealed record SshPrompt(string Id, string Endpoint, string Prompt, string Kind);

internal sealed class SshPromptBroker : IAsyncDisposable
{
    private const string PipeVariable = "MSYNC_SSH_ASKPASS_PIPE";
    private readonly string name = "msync-ssh-" + Guid.NewGuid().ToString("N");
    private readonly Func<SshPrompt, CancellationToken, Task<string?>> prompt;
    private readonly string endpoint;
    private readonly CancellationTokenSource lifetime;
    private readonly NamedPipeServerStream pipe;
    private readonly Task listener;

    public SshPromptBroker(Func<SshPrompt, CancellationToken, Task<string?>> prompt, string endpoint, CancellationToken cancellationToken)
    {
        this.prompt = prompt;
        this.endpoint = endpoint;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        listener = ListenAsync();
    }

    public void Configure(ProcessStartInfo process)
    {
        if (process.Environment.TryGetValue("SSH_ASKPASS", out var configured) && !string.IsNullOrEmpty(configured)) return;
        if (process.Environment.TryGetValue("SSH_ASKPASS_REQUIRE", out var policy) && policy == "never") return;
        string? executable = Environment.ProcessPath;
        process.Environment["SSH_ASKPASS"] = executable is not null && Path.GetFileNameWithoutExtension(executable) != "dotnet"
            ? executable : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "msync.exe" : "msync");
        process.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        process.Environment[PipeVariable] = name;
        if (!process.Environment.TryGetValue("DISPLAY", out var display) || string.IsNullOrEmpty(display)) process.Environment["DISPLAY"] = "msync:0";
    }

    public static async ValueTask<int?> TryRunHelperAsync(string[] arguments)
    {
        string? name = Environment.GetEnvironmentVariable(PipeVariable);
        if (name is null) return null;
        try
        {
            await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            string text = string.Join(' ', arguments);
            string kind = PromptKind(Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT"), text);
            await WriteAsync(pipe, kind, CancellationToken.None);
            await WriteAsync(pipe, text, CancellationToken.None);
            string accepted = await ReadAsync(pipe, CancellationToken.None);
            string? value = accepted == "answer" ? await ReadAsync(pipe, CancellationToken.None) : null;
            await WriteAsync(pipe, "received", CancellationToken.None);
            if (value is null) return 1;
            Console.Out.Write(value);
            Console.Out.Flush();
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"msync SSH prompt: {error.Message}");
            return 1;
        }
    }

    private static string PromptKind(string? hint, string text)
    {
        if (hint == "confirm") return "confirm";
        if (hint == "none") return "notice";
        // OpenSSH's host-key confirm() uses RP_ECHO without the askpass permission hint.
        // https://github.com/openssh/openssh-portable/blob/master/sshconnect.c
        if (text.Contains("Are you sure you want to continue connecting (yes/no", StringComparison.Ordinal)
            || text.StartsWith("Please type 'yes' or 'no':", StringComparison.Ordinal)
            || text.StartsWith("Please type 'yes', 'no' or the fingerprint:", StringComparison.Ordinal)) return "confirm";
        return "secret";
    }

    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                string kind = await ReadAsync(pipe, lifetime.Token);
                string text = await ReadAsync(pipe, lifetime.Token);
                var request = new SshPrompt(Guid.NewGuid().ToString("N"), endpoint, text, kind);
                string? answer = await prompt(request, lifetime.Token);
                await WriteAsync(pipe, answer is null ? "cancel" : "answer", lifetime.Token);
                if (answer is not null) await WriteAsync(pipe, answer, lifetime.Token);
                _ = await ReadAsync(pipe, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (IOException) when (!lifetime.IsCancellationRequested) { }
            finally
            {
                if (pipe.IsConnected) pipe.Disconnect();
            }
        }
    }

    private static async Task<string> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        byte[] bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { await listener; }
        finally { await pipe.DisposeAsync(); lifetime.Dispose(); }
    }
}
