using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class RuntimeFixture
{
    private static int assertions;
    internal static int Assertions => Volatile.Read(ref assertions);
    internal static readonly string Root = FindRoot();
    internal static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref assertions);
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static NativeProgram Compile(string source, int level = 0) =>
        SourceCompiler.CompileNative(source, "runtime-fixture.msync", level);
    internal static NativeProgram Reload(NativeProgram program)
    {
        using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
        return NativeArtifact.Read(bytes);
    }
    internal static NativeProgram Bind(NativeProgram program, string root) => NativePreparation.Prepare(program with
        { Context = new(root, root, "runtime-fixture.msync") });
    internal static async Task<NativeValue> Run(NativeProgram program, ExecutionOptions? options = null, CancellationToken token = default)
    {
        await using NativeInterpreter interpreter = new(program, options);
        return await interpreter.ExecuteAsync(token);
    }
    internal static async Task Number(string source, double expected)
    {
        foreach (int level in new[] { 0, 3 })
        {
            NativeProgram program = Compile(source, level);
            foreach (NativeProgram image in new[] { Reload(program), Reload(Bind(program, Root)) })
            {
                NativeValue actual = await Run(image);
                Check(actual.Kind == NativeKind.Number && actual.AsNumber() == expected,
                    $"Native source/detached result at O{level}: expected {expected}, got {actual}; source: {source}");
            }
        }
    }
    internal static void Reject(NativeProgram program, string detail)
    {
        try { NativeArtifact.Validate(program); throw new InvalidOperationException("Malformed native program accepted: " + detail); }
        catch (MsyncException error) { Check(error.Message.Contains(detail, StringComparison.OrdinalIgnoreCase), error.Message); }
    }
    private static string FindRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "msync.slnx"))
            && !Directory.Exists(Path.Combine(directory.FullName, "src", "Msync.Core"))) directory = directory.Parent;
        string root = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository not found"), ".temp", "runtime-tests");
        Directory.CreateDirectory(root); return root;
    }
}
