using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

internal sealed class RemoteProcessControl(Process process, int id) : IProcessControl
{
    private readonly SemaphoreSlim inputGate = new(1);
    private bool inputClosed;
    private int completed;
    public int ProcessId => id;
    public bool IsRunning
    {
        get
        {
            if (Volatile.Read(ref completed) != 0) return false;
            try { return !process.HasExited; }
            catch (InvalidOperationException) when (Volatile.Read(ref completed) != 0) { return false; }
        }
    }
    internal void Complete() => Volatile.Write(ref completed, 1);

    internal static ProcessStartInfo StartInfo(ConnectionValue connection, string executable, IReadOnlyList<string> arguments) =>
        TransferEngine.CreateRemoteProcessStartInfo(connection, "/bin/sh", ["-c", ShellProgram.Source(Program), "msync-process", executable, .. arguments]);

    internal static async Task<(int Id, string Diagnostic)> ReadIdentityAsync(Process process, CancellationToken token)
    {
        const string prefix = "MSYNC-PROCESS-1 ";
        byte[] next = new byte[1];
        using MemoryStream line = new();
        StringBuilder diagnostic = new();
        while (await process.StandardError.BaseStream.ReadAsync(next, token) != 0)
        {
            line.WriteByte(next[0]);
            if (next[0] != (byte)'\n') continue;
            string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
            if (text.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(text.AsSpan(prefix.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
                return (pid, diagnostic.ToString());
            diagnostic.Append(text); line.SetLength(0);
        }
        diagnostic.Append(Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)));
        throw new IOException($"The remote process did not publish its identity: {diagnostic.ToString().TrimEnd()}");
    }

    public async ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (inputClosed || Volatile.Read(ref completed) != 0) throw new MsyncException("The remote Process input pipe has been closed.");
            string bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            await process.StandardInput.WriteLineAsync(("W " + bytes).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { inputGate.Release(); }
    }
    public async ValueTask CloseInputAsync(CancellationToken cancellationToken)
    {
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (inputClosed || Volatile.Read(ref completed) != 0) return;
            inputClosed = true;
            await process.StandardInput.WriteLineAsync("C".AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { inputGate.Release(); }
    }
    public bool Kill() => Signal("K");
    public bool Terminate() => Signal("T");
    private bool Signal(string operation)
    {
        if (Volatile.Read(ref completed) != 0) return false;
        inputGate.Wait();
        try
        {
            if (Volatile.Read(ref completed) != 0) return false;
            process.StandardInput.WriteLine(operation);
            process.StandardInput.Flush();
            return true;
        }
        finally { inputGate.Release(); }
    }

    private const string Program = """
        set -eu
        expand() { case "$1" in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#??}";; *) printf '%s' "$1";; esac; }
        executable=$(expand "$1"; printf .); executable=${executable%.}
        shift
        work=$(mktemp -d "${TMPDIR:-/tmp}/msync-process-XXXXXXXXXX")
        child=
        reader=
        grouped=false
        cleanup() {
            if [ -n "$reader" ]; then kill "$reader" 2>/dev/null || :; fi
            if [ -n "$child" ]; then
                if "$grouped"; then kill -KILL "-$child" 2>/dev/null || :; else kill -KILL "$child" 2>/dev/null || :; fi
            fi
            rm -f -- "$work/input" "$work/start"
            rmdir -- "$work"
        }
        trap cleanup EXIT HUP INT TERM
        mkfifo "$work/input" "$work/start"
        exec 3<>"$work/input"
        exec 4<&0
        if command -v setsid >/dev/null 2>&1; then
            setsid sh -c 'gate=$1; shift; IFS= read -r go < "$gate"; exec "$@"' sh "$work/start" "$executable" "$@" 3>&- 4<&- < "$work/input" &
            grouped=true
        else
            sh -c 'gate=$1; shift; IFS= read -r go < "$gate"; exec "$@"' sh "$work/start" "$executable" "$@" 3>&- 4<&- < "$work/input" &
        fi
        child=$!
        printf 'MSYNC-PROCESS-1 %s\n' "$child" >&2
        printf 'start\n' > "$work/start"
        (
            while IFS= read -r command; do
                case "$command" in
                    'W '*) printf '%s' "${command#W }" | base64 -d >&3;;
                    C) exec 3>&-;;
                    T) if "$grouped"; then kill -TERM "-$child"; else kill -TERM "$child"; fi;;
                    K) if "$grouped"; then kill -KILL "-$child"; else kill -KILL "$child"; fi;;
                esac
            done
            exec 3>&-
        ) <&4 &
        reader=$!
        exec 3>&-
        exec 4<&-
        set +e
        wait "$child"
        status=$?
        child=
        kill "$reader" 2>/dev/null
        wait "$reader" 2>/dev/null
        reader=
        exit "$status"
        """;
}
