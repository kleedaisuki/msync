using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const int BufferSize = 65536;
    private const uint ProgressCancel = 1;
    private const uint StreamSwitch = 1;

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The copy probe requires actual Windows file I/O");
        string output = Option(args, "--output") ?? ".temp/windows-copy-probe.json";
        int samples = int.Parse(Option(args, "--samples") ?? "9", System.Globalization.CultureInfo.InvariantCulture);
        string root = Path.GetFullPath(Option(args, "--work-directory") ?? Path.Combine(".temp", "windows-copy-probe-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            List<CaseResult> cases = [];
            foreach (int size in new[] { 4096, 1048576, 67108864 })
            {
                string source = Path.Combine(root, $"source-{size}.bin");
                CreateSource(source, size, size);
                cases.Add(await Measure($"file-{size}", [source], root, samples));
            }
            string tree = Path.Combine(root, "tree"); Directory.CreateDirectory(tree);
            string[] files = Enumerable.Range(0, 256).Select(index => Path.Combine(tree, $"file-{index:D4}.bin")).ToArray();
            for (int i = 0; i < files.Length; i++) CreateSource(files[i], 4096, i);
            cases.Add(await Measure("tree-256x4096", files, root, samples));
            string large = Path.Combine(root, "source-67108864.bin");
            CancellationResult managed = await CancelManaged(large, root);
            CancellationResult native = await CancelNative(large, root);
            FeatureResult[] features = await Features(root);
            ProbeReport report = new(Environment.OSVersion.VersionString, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount,
                root, samples, cases.ToArray(), [managed, native], features);
            string path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeReport));
            Console.WriteLine(path);
            return 0;
        }
        finally { RemoveOwnedTree(root); }
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? null : args[index + 1];
    }
    private static void CreateSource(string path, int size, int seed)
    {
        byte[] bytes = new byte[Math.Min(1048576, size)]; new Random(seed).NextBytes(bytes);
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        for (int count = 0; count < size; count += bytes.Length) file.Write(bytes.AsSpan(0, Math.Min(bytes.Length, size - count)));
        file.Flush(true);
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(seed % 100000));
    }
    private static async Task<CaseResult> Measure(string name, string[] sources, string root, int samples)
    {
        string destination = Path.Combine(root, "destination-" + name); Directory.CreateDirectory(destination);
        Dictionary<string, string> expected = sources.ToDictionary(path => path, Hash);
        List<double> managed = [], native = [], managedCpu = [], nativeCpu = [];
        for (int iteration = -2; iteration < samples; iteration++)
        {
            bool nativeFirst = (iteration & 1) != 0;
            foreach (bool useNative in nativeFirst ? new[] { true, false } : new[] { false, true })
            {
                using Process process = Process.GetCurrentProcess();
                TimeSpan cpuStart = process.TotalProcessorTime;
                long started = Stopwatch.GetTimestamp();
                if (useNative)
                    await Task.Run(() =>
                    {
                        foreach (string source in sources) CopyNative(source, Path.Combine(destination, Path.GetFileName(source)), new(CancellationToken.None));
                    });
                else
                    foreach (string source in sources) await CopyManaged(source, Path.Combine(destination, Path.GetFileName(source)), CancellationToken.None);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                foreach (string source in sources) Verify(source, Path.Combine(destination, Path.GetFileName(source)), expected[source]);
                Require(!Directory.EnumerateFiles(destination, ".msync-probe-*").Any(), "Completed copy left its owned stage");
                if (iteration < 0) continue;
                (useNative ? native : managed).Add(elapsed); (useNative ? nativeCpu : managedCpu).Add(cpu);
            }
        }
        return new(name, sources.Sum(path => new FileInfo(path).Length), sources.Length, managed.ToArray(), native.ToArray(), managedCpu.ToArray(), nativeCpu.ToArray(), Median(managed), Median(native));
    }
    private static async Task CopyManaged(string source, string target, CancellationToken cancellationToken, string? stagePath = null, Action<long>? progress = null)
    {
        string stage = stagePath ?? Stage(target);
        try
        {
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(progress is null ? output : new WriteProgressStream(output, progress), BufferSize, cancellationToken);
                File.SetLastWriteTimeUtc(stage, File.GetLastWriteTimeUtc(source));
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(stage, target, true);
        }
        finally { DeleteOwnedStage(stage); }
    }
    private static unsafe void CopyNative(string source, string target, NativeContext context, string? stagePath = null)
    {
        string stage = stagePath ?? Stage(target);
        GCHandle handle = GCHandle.Alloc(context);
        try
        {
            bool copied = Native.CopyFileEx(WindowsPath(source), WindowsPath(stage), &Progress, GCHandle.ToIntPtr(handle), 0, 1);
            int error = copied ? 0 : Marshal.GetLastPInvokeError();
            if (!copied)
            {
                if (context.Token.IsCancellationRequested) throw new OperationCanceledException(context.Token);
                throw new IOException($"CopyFileExW failed with {error}: {new Win32Exception(error).Message}");
            }
            if (context.Failure is not null) throw context.Failure;
            context.Token.ThrowIfCancellationRequested();
            if (!Native.FlushFileBuffers(context.DestinationHandle)) throw new IOException($"FlushFileBuffers failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            Native.CloseHandle(context.DestinationHandle); context.DestinationHandle = 0;
            context.Token.ThrowIfCancellationRequested();
            File.Move(stage, target, true);
        }
        finally
        {
            if (context.DestinationHandle != 0) Native.CloseHandle(context.DestinationHandle);
            handle.Free(); DeleteOwnedStage(stage);
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Progress(long total, long copied, long streamSize, long streamCopied, uint stream, uint reason, nint source, nint destination, nint user)
    {
        NativeContext context = (NativeContext)GCHandle.FromIntPtr(user).Target!;
        try
        {
            context.BytesCopied = copied;
            if (reason == StreamSwitch && stream == 1 && context.DestinationHandle == 0)
            {
                nint process = Native.GetCurrentProcess();
                if (!Native.DuplicateHandle(process, destination, process, out nint duplicate, 0, false, 2))
                    throw new IOException($"DuplicateHandle failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                context.DestinationHandle = duplicate;
            }
            if (context.CancelAfterProgress is { } cancellation && copied > 0 && copied < total)
            {
                context.CancelledAt = Stopwatch.GetTimestamp(); cancellation.Cancel();
            }
            return context.Token.IsCancellationRequested ? ProgressCancel : 0;
        }
        catch (Exception error) { context.Failure = error; return ProgressCancel; }
    }
    private static async Task<CancellationResult> CancelNative(string source, string root)
    {
        string target = Path.Combine(root, "cancel-native.bin"); await File.WriteAllTextAsync(target, "previous");
        using CancellationTokenSource cancellation = new();
        NativeContext context = new(cancellation.Token) { CancelAfterProgress = cancellation };
        long start = Stopwatch.GetTimestamp(); bool cancelled = false;
        try { await Task.Run(() => CopyNative(source, target, context)); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && context.BytesCopied > 0, "Native cancellation did not happen after actual transferred bytes");
        Require(await File.ReadAllTextAsync(target) == "previous", "Cancelled native copy changed published destination");
        return new("copyfileex-callback", context.BytesCopied, context.CancelledAt == 0 ? null : Stopwatch.GetElapsedTime(context.CancelledAt).TotalMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds, cancelled);
    }
    private static async Task<CancellationResult> CancelManaged(string source, string root)
    {
        string target = Path.Combine(root, "cancel-managed.bin"); await File.WriteAllTextAsync(target, "previous");
        string stage = Stage(target); using CancellationTokenSource cancellation = new();
        long started = Stopwatch.GetTimestamp(); long cancelledAt = 0; long progress = 0;
        Task copying = CopyManaged(source, target, cancellation.Token, stage, bytes =>
        {
            if (cancelledAt != 0) return;
            progress = bytes; cancelledAt = Stopwatch.GetTimestamp(); cancellation.Cancel();
        });
        bool cancelled = false;
        try { await copying; } catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && progress > 0, "Managed cancellation did not happen after actual written bytes");
        Require(await File.ReadAllTextAsync(target) == "previous" && !File.Exists(stage), "Managed cancellation published or leaked a stage");
        return new("overlapped-current", progress, cancelledAt == 0 ? null : Stopwatch.GetElapsedTime(cancelledAt).TotalMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds, cancelled);
    }
    private static async Task<FeatureResult[]> Features(string root)
    {
        string source = Path.Combine(root, "attributes.bin"); CreateSource(source, 4096, 42);
        string native = Path.Combine(root, "attributes-native.bin"); string managed = Path.Combine(root, "attributes-managed.bin");
        await File.WriteAllTextAsync(source + ":msync.probe", "named stream");
        await Task.Run(() => CopyNative(source, native, new(CancellationToken.None)));
        await CopyManaged(source, managed, CancellationToken.None);
        bool nativeAds = File.Exists(native + ":msync.probe") && await File.ReadAllTextAsync(native + ":msync.probe") == "named stream";
        bool managedAds = File.Exists(managed + ":msync.probe");
        Require(nativeAds, "Native copy did not preserve its NTFS named stream");
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
        string readOnly = Path.Combine(root, "readonly-native.bin");
        await Task.Run(() => CopyNative(source, readOnly, new(CancellationToken.None)));
        Require((File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0, "Native copy lost readonly attribute");
        string deep = Path.Combine(root, new string('a', 100), new string('b', 100), new string('c', 100)); Directory.CreateDirectory(deep);
        string longSource = Path.Combine(deep, "source.bin"), longTarget = Path.Combine(deep, "target.bin"); CreateSource(longSource, 4096, 43);
        await Task.Run(() => CopyNative(longSource, longTarget, new(CancellationToken.None)));
        Verify(longSource, longTarget, Hash(longSource));
        string emptySource = Path.Combine(root, "empty-source.bin"), emptyTarget = Path.Combine(root, "empty-target.bin");
        await File.WriteAllBytesAsync(emptySource, []);
        await Task.Run(() => CopyNative(emptySource, emptyTarget, new(CancellationToken.None)));
        Require(new FileInfo(emptyTarget).Length == 0, "Native empty copy changed file length");
        return [new("ntfs-alternate-data-stream", nativeAds, managedAds), new("readonly-source", true, null), new("long-path", true, null), new("empty-file", true, null)];
    }
    private static void Verify(string source, string destination, string expectedHash)
    {
        Require(Hash(destination) == expectedHash, $"Copied bytes differ: {destination}");
        Require(File.GetLastWriteTimeUtc(source) == File.GetLastWriteTimeUtc(destination), $"Copied modification time differs: {destination}");
    }
    private static string Hash(string path) { using FileStream file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string Stage(string target) => Path.Combine(Path.GetDirectoryName(target)!, ".msync-probe-" + Guid.NewGuid().ToString("N"));
    private static string WindowsPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return path;
        string full = Path.GetFullPath(path);
        return full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + full[2..] : "\\\\?\\" + full;
    }
    private static void DeleteOwnedStage(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); File.Delete(path);
    }
    private static void RemoveOwnedTree(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, true);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Median(List<double> values) { double[] sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
    private sealed class WriteProgressStream(Stream inner, Action<long> progress) : Stream
    {
        private long bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        { inner.Write(buffer, offset, count); bytes += count; progress(bytes); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken); bytes += buffer.Length; progress(bytes); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    private sealed class NativeContext(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public CancellationTokenSource? CancelAfterProgress { get; init; }
        public long BytesCopied;
        public long CancelledAt;
        public nint DestinationHandle;
        public Exception? Failure;
    }
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CopyFileEx(string source, string target, delegate* unmanaged[Stdcall]<long, long, long, long, uint, uint, nint, nint, nint, uint> progress, nint data, nint cancel, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool FlushFileBuffers(nint file);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}

internal sealed record ProbeReport(string OperatingSystem, string Runtime, int ProcessorCount, string WorkDirectory, int Samples, CaseResult[] Cases, CancellationResult[] Cancellation, FeatureResult[] Features);
internal sealed record CaseResult(string Name, long BytesPerTrial, int FileCount, double[] ManagedMilliseconds, double[] NativeMilliseconds, double[] ManagedCpuMilliseconds, double[] NativeCpuMilliseconds, double ManagedMedianMilliseconds, double NativeMedianMilliseconds);
internal sealed record CancellationResult(string Method, long ProgressBytes, double? CancellationToCompletionMilliseconds, double TotalMilliseconds, bool Cancelled);
internal sealed record FeatureResult(string Name, bool NativePreserved, bool? CurrentManagedPreserved);
[JsonSerializable(typeof(ProbeReport))]
internal partial class ProbeJsonContext : JsonSerializerContext;
