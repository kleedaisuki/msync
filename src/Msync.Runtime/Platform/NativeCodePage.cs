using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Msync.Runtime;

internal sealed partial class NativeCodePage : SafeHandleZeroOrMinusOneIsInvalid
{
    private const int Read = 1, Write = 2, Execute = 4;
    private const int PrivateAnonymous = 0x02 | 0x20;
    private const uint CommitReserve = 0x1000 | 0x2000, Release = 0x8000;
    private const uint ReadWrite = 0x04, ExecuteRead = 0x20;

    private readonly nuint length;
    private readonly bool windows;

    private NativeCodePage(nuint length, bool windows) : base(ownsHandle: true)
    {
        this.length = length;
        this.windows = windows;
    }

    internal static bool IsSupported => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
        Architecture.Arm64 => OperatingSystem.IsLinux() && LinuxArm64.Available,
        _ => false,
    };

    internal static unsafe NativeCodePage? Create(ReadOnlySpan<byte> code)
    {
        if (!IsSupported || code.IsEmpty) return null;
        bool windows = OperatingSystem.IsWindows();
        nuint length = (nuint)code.Length;
        NativeCodePage page = new(length, windows);
        // Do not request Linux PROT_READ while writing: READ_IMPLIES_EXEC could add execute access.
        page.SetHandle(windows
            ? VirtualAlloc(0, length, CommitReserve, ReadWrite)
            : Mmap(0, length, Write, PrivateAnonymous, -1, 0));
        if (page.IsInvalid) return null;

        code.CopyTo(new Span<byte>((void*)page.handle, code.Length));
        if (page.Seal()) return page;
        page.Dispose();
        return null;
    }

    private bool Seal()
    {
        if (!windows)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                return Mprotect(handle, length, Read | Execute) == 0;
            LinuxArm64.Clear(handle, length);
            return Mprotect(handle, length, Read | Execute) == 0 && LinuxArm64.Publish();
        }
        // Windows requires explicit instruction-cache publication even on x64.
        return VirtualProtect(handle, length, ExecuteRead, out _)
            && FlushInstructionCache(GetCurrentProcess(), handle, length);
    }

    internal unsafe int Run(double* registers, int fuel, ulong* counts, int resume)
    {
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            return ((delegate* unmanaged[Cdecl]<double*, int, ulong*, int, int>)handle)(registers, fuel, counts, resume);
        }
        finally
        {
            if (retained) DangerousRelease();
        }
    }

    internal unsafe double RunRows(double* rows, int count, double* inputs, double accumulator)
    {
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            return ((delegate* unmanaged[Cdecl]<double*, int, double*, double, double>)handle)(
                rows, count, inputs, accumulator);
        }
        finally
        {
            if (retained) DangerousRelease();
        }
    }

    internal unsafe int RunWorker(byte* rows, ulong* slots, int resume, int fuel)
    {
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            return ((delegate* unmanaged[Cdecl]<byte*, ulong*, int, int, int>)handle)(rows, slots, resume, fuel);
        }
        finally
        {
            if (retained) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle() => windows
        ? VirtualFree(handle, 0, Release)
        : Munmap(handle, length) == 0;

    private static partial class LinuxArm64
    {
        private const int Membarrier = 283;
        private const int SyncCore = 1 << 5, RegisterSyncCore = 1 << 6;
        private static readonly nint clearCache;

        static LinuxArm64() => clearCache = Load();

        internal static bool Available => clearCache != 0;

        private static nint Load()
        {
            if (!NativeLibrary.TryLoad("libgcc_s.so.1", out nint library)) return 0;
            // Keep the one system-library reference for the process lifetime.
            if (NativeLibrary.TryGetExport(library, "__clear_cache", out nint entry)
                && Syscall(Membarrier, RegisterSyncCore, 0, 0) == 0) return entry;
            NativeLibrary.Free(library);
            return 0;
        }

        internal static unsafe void Clear(nint address, nuint length) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)clearCache)(address, address + (nint)length);

        // The compiling thread's ISB cannot synchronize another thread's frontend.
        // Publish only after the kernel has synchronized this process's threads.
        internal static bool Publish() => Syscall(Membarrier, SyncCore, 0, 0) == 0;

        [LibraryImport("libc", EntryPoint = "syscall")]
        private static partial nint Syscall(nint number, nint command, nint flags, nint cpu);
    }

    [LibraryImport("libc", EntryPoint = "mmap")]
    private static partial nint Mmap(nint address, nuint length, int protection, int flags, int file, nint offset);
    [LibraryImport("libc", EntryPoint = "mprotect")]
    private static partial int Mprotect(nint address, nuint length, int protection);
    [LibraryImport("libc", EntryPoint = "munmap")]
    private static partial int Munmap(nint address, nuint length);

    [LibraryImport("kernel32.dll")]
    private static partial nint VirtualAlloc(nint address, nuint length, uint allocation, uint protection);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint address, nuint length, uint protection, out uint previous);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint length, uint freeType);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(nint process, nint address, nuint length);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
