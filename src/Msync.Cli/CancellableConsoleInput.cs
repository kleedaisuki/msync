using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Msync.Cli;

internal sealed class CancellableConsoleInput : IDisposable
{
    private readonly CancellationToken cancellationToken;
    private readonly WaitHandle[]? windows;
    private readonly int[]? pipe;
    private readonly PollDescriptor[]? descriptors;
    private readonly CancellationTokenRegistration cancellation;

    public CancellableConsoleInput(CancellationToken cancellationToken)
    {
        this.cancellationToken = cancellationToken;
        if (OperatingSystem.IsWindows())
        {
            windows = [cancellationToken.WaitHandle, new ConsoleWaitHandle()];
            return;
        }
        pipe = new int[2];
        if (Pipe(pipe) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        foreach (int descriptor in pipe)
            if (SetDescriptorFlags(descriptor, 2, 1) < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                Dispose();
                throw new Win32Exception(error);
            }
        descriptors = [new() { Descriptor = 0, Events = 1 }, new() { Descriptor = pipe[0], Events = 1 }];
        cancellation = cancellationToken.Register(() => _ = Write(pipe[1], [1], 1));
    }

    public ConsoleKeyInfo ReadKey()
    {
        while (!Console.KeyAvailable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (windows is not null)
            {
                if (WaitHandle.WaitAny(windows) == 0) cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            int ready = Poll(descriptors!, 2, -1);
            if (ready < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 4) continue;
                throw new Win32Exception(error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if ((descriptors![0].Returned & (8 | 16 | 32)) != 0)
                throw new EndOfStreamException("Terminal input closed during SSH authentication.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Console.ReadKey(intercept: true);
    }

    public void Dispose()
    {
        cancellation.Dispose();
        if (windows is not null) windows[1].Dispose();
        if (pipe is not null)
            foreach (int descriptor in pipe) _ = Close(descriptor);
    }

    private sealed class ConsoleWaitHandle : WaitHandle
    {
        public ConsoleWaitHandle() => SafeWaitHandle = new SafeWaitHandle(GetStdHandle(-10), ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short Returned;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int kind);
    [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
    private static extern int Pipe([Out] int[] descriptors);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int SetDescriptorFlags(int descriptor, int command, int flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll([In, Out] PollDescriptor[] descriptors, nuint count, int milliseconds);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int descriptor, byte[] bytes, nuint count);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
