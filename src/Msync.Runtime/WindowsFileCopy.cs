using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Msync.Runtime;

internal static partial class WindowsFileCopy
{
    private sealed class CopyState(CancellationToken cancellationToken)
    {
        internal readonly CancellationToken CancellationToken = cancellationToken;
        internal nint Destination;
        internal Exception? Failure;
    }

    internal static unsafe void Copy(string source, string stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = new CopyState(cancellationToken);
        var owner = GCHandle.Alloc(state);
        try
        {
            bool copied = CopyFileEx(NativeFileMetadata.WindowsPath(source), NativeFileMetadata.WindowsPath(stage), &Progress, GCHandle.ToIntPtr(owner), 0, 1);
            int error = copied ? 0 : Marshal.GetLastPInvokeError();
            if (state.Failure is not null) throw state.Failure;
            cancellationToken.ThrowIfCancellationRequested();
            if (!copied) throw new IOException($"CopyFileExW: {new Win32Exception(error).Message}");
            if (!FlushFileBuffers(state.Destination)) throw new IOException($"FlushFileBuffers: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        finally
        {
            if (state.Destination != 0) CloseHandle(state.Destination);
            owner.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Progress(long total, long copied, long streamSize, long streamCopied, uint stream, uint reason,
        nint source, nint destination, nint context)
    {
        var state = (CopyState)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (reason == 1 && stream == 1 && state.Destination == 0)
            {
                nint process = GetCurrentProcess();
                if (!DuplicateHandle(process, destination, process, out nint retained, 0, false, 2))
                    throw new IOException($"DuplicateHandle: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                // Retain the native writable handle before readonly attributes are
                // installed; reopening the completed stage for fsync can fail.
                state.Destination = retained;
            }
            return state.CancellationToken.IsCancellationRequested ? 1u : 0;
        }
        catch (Exception error) { state.Failure = error; return 1; }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CopyFileEx(string source, string destination,
        delegate* unmanaged[Stdcall]<long, long, long, long, uint, uint, nint, nint, nint, uint> progress,
        nint data, nint cancel, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(nint file);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint destination,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
