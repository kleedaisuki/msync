using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Msync.Runtime;

internal static class NativeFileIdentity
{
    internal static void CreateHardLink(string link, string target)
    {
        bool success = OperatingSystem.IsWindows() ? CreateHardLinkWindows(link, target, IntPtr.Zero) : Link(target, link) == 0;
        if (!success) throw new IOException($"Cannot create hard link '{link}' to '{target}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    internal static bool Equivalent(string left, string right)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle first = OpenWindows(left);
            using SafeFileHandle second = OpenWindows(right);
            if (!GetFileInformationByHandle(first, out FileInformation a) || !GetFileInformationByHandle(second, out FileInformation b))
                throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
        }
        byte[] firstStat = StatBytes(left, false) ?? throw new FileNotFoundException(left), secondStat = StatBytes(right, false) ?? throw new FileNotFoundException(right);
        int deviceSize = OperatingSystem.IsMacOS() ? 4 : 8;
        return firstStat.AsSpan(0, deviceSize).SequenceEqual(secondStat.AsSpan(0, deviceSize)) && firstStat.AsSpan(8, 8).SequenceEqual(secondStat.AsSpan(8, 8));
    }

    internal static string Kind(string path, bool noFollow)
    {
        byte[]? data = StatBytes(path, noFollow);
        if (data is null) return "not_found";
        uint mode = OperatingSystem.IsMacOS() ? BitConverter.ToUInt16(data, 4) : BitConverter.ToUInt32(data,
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16);
        return (mode & 0xf000) switch
        {
            0x8000 => "regular",
            0x4000 => "directory",
            0xa000 => "symlink",
            0x6000 => "block",
            0x2000 => "character",
            0x1000 => "fifo",
            0xc000 => "socket",
            _ => "unknown"
        };
    }

    private static byte[]? StatBytes(string path, bool noFollow)
    {
        // libc's stat layout is architecture-defined; reserve more than its public ABI structure.
        byte[] buffer = new byte[512];
        int result = noFollow ? Lstat(path, buffer) : Stat(path, buffer);
        if (result != 0 && Marshal.GetLastPInvokeError() is 2 or 20) return null;
        if (result != 0) throw new IOException($"Cannot inspect '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        return buffer;
    }

    private static SafeFileHandle OpenWindows(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException($"Cannot inspect '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int Lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string link, string target, IntPtr securityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
