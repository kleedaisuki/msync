using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Msync.IO.Probes;

if (!OperatingSystem.IsLinux()) return;
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "linux-copy-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    byte[] bytes = new byte[3 * 1024 * 1024 + 12345]; new Random(412).NextBytes(bytes);
    string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
    File.WriteAllBytes(source, bytes);
    using (FileStream input = Open(source, FileAccess.Read))
    using (FileStream output = Open(target, FileAccess.Write))
    {
        if (!KernelFileCopy.TryCopy(input, output, bytes.Length, CancellationToken.None)) Buffered(input, output);
        output.Flush(true);
    }
    Require(SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)), "native copy changed bytes");
    File.Delete(target);
    if (args.Contains("--partial-fallback", StringComparer.Ordinal))
    {
        Native.DenyShortCopyRanges();
        using FileStream input = Open(source, FileAccess.Read);
        using FileStream output = Open(target, FileAccess.Write);
        bool completed = KernelFileCopy.TryCopy(input, output, bytes.Length, CancellationToken.None);
        Require(!completed, "seccomp did not exercise the native remainder fallback");
        Require(output.Length == 3 * 1024 * 1024, "fallback did not retain the actual native prefix");
        Buffered(input, output); output.Flush(true);
        Require(SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)), "fallback repeated or lost the native prefix");
        Console.WriteLine("Native positive-prefix fallback preserved both FileStream cursors and exact bytes.");
    }
    else
    {
        using FileStream input = Open("/proc/self/cmdline", FileAccess.Read);
        using FileStream output = Open(target, FileAccess.Write);
        Require(!KernelFileCopy.TryCopy(input, output, 0, CancellationToken.None), "zero metadata size was treated as EOF");
        Buffered(input, output); output.Flush(true);
        Require(output.Length != 0, "virtual-file contents were lost");
        Console.WriteLine("Native dense copy and zero-metadata virtual-file fallback preserved exact bytes.");
    }
}
finally { Directory.Delete(root, recursive: true); }

static FileStream Open(string path, FileAccess access) => new(path, access == FileAccess.Read ? FileMode.Open : FileMode.CreateNew,
    access, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
static void Buffered(FileStream input, FileStream output)
{
    Span<byte> buffer = stackalloc byte[65536];
    int count;
    while ((count = input.Read(buffer)) != 0) output.Write(buffer[..count]);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Instruction(ushort code, byte yes, byte no, uint value)
    { public ushort Code = code; public byte Yes = yes; public byte No = no; public uint Value = value; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Program(ushort count, Instruction* instructions)
    { public ushort Count = count; public Instruction* Instructions = instructions; }
    internal static unsafe void DenyShortCopyRanges()
    {
        uint syscall = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => 326, Architecture.Arm64 => 285, _ => throw new PlatformNotSupportedException("The syscall probe supports native x64 and arm64") };
        Instruction* instructions = stackalloc Instruction[]
        {
            new(0x20, 0, 0, 0), new(0x15, 0, 3, syscall),
            new(0x20, 0, 0, 48), new(0x15, 1, 0, 1024 * 1024),
            new(0x06, 0, 0, 0x50001), new(0x06, 0, 0, 0x7fff0000)
        };
        var program = new Program(6, instructions);
        if (Prctl(38, 1, 0, 0, 0) != 0 || Prctl(22, 2, (nint)(&program), 0, 0) != 0)
            throw new IOException($"Cannot install isolated seccomp copy probe: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }
    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int operation, nint first, nint second, nint third, nint fourth);
}
