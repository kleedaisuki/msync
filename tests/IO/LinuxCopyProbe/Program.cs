using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.IO.Probes;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This probe measures Linux kernel-assisted copy");
int count = int.Parse(Option("--samples") ?? "21", System.Globalization.CultureInfo.InvariantCulture);
string report = Path.GetFullPath(Option("--output") ?? ".temp/io-v080/component.json");
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "linux-copy-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    List<Case> cases = [];
    foreach (var (name, files, size) in new[] { ("file-4096", 1, 4096), ("file-1048576", 1, 1048576), ("file-134217728", 1, 134217728), ("tree-256x4096", 256, 4096) })
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "destination"); Directory.CreateDirectory(destination);
        List<(string Source, string Target, byte[] Hash)> paths = [];
        for (int i = 0; i < files; i++)
        {
            string path = Path.Combine(folder, $"source-{i}");
            Create(path, size, i); paths.Add((path, Path.Combine(destination, $"target-{i}"), Hash(path)));
        }
        List<Sample> buffered = [], kernel = [];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (int iteration = -2; iteration < count; iteration++)
                foreach (bool native in (iteration & 1) == 0 ? new[] { false, true } : new[] { true, false })
                {
                    long cpu = Native.CpuTime(), started = Stopwatch.GetTimestamp();
                    foreach (var path in paths) Copy(path.Source, path.Target, buffer, native);
                    double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    double cpuMilliseconds = (Native.CpuTime() - cpu) / 1_000_000.0;
                    foreach (var path in paths)
                    {
                        if (!Hash(path.Target).AsSpan().SequenceEqual(path.Hash)) throw new IOException("Copy changed file bytes");
                        if (File.GetLastWriteTimeUtc(path.Source) != File.GetLastWriteTimeUtc(path.Target) || File.GetUnixFileMode(path.Source) != File.GetUnixFileMode(path.Target)) throw new IOException("Copy changed mode or mtime");
                    }
                    if (iteration >= 0) (native ? kernel : buffered).Add(new(milliseconds, cpuMilliseconds));
                }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        cases.Add(new(name, files, size, buffered, kernel));
        Console.WriteLine($"{name}: buffered {Percentile(buffered, .5):F3}ms/{Percentile(buffered, .5, true):F3} CPUms; kernel {Percentile(kernel, .5):F3}ms/{Percentile(kernel, .5, true):F3} CPUms");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    File.WriteAllText(report, JsonSerializer.Serialize(new Report(Environment.OSVersion.VersionString, RuntimeInformation.FrameworkDescription, count, cases), ProbeJson.Default.Report));
}
finally { Directory.Delete(root, recursive: true); }

string? Option(string name) { int index = Array.IndexOf(args, name); return index < 0 ? null : args[index + 1]; }
static byte[] Hash(string path) { using var file = File.OpenRead(path); return SHA256.HashData(file); }
static void Create(string path, int size, int seed)
{
    byte[] block = new byte[Math.Min(size, 1048576)]; new Random(seed + 412).NextBytes(block);
    using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    { for (int offset = 0; offset < size; offset += block.Length) output.Write(block.AsSpan(0, Math.Min(block.Length, size - offset))); output.Flush(true); }
    File.SetLastWriteTimeUtc(path, new DateTime(2024, 11, 8, 9, 10, 11, DateTimeKind.Utc).AddTicks(seed));
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.UserExecute);
}
static void Copy(string source, string target, byte[] buffer, bool kernel)
{
    var info = new FileInfo(source); _ = info.Attributes;
    string stage = Path.Combine(Path.GetDirectoryName(target)!, ".msync-probe-" + Guid.NewGuid().ToString("N"));
    try
    {
        using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        {
            if (!kernel || !KernelFileCopy.TryCopy(input, output, info.Length, CancellationToken.None))
            { int read; while ((read = input.Read(buffer)) != 0) output.Write(buffer.AsSpan(0, read)); }
            File.SetLastWriteTimeUtc(stage, info.LastWriteTimeUtc); File.SetUnixFileMode(stage, info.UnixFileMode); output.Flush(true);
        }
        File.Move(stage, target, overwrite: true);
    }
    catch { File.Delete(stage); throw; }
}
static double Percentile(List<Sample> samples, double quantile, bool cpu = false)
{
    double[] values = samples.Select(sample => cpu ? sample.CpuMilliseconds : sample.Milliseconds).Order().ToArray();
    double index = (values.Length - 1) * quantile;
    int lower = (int)index; return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - values[lower]) * (index - lower);
}
internal sealed record Sample(double Milliseconds, double CpuMilliseconds);
internal sealed record Case(string Name, int Files, int FileBytes, List<Sample> Buffered, List<Sample> Kernel);
internal sealed record Report(string OperatingSystem, string Runtime, int Samples, List<Case> Cases);
[JsonSerializable(typeof(Report))]
internal sealed partial class ProbeJson : JsonSerializerContext;
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Seconds; public long Nanoseconds; }
    internal static long CpuTime() { if (ClockGetTime(2, out var time) != 0) throw new IOException("clock_gettime failed"); return time.Seconds * 1_000_000_000 + time.Nanoseconds; }
    [LibraryImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockGetTime(int clock, out Timespec time);
}
