using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Msync.Runtime;

internal static partial class NativeAccess
{
    internal static bool Executable(string path) => Access(path, 1) == 0;

    internal static void SearchDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || Access(path, 1) == 0) return;
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        throw new IOException($"Cannot cd to '{path}': {error.Message}.", error);
    }

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);
}
