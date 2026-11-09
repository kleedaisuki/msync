using System.Buffers;
using System.Diagnostics;
using System.Text;
using Msync.Core;
using Msync.Runtime;

namespace Msync.Cli;

internal sealed record RemoteDirectoryEntry(string Name, bool IsDirectory);

internal sealed class RemoteDirectoryCompletion(Func<SshPrompt, CancellationToken, Task<string?>>? prompt = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<ConnectionValue, Session> sessions = [];
    private bool disposed;

    public Task<IReadOnlyList<RemoteDirectoryEntry>> ListAsync(ConnectionValue endpoint, string directory, CancellationToken cancellationToken) =>
        ListAsync(endpoint, directory, "", cancellationToken);

    public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListAsync(ConnectionValue endpoint, string directory, string prefix, CancellationToken cancellationToken)
    {
        while (true)
        {
            Session session;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!sessions.TryGetValue(endpoint, out session!)) sessions[endpoint] = session = new(endpoint, prompt);
            }
            try { return await session.ListAsync(directory, prefix, cancellationToken); }
            catch (RetiredSessionException) { await RemoveAsync(endpoint, session); }
            catch
            {
                if (session.IsRetired) await RemoveAsync(endpoint, session);
                throw;
            }
        }
    }

    private async Task RemoveAsync(ConnectionValue endpoint, Session session)
    {
        lock (gate)
            if (sessions.TryGetValue(endpoint, out Session? current) && ReferenceEquals(current, session)) sessions.Remove(endpoint);
        await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Session[] owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = sessions.Values.ToArray();
            sessions.Clear();
        }
        foreach (Session session in owned) await session.DisposeAsync();
    }

    private sealed class RetiredSessionException : Exception;

    private sealed class Session(ConnectionValue endpoint, Func<SshPrompt, CancellationToken, Task<string?>>? prompt) : IAsyncDisposable
    {
        private readonly SemaphoreSlim serial = new(1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly byte[] buffer = new byte[8192];
        private Process? process;
        private SshPromptBroker? broker;
        private Task<string>? errors;
        private int position;
        private int available;
        private volatile bool retired;
        private volatile bool ready;
        private bool authenticationDismissed;
        private bool started;
        private Task? disposal;
        public bool IsRetired => retired;

        private sealed record QueryOutcome(IReadOnlyList<RemoteDirectoryEntry>? Entries, Exception? Error = null);

        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListAsync(string directory, string prefix, CancellationToken cancellationToken)
        {
            await serial.WaitAsync(cancellationToken);
            bool draining = false;
            try
            {
                if (retired) throw new RetiredSessionException();
                if (!ready)
                {
                    using CancellationTokenRegistration cancellation = cancellationToken.Register(() => { if (!ready) Retire(); });
                    try { await StartAsync(cancellationToken); }
                    catch (Exception startupError) { throw await FailureAsync(startupError, cancellationToken); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // A superseded document cancels its consumer, not the authenticated channel.
                // The query keeps serial ownership until its complete frame is drained.
                Task<QueryOutcome> query = QueryAsync(directory, prefix);
                draining = true;
                QueryOutcome result = await query.WaitAsync(cancellationToken);
                if (result.Error is { } error) throw error;
                return result.Entries!;
            }
            finally { if (!draining) serial.Release(); }
        }

        private async Task<QueryOutcome> QueryAsync(string directory, string prefix)
        {
            try
            {
                await WriteAsync("msync_directory " + Quote(directory) + " " + Quote(prefix) + "\n", lifetime.Token);
                List<RemoteDirectoryEntry> entries = [];
                string? failure = null;
                while (true)
                {
                    string record = await ReadRecordAsync(lifetime.Token);
                    switch (record[0])
                    {
                        case 'D': entries.Add(new(record[1..], true)); break;
                        case 'F': entries.Add(new(record[1..], false)); break;
                        case 'X': failure = record[1..]; break;
                        case 'E':
                            if (failure is not null) throw new IOException($"Cannot list directory '{directory}' on {endpoint}. {failure}".TrimEnd());
                            return new(entries);
                        default: throw new IOException($"Unexpected directory response from {endpoint}: '{record}'.");
                    }
                }
            }
            catch (Exception error) { return new(null, await FailureAsync(error, lifetime.Token)); }
            finally { serial.Release(); }
        }

        private async Task<Exception> FailureAsync(Exception error, CancellationToken cancellationToken)
        {
            bool cancelled = cancellationToken.IsCancellationRequested || authenticationDismissed;
            Retire();
            if (cancelled) return new OperationCanceledException(cancellationToken);
            string diagnostic = errors is null ? "" : await errors;
            return new IOException($"Directory completion on {endpoint} failed: {error.Message}"
                + (diagnostic.Length == 0 ? "" : "\n" + diagnostic.TrimEnd()), error);
        }

        private async Task<string?> PromptAsync(SshPrompt request, CancellationToken cancellationToken)
        {
            string? answer = await prompt!(request, cancellationToken);
            if (answer is null) { authenticationDismissed = true; Retire(); }
            return answer;
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            ProcessStartInfo start = TransferEngine.CreateRemoteShellProcessStartInfo(endpoint);
            start.RedirectStandardError = true;
            if (endpoint.Kind == ConnectionKind.Ssh && prompt is not null)
            {
                broker = new(PromptAsync, endpoint.ToString(), lifetime.Token);
                broker.Configure(start);
            }
            process = new() { StartInfo = start };
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException($"Cannot start {start.FileName} for {endpoint}.");
            started = true;
            errors = process.StandardError.ReadToEndAsync();
            string ready = "R" + Guid.NewGuid().ToString("N");
            await WriteAsync(ProgramText.Replace("\r\n", "\n", StringComparison.Ordinal) + "\nprintf '%s\\0' " + Quote(ready) + "\n", cancellationToken);
            while (!(await ReadRecordAsync(cancellationToken)).EndsWith(ready, StringComparison.Ordinal)) { }
            this.ready = true;
        }

        private async Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await process!.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }

        private async Task<string> ReadRecordAsync(CancellationToken cancellationToken)
        {
            ArrayBufferWriter<byte> record = new();
            while (true)
            {
                if (position == available)
                {
                    available = await process!.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                    position = 0;
                    if (available == 0) throw new EndOfStreamException($"Native directory channel for {endpoint} closed.");
                }
                int terminator = buffer.AsSpan(position, available - position).IndexOf((byte)0);
                int count = terminator < 0 ? available - position : terminator;
                record.Write(buffer.AsSpan(position, count));
                position += count;
                if (terminator < 0) continue;
                position++;
                if (record.WrittenCount == 0) continue;
                return Encoding.UTF8.GetString(record.WrittenSpan);
            }
        }

        private void Retire()
        {
            retired = true;
            lifetime.Cancel();
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        public ValueTask DisposeAsync()
        {
            lock (lifetime) return new(disposal ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            Retire();
            await serial.WaitAsync();
            try
            {
                if (process is not null)
                {
                    if (started) await process.WaitForExitAsync();
                    if (errors is not null) await errors;
                    process.Dispose();
                }
                if (broker is not null) await broker.DisposeAsync();
            }
            finally { serial.Release(); }
        }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private const string ProgramText = """
        msync_directory() (
            directory=$1
            prefix=$2
            case "$directory" in
                '~') directory=$HOME ;;
                '~/'*) directory=$HOME/${directory#\~/} ;;
                /*) ;;
                *) directory=$PWD/$directory ;;
            esac
            if ! cd "$directory"; then
                printf 'X\0E\0'
                exit
            fi
            if [ ! -r . ]; then
                printf 'XDirectory is not readable.\0E\0'
                exit
            fi
            for entry in ./* ./.[!.]* ./..?*; do
                name=${entry#./}
                case "$name" in "$prefix"*) ;; *) continue ;; esac
                [ -e "$entry" ] || [ -L "$entry" ] || continue
                if [ -d "$entry" ]; then
                    printf 'D%s\0' "$name"
                else
                    printf 'F%s\0' "$name"
                fi
            done
            printf 'E\0'
        )
        """;
}
