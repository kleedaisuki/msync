using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Msync.Runtime;

internal sealed unsafe class NativePosixInputStream : Stream
{
    private readonly Descriptor handle;
    private readonly string path;

    internal NativePosixInputStream(string path)
    {
        this.path = path;
        int flags = OperatingSystem.IsMacOS() ? 0x4 | 0x1000000 : 0x800 | 0x80000;
        int fd = Open(path, flags);
        if (fd < 0) throw Error(Marshal.GetLastPInvokeError());
        handle = new(fd);
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        new(Task.Run(() => ReadCore(buffer.Span, token), token));

    private int ReadCore(Span<byte> buffer, CancellationToken token)
    {
        if (buffer.IsEmpty) return 0;
        PollFd descriptor = new() { Fd = handle.DangerousGetHandle().ToInt32(), Events = 1 };
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int ready = Poll(ref descriptor, 1, token.CanBeCanceled ? 50 : -1);
            if (ready == 0) continue;
            if (ready < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 4) continue;
                throw Error(error);
            }
            if ((descriptor.Returned & 1) == 0 && (descriptor.Returned & 16) != 0) return 0;
            if ((descriptor.Returned & (8 | 32)) != 0) throw new IOException($"Stream '{path}' poll failed with events {descriptor.Returned}.");
            nint count;
            fixed (byte* bytes = buffer) count = ReadNative(handle, bytes, (nuint)buffer.Length);
            if (count >= 0) return (int)count;
            int failure = Marshal.GetLastPInvokeError();
            if (failure == 4 || failure == (OperatingSystem.IsMacOS() ? 35 : 11)) continue;
            throw Error(failure);
        }
    }

    private IOException Error(int code)
    {
        Win32Exception error = new(code);
        return new($"Stream '{path}': {error.Message}", error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) handle.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { internal int Fd; internal short Events; internal short Returned; }
    private sealed class Descriptor : SafeHandleMinusOneIsInvalid
    {
        internal Descriptor(int fd) : base(true) => SetHandle(fd);
        protected override bool ReleaseHandle() => NativePosixInputStream.Close(handle.ToInt32()) == 0;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollFd descriptors, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadNative(Descriptor descriptor, byte* buffer, nuint count);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
