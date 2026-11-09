using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Msync.Runtime;

internal readonly record struct NativeTimestamp(long Seconds, long Nanoseconds)
{
    internal string Pax => (Seconds + Nanoseconds / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture);
    internal DateTime UtcDateTime => DateTime.UnixEpoch.AddTicks(checked(Seconds * TimeSpan.TicksPerSecond + Nanoseconds / 100));
    internal static NativeTimestamp FromDateTime(DateTime timestamp)
    {
        long ticks = timestamp.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks;
        long seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        if (remainder < 0) { seconds--; remainder += TimeSpan.TicksPerSecond; }
        return new(seconds, remainder * 100);
    }
    internal static NativeTimestamp FromPax(string timestamp)
    {
        decimal value = decimal.Parse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture);
        decimal seconds = decimal.Floor(value);
        return new(decimal.ToInt64(seconds), decimal.ToInt64((value - seconds) * 1_000_000_000m));
    }
    internal void Apply(string target, bool directory = false, bool link = false)
    {
        if (OperatingSystem.IsLinux()) { NativeFileMetadata.SetTimestamp(target, this, link); return; }
        if (link)
        {
            if (OperatingSystem.IsWindows()) NativeFileMetadata.SetWindowsLinkTimestamp(target, this);
            return;
        }
        if (directory) Directory.SetLastWriteTimeUtc(target, UtcDateTime);
        else File.SetLastWriteTimeUtc(target, UtcDateTime);
    }
}

internal readonly record struct FileMetadata(FileAttributes Attributes, long Length, UnixFileMode Mode, NativeTimestamp Modified)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    internal bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
    internal void Apply(string target)
    {
        Modified.Apply(target, IsDirectory, IsLink);
        if (!OperatingSystem.IsWindows() && !IsLink) File.SetUnixFileMode(target, Mode);
    }
}

internal static partial class NativeFileMetadata
{
    internal static FileMetadata Capture(string path)
    {
        if (OperatingSystem.IsLinux()) return CaptureLinux(path);
        var info = new FileInfo(path);
        FileAttributes attributes = info.Attributes;
        bool ordinary = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        return new(attributes, ordinary ? info.Length : 0, OperatingSystem.IsWindows() ? default : info.UnixFileMode, NativeTimestamp.FromDateTime(info.LastWriteTimeUtc));
    }
    private static FileMetadata CaptureLinux(string path)
    {
        if (Statx(-100, path, 0x100, 0x243, out var status) == 0)
            return FromLinux(status.Mode, status.Length, status.ModifiedSeconds, status.ModifiedNanoseconds);
        int error = Marshal.GetLastPInvokeError();
        if (error is 1 or 38) return CaptureLegacyLinux(path);
        throw new IOException($"Cannot inspect '{path}': {new Win32Exception(error).Message}");
    }
    private static FileMetadata FromLinux(uint mode, long length, long seconds, long nanoseconds)
    {
        FileAttributes attributes = (mode & 0xf000) switch
        {
            0x4000 => FileAttributes.Directory,
            0xa000 => FileAttributes.ReparsePoint,
            _ => FileAttributes.Normal
        };
        return new(attributes, length, (UnixFileMode)(mode & 0xfff), new(seconds, nanoseconds));
    }
    private static unsafe FileMetadata CaptureLegacyLinux(string path)
    {
        Span<byte> bytes = stackalloc byte[512];
        fixed (byte* buffer = bytes)
            if (Lstat(path, buffer) != 0) throw new IOException($"Cannot inspect '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        int modeOffset = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16;
        return FromLinux(BitConverter.ToUInt32(bytes[modeOffset..]), BitConverter.ToInt64(bytes[48..]), BitConverter.ToInt64(bytes[88..]), BitConverter.ToInt64(bytes[96..]));
    }
    internal static void SetTimestamp(string path, NativeTimestamp timestamp, bool link)
    {
        var times = new FileTimes(new(0, 1_073_741_822), new(timestamp.Seconds, timestamp.Nanoseconds));
        if (UtimensAt(-100, path, in times, link ? 0x100 : 0) != 0)
            throw new IOException($"Cannot set modification time on '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }
    internal static void SetWindowsLinkTimestamp(string path, NativeTimestamp timestamp)
    {
        using SafeFileHandle handle = OpenWindowsLink(WindowsPath(path), 0x100, 7, 0, 3, 0x02200000, 0);
        long modified = timestamp.UtcDateTime.ToFileTimeUtc();
        if (handle.IsInvalid || !SetFileTime(handle, 0, 0, in modified))
            throw new IOException($"Cannot set link modification time on '{path}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }
    internal static string WindowsPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return path;
        string absolute = Path.GetFullPath(path);
        return absolute.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + absolute[2..] : "\\\\?\\" + absolute;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Status
    {
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public long Length;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Timespec(long seconds, long nanoseconds)
    { public readonly long Seconds = seconds; public readonly long Nanoseconds = nanoseconds; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTimes(Timespec accessed, Timespec modified)
    { public readonly Timespec Accessed = accessed; public readonly Timespec Modified = modified; }
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out Status status);
    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Lstat(string path, byte* status);
    [LibraryImport("libc", EntryPoint = "utimensat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UtimensAt(int directory, string path, in FileTimes times, int flags);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenWindowsLink(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileTime(SafeFileHandle handle, nint creation, nint accessed, in long modified);
}
