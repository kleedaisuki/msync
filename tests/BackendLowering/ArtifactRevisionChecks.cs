using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class ArtifactRevisionChecks
{
    internal static async Task Run()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(value => value.EndsWith("constructor-work-v3.mexe", StringComparison.Ordinal));
        using Stream resource = assembly.GetManifestResourceStream(name)!;
        try
        {
            NativeArtifact.Read(resource);
            throw new InvalidOperationException("An obsolete prototype artifact was silently accepted");
        }
        catch (MsyncException error) when (error.Message.Contains("Recompile", StringComparison.Ordinal)) { }

        const string source = """
            let bias = 1;
            let result = $std::ranges::first($std::ranges::transform([2], fn(value) { value + bias }));
            bias = 9;
            $std::io::println("submitted");
            $std::io::println(result as String);
            """;
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "constructor-work.msync", 2));
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            using StringWriter output = new();
            await using NativeInterpreter interpreter = new(program, new() { Output = output, Tiers = new(policy, CallThreshold: 1) });
            await interpreter.ExecuteAsync();
            if (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != "submitted\n3\n")
                throw new InvalidOperationException("Fresh compilation changed constructor observation order");
        }
        Console.WriteLine("PASS obsolete artifact rejection and fresh Off/Force execution");
    }
}
