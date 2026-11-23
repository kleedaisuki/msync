using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFileMetadataChecks
{
    private const string Name = "live-file-metadata.msync";

    internal static void Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "hpir", $"file-metadata-{Guid.NewGuid():N}");
        string detached = Path.Combine(root, "detached");
        Directory.CreateDirectory(detached);
        try
        {
            string existing = Path.Combine(root, "actual.txt");
            string replaced = Path.Combine(root, "replaced.txt");
            File.WriteAllText(existing, "before");
            File.WriteAllText(replaced, "old");
            File.WriteAllText(Path.Combine(root, "fresh.txt"), "fresh 🧪");
            const string query = """
                let io = $std::io;
                let fs = $std::fs;
                if fs::is_regular_file(./actual.txt) and not fs::exists(./absent.txt) {
                    io::println(io::file_size("./actual.txt") as String);
                };
                ./fresh.txt -> ./replaced.txt;
                io::println(io::file_size(./replaced.txt) as String);
                """;
            HpirProgram executable = Compile(query, root);
            if (!executable.Imports.Any(import => import.Service == HpirService.LiveTransfer))
                throw new Exception("Metadata ordering test did not retain the live write.");
            File.WriteAllText(existing, "🧪");
            var events = new ConcurrentQueue<RuntimeTimelineRecord>();
            var profile = new RuntimeProfile(instructionSites: true);
            using var output = new StringWriter { NewLine = "\n" };
            using var publish = new ManualResetEventSlim();
            var options = new ExecutionOptions(WorkingDirectory: detached, Output: output)
                { Timeline = new(Record, "file-metadata"), Profile = profile };
            _ = HpirExecutor.Run(executable, options);
            var records = events.Where(item => item.Name == "hpir.file_read.admit").ToArray();
            var native = profile.Drain().Where(item => item.Name == "vm.native").ToArray();
            if (output.ToString() != "4\n10\n" || File.ReadAllText(replaced) != "fresh 🧪"
                || records.Length != 4 || records.Any(item => item.Attributes.GetValueOrDefault("source.file") != Name)
                || records.Count(item => item.Attributes.GetValueOrDefault("work.outcome") == "ready") < 3
                || !OperatingSystem.IsWindows() && !records.Any(item => item.Metrics.GetValueOrDefault("dependencies.live") == 1)
                || !native.Any(item => item.Attributes["native.name"] == "io.file_size"))
                throw new Exception($"File metadata control: output={output}, file={File.ReadAllText(replaced)}, "
                    + $"admissions=[{string.Join(';', records.Select(item => string.Join(',', item.Attributes)))}], "
                    + $"native=[{string.Join(',', native.Select(item => item.Attributes["native.name"]))}]");

            const string failing = """
                let size = $std::io::file_size(./missing.dat);
                $std::io::println("admitted");
                $std::io::println(size as String);
                """;
            output.GetStringBuilder().Clear();
            try
            {
                _ = HpirExecutor.Run(Compile(failing, root), options with { Timeline = null, Profile = null });
                throw new Exception("Demanding metadata for a missing file should fail.");
            }
            catch (MsyncException error) when (error.Span?.Source == Name && error.InnerException is FileNotFoundException
                && output.ToString() == "admitted\n") { }

            Console.WriteLine("PASS HPIR file metadata: detached live bytes, mixed readiness, same-path writer ordering, sourceful deferred failure");
            return;

            void Record(RuntimeTimelineRecord record)
            {
                events.Enqueue(record);
                // Hold the actual buffered transfer after flushing its stage
                // and before publishing it; metadata must admit a dependency.
                if (!OperatingSystem.IsWindows() && record.Name == "file.sync" && !publish.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The live transfer never saw its metadata consumer admit.");
                if (record.Name == "hpir.file_read.admit"
                    && record.Attributes.GetValueOrDefault("input.resource")?.EndsWith("replaced.txt", StringComparison.Ordinal) == true)
                    publish.Set();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static HpirProgram Compile(string source, string root)
    {
        var program = Compiler.LowerHpir(Compiler.Analyze(source, Name), 2);
        program = HpirBinding.Prepare(program, new(root, root));
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }
}
