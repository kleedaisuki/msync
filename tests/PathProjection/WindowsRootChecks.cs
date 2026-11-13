using Msync.Core;
using Msync.Core.Native;

internal static class WindowsRootChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run the captured-drive checks on Windows.");
        Check(@"D:\", new NativePath(@"\", @"D:\captured").LocalPath(@"C:\later"));
        Check(@"D:\nested\file", new NativePath(@"\nested\file", @"D:\captured").LocalPath(@"C:\later"));
        Check(@"C:\nested\file", new NativePath(@"\nested\file").LocalPath(@"C:\later"));
        Check(@"D:\captured\relative", new NativePath("relative", @"D:\captured").LocalPath(@"C:\later"));
        Check(@"E:\absolute", new NativePath(@"E:\absolute", @"D:\captured").LocalPath(@"C:\later"));
        Check(@"\\server\share\file", new NativePath(@"\\server\share\file", @"D:\captured").LocalPath(@"C:\later"));
        Check(@"\\?\D:\literal\..\file", new NativePath(@"\\?\D:\literal\..\file", @"D:\captured").LocalPath(@"C:\later"));
        Console.WriteLine("Windows root-relative paths retain their captured drive; fully qualified paths remain unchanged.");
    }

    private static void Check(string expected, string actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}
