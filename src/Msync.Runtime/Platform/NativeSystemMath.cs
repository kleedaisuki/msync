using System.Runtime.InteropServices;

namespace Msync.Runtime;

internal static class NativeSystemMath
{
    internal static readonly nint Remainder = LinkRemainder();

    private static nint LinkRemainder()
    {
        nint library = 0;
        bool loaded = OperatingSystem.IsWindows()
            ? NativeLibrary.TryLoad("ucrtbase.dll", typeof(NativeSystemMath).Assembly, DllImportSearchPath.System32, out library)
            : OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libm.so.6", out library);
        if (!loaded) return 0;
        // Generated code embeds the entry, so the library stays loaded.
        if (NativeLibrary.TryGetExport(library, "fmod", out nint address)) return address;
        NativeLibrary.Free(library);
        return 0;
    }
}
