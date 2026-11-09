using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static async ValueTask<Value> ProcessOperationAsync(string name, IReadOnlyList<Value> args, CancellationToken token)
    {
        if (name == "spawn") throw new MsyncException("process.spawn requires an interpreter execution context.");
        ProcessValue value = args[0] as ProcessValue ?? throw new MsyncException("Expected Process");
        if (name == "wait") return value.Completion is null ? value : await value.Completion.WaitAsync(token);
        if (name == "pid") return new NumberValue(value.ProcessId ?? value.Control?.ProcessId
            ?? throw new MsyncException("This Process value has no operating-system identity; use process.spawn for controlled execution."));
        if (name == "running") return Bool(value.Control?.IsRunning ?? value.Completion is { IsCompleted: false });
        if (name is "kill" or "terminate")
        {
            if (value.Control is { } control) return Bool(name == "kill" ? control.Kill() : control.Terminate());
            if (value.Completion is { IsCompleted: false })
                throw new MsyncException("This Process has no control channel; use process.spawn for managed remote execution.");
            return Value.False;
        }
        if (name == "write_stdin")
        {
            IProcessControl control = value.Control ?? throw new MsyncException("This Process has no writable input pipe; use process.spawn.");
            await control.WriteInputAsync(Text(args[1]), token);
            return Value.Null;
        }
        if (name == "close_stdin")
        {
            if (value.Control is { } control) await control.CloseInputAsync(token);
            return Value.Null;
        }
        if (value.Completion is not null) value = await value.Completion.WaitAsync(token);
        return ProcessOperation(name, [value]);
    }
}

internal sealed partial class LocalProcessControl(Process process, bool input) : IProcessControl
{
    private readonly int id = process.Id;
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
    public async ValueTask WriteInputAsync(string text, CancellationToken cancellationToken)
    {
        if (!input) throw new MsyncException("This Process inherits standard input; use process.spawn to create a writable input pipe.");
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (inputClosed || Volatile.Read(ref completed) != 0) throw new MsyncException("The Process input pipe has been closed.");
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { inputGate.Release(); }
    }
    public async ValueTask CloseInputAsync(CancellationToken cancellationToken)
    {
        if (!input) return;
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (inputClosed || Volatile.Read(ref completed) != 0) return;
            inputClosed = true;
            process.StandardInput.Close();
        }
        finally { inputGate.Release(); }
    }
    public bool Kill()
    {
        if (!IsRunning) return false;
        try { process.Kill(entireProcessTree: true); return true; }
        catch (InvalidOperationException) when (!IsRunning) { return false; }
    }
    public bool Terminate()
    {
        if (!IsRunning) return false;
        if (OperatingSystem.IsWindows())
        {
            try { return process.CloseMainWindow(); }
            catch (InvalidOperationException) when (!IsRunning) { return false; }
        }
        if (SendSignal(id, 15) == 0) return true;
        int error = Marshal.GetLastPInvokeError();
        if (error == 3) return false;
        throw new IOException($"Cannot terminate process {id}.", new Win32Exception(error));
    }
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int pid, int signal);
}
