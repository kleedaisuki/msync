using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirConnectionDisplayChecks
{
    private const string Source = """
        fn select(remote: Bool) -> Connection {
            if remote { ssh://forge } else { $wsl }
        };
        let line? = $std::io::read_line();
        let selected = object { endpoint: select(line?) };
        fn use(endpoint: Connection, name: String) -> Path {
            (endpoint @ name) / "leaf"
        };
        $std::io::println(use(selected::endpoint, "dir with space") as String);
        let invalid = selected::endpoint @ "not-a-number";
        invalid as Number
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "connection-display-gate", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                HpirProgram image = Prepare(root, level);
                using var machine = HpirMachineImage.IsSupported ? HpirMachineImage.Compile(image) : null;
                Check(root, image, null, "", "ssh://forge", ConnectionKind.Ssh);
                Check(root, image, null, "input\n", "wsl://", ConnectionKind.Wsl);
                if (machine is null) continue;
                Check(root, image, machine, "", "ssh://forge", ConnectionKind.Ssh);
                Check(root, image, machine, "input\n", "wsl://", ConnectionKind.Wsl);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR Connection: selected endpoint across Object and guest ABI, native pathname projection and source-attributed diagnostics");
    }

    private static HpirProgram Prepare(string root, int level)
    {
        string source = Path.Combine(root, "connection.msync");
        File.WriteAllText(source, Source);
        var typed = Compiler.Analyze(File.ReadAllText(source), source);
        string hir = Path.ChangeExtension(source, ".mhir");
        Msync.Hir.HirArtifact.WriteProduced(typed, hir);
        File.Delete(source);
        HpirProgram portable = Reload(Compiler.LowerHpir(Msync.Hir.HirArtifact.Read(hir), level));
        File.Delete(hir);
        return Reload(HpirBinding.Prepare(portable, new(root, root)));
    }

    private static void Check(string root, HpirProgram image, HpirMachineImage? machine, string input,
        string spelling, ConnectionKind kind)
    {
        using var reader = new StringReader(input);
        using var output = new StringWriter { NewLine = "\n" };
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        { Input = reader, Timeline = new(events.Enqueue, "connection-display") };
        try
        {
            _ = machine is null ? HpirExecutor.Run(image, options) : HpirExecutor.RunMachineCandidate(machine, options);
            throw new InvalidOperationException("An invalid Path-to-Number cast unexpectedly succeeded.");
        }
        catch (MsyncException failure) when (failure.Message.StartsWith($"Cannot cast Path '{spelling} @ not-a-number' to Number",
            StringComparison.Ordinal))
        {
            if (failure.Span?.Source != Path.Combine(root, "connection.msync") || output.ToString() != "dir with space/leaf\n"
                || events.Count(row => row.Name == "hpir.path.qualify"
                    && row.Attributes.GetValueOrDefault("endpoint.kind") == kind.ToString()) != 2)
                throw new InvalidOperationException("Connection qualification lost its native projection or causal endpoint.", failure);
        }
    }

    private static HpirProgram Reload(HpirProgram image)
    {
        using MemoryStream bytes = new();
        HpirArtifact.WriteProduced(image, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }
}
