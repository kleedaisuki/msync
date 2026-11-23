using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirDirectoryChecks
{
    private const string Source = """
        fn list(path: Path) { $std::fs::directory_iterator(path) };
        let unused = $std::fs::directory_iterator("./does-not-exist");
        let entries = list(./names);
        for entry in entries { entry -> ./last; };
        for entry in entries { entry -> ./first; break; };
        """;

    internal static void Run()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
        string directory = Directory.CreateDirectory(Path.Combine(root, ".temp", "hpir", "directory", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            for (int level = 0; level <= 3; ++level) Check(directory, level);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS HPIR typed Directory Path cursor: lazy first pull, exact scalar order, reopen/break and detached binding O0–O3");
    }

    private static void Check(string directory, int optimization)
    {
        string work = Directory.CreateDirectory(Path.Combine(directory, $"O{optimization}")).FullName;
        var module = Compiler.Analyze(Source, Path.Combine(work, "directory.msync"));
        HpirProgram bound = Reload(HpirBinding.Prepare(Reload(Compiler.LowerHpir(module, optimization)), new(work, work)));
        if (Directory.Exists(Path.Combine(work, "names")) || bound.Imports.All(import => import.Service != HpirService.DirectoryNext))
            throw new Exception("Directory Range must bind without enumerating the actual typed producer.");
        Verify(null, "first");
        if (HpirMachineImage.IsSupported)
        {
            using var image = HpirMachineImage.Compile(bound);
            Verify(image, "second");
        }
        return;

        void Verify(HpirMachineImage? image, string revision)
        {
            var names = Directory.CreateDirectory(Path.Combine(work, "names"));
            File.WriteAllText(Path.Combine(names.FullName, "\U00010000"), "supplementary-" + revision);
            File.WriteAllText(Path.Combine(names.FullName, "\uE000"), "bmp-" + revision);
            var events = new ConcurrentQueue<RuntimeTimelineRecord>();
            var options = new ExecutionOptions(WorkingDirectory: directory) { Timeline = new(events.Enqueue, "typed-directory") };
            if (image is null) _ = HpirExecutor.Run(bound, options);
            else _ = HpirExecutor.RunMachineCandidate(image, options);
            if (File.ReadAllText(Path.Combine(work, "first")) != "bmp-" + revision
                || File.ReadAllText(Path.Combine(work, "last")) != "supplementary-" + revision
                || events.Count(item => item.Name == "hpir.directory.open") != 2
                || events.Where(item => item.Name == "hpir.directory.next").Sum(item => (int)item.Metrics.GetValueOrDefault("directory.yielded")) != 3
                || !events.Any(item => item.Name == "hpir.directory.close" && item.Attributes.GetValueOrDefault("cleanup.reason") == "explicit"))
                throw new Exception("The two independent directory traversals must observe exact scalar order, current bytes and early close.");
        }
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
