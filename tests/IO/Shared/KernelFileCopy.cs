using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Msync.IO.Probes;

internal static class KernelFileCopy
{
    private const nuint WindowBytes = 1024 * 1024;
    private static readonly bool Supported = OperatingSystem.IsLinux();

    internal static bool TryCopy(FileStream input, FileStream output, long sourceLength, CancellationToken cancellationToken)
    {
        if (!Supported || sourceLength == 0) return false;
        var source = input.SafeFileHandle;
        var target = output.SafeFileHandle;
        bool sourceRetained = false, targetRetained = false;
        long copied = 0;
        try
        {
            source.DangerousAddRef(ref sourceRetained);
            target.DangerousAddRef(ref targetRetained);
            int sourceDescriptor = source.DangerousGetHandle().ToInt32();
            int targetDescriptor = target.DangerousGetHandle().ToInt32();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nuint window = copied < sourceLength ? (nuint)Math.Min((long)WindowBytes, sourceLength - copied) : WindowBytes;
                nint count = CopyRange(sourceDescriptor, IntPtr.Zero, targetDescriptor, IntPtr.Zero, window, 0);
                if (count > 0) { copied += count; continue; }
                if (count == 0 && copied >= sourceLength) return true;
                if (count < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 4) continue;
                    if (error is not (1 or 18 or 22 or 38 or 95))
                        throw new IOException($"copy_file_range after {copied} bytes: {new Win32Exception(error).Message}");
                }
                // FileStream owns a logical cursor separately from the descriptor.
                // Resume the prefix on the same handles, including false-zero kernels.
                input.Position = copied;
                output.Position = copied;
                return false;
            }
        }
        finally
        {
            if (targetRetained) target.DangerousRelease();
            if (sourceRetained) source.DangerousRelease();
        }
    }

    [DllImport("libc", EntryPoint = "copy_file_range", SetLastError = true)]
    private static extern nint CopyRange(int input, IntPtr inputOffset, int output, IntPtr outputOffset, nuint length, uint flags);
}
