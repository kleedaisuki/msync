using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class ContextualIoChecks
{
    internal static async Task RunExecutionBoundaryAsync(string scratch)
    {
        foreach (bool prepare in new[] { false, true })
        {
            string directory = Path.Combine(scratch, prepare ? "prepared-prefix" : "relocatable");
            Directory.CreateDirectory(directory);
            const string source = """
                fn publish(target: Path) {
                    "first" -> target;
                    if exists target { target -> ./observed.txt; };
                    "last" -> target;
                };
                let name? = $std::io::read_line();
                if not name? { publish(name); };
                """;
            using StringReader input = new("result.txt\n");
            var options = new ExecutionOptions(WorkingDirectory: directory, Jobs: 1) { Input = input };
            var module = Compiler.Compile(source, Path.Combine(directory, "main.msync"), optimization: 2);
            if (prepare)
            {
                module = ContextualBackend.Prepare(Binding.Bind(module, new(directory, directory)), options);
                Require(module.ExecutionPlans is { Count: 0 }, "The opaque input must leave residual instructions.");
            }
            var vm = new Interpreter(RoundTrip(module), options);
            await vm.ExecuteAsync();
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "observed.txt")) == "first",
                "The executed metadata read and copy must observe the preceding publication.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "result.txt")) == "last",
                "The final write overtook its dependent read.");
            Require(vm.SchedulingStatistics is { SubmittedPlans: 0, AdmittedTransfers: 3 },
                "Residual instructions must execute as written, not be planned during dispatch.");
        }
        Console.WriteLine("PASS execution-only VM: relocatable and bound residual I/O preserve actual dependencies");
    }

    internal static async Task RunAsync(string scratch)
    {
        foreach (string route in new[] { "source", "mso", "mexe" })
        {
            await DeadOverwrite(route);
            await DeadOverwrite(route, "acquire");
            await DeadOverwrite(route, "release");
            await TailResult(route);
            await EmptySelection(route);
            await ReadObserver(route);
            await ResultObserver(route);
        }
        await RunBatchFailureAsync(scratch);
        Console.WriteLine("PASS contextual I/O: actual surviving admissions, read/result observers, source/object/executable routes");

        async Task DeadOverwrite(string route, string? marker = null)
        {
            string directory = Work(marker is null ? "overwrite" : marker + "-fusion", route);
            await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), "first-generation");
            await File.WriteAllTextAsync(Path.Combine(directory, "second.txt"), "second-generation");
            string boundary = marker is null ? "" : marker + "; ";
            var (vm, _) = await Execute(directory, "./first.txt -> ./shared.txt; " + boundary + "./second.txt -> ./shared.txt;", route);
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "shared.txt")) == "second-generation",
                $"{route}: the last write did not publish its payload");
            Require(vm.SchedulingStatistics.AdmittedTransfers == 1,
                $"{route}/{marker ?? "plain"}: dead I/O still admitted {vm.SchedulingStatistics.AdmittedTransfers} transfers instead of one");
            Require(vm.FailedTransfers == 0, $"{route}: removed I/O contributed failure accounting");
        }

        async Task TailResult(string route)
        {
            string directory = Work("tail-result", route);
            await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), "first-generation");
            await File.WriteAllTextAsync(Path.Combine(directory, "second.txt"), "second-generation");
            var (vm, result) = await Execute(directory,
                "./first.txt -> ./shared.txt; ./second.txt -> ./shared.txt", route);
            Require(result is BoolValue { Data: true }, $"{route}: the surviving transfer did not return its actual Bool");
            Require(vm.SchedulingStatistics.AdmittedTransfers == 1,
                $"{route}: demanding the last result retained an unobserved earlier overwrite");
        }

        async Task EmptySelection(string route)
        {
            string directory = Work("empty-selection", route);
            Directory.CreateDirectory(Path.Combine(directory, "inputs"));
            var (vm, result) = await Execute(directory, @"./inputs/<.+\.pick> -> ./selected/", route);
            Require(result is BoolValue { Data: true }, $"{route}: an empty finite transfer must succeed");
            Require(vm.SchedulingStatistics.AdmittedTransfers == 0, $"{route}: an empty selection admitted file I/O");
            Require(!Directory.Exists(Path.Combine(directory, "selected")), $"{route}: an empty selection created a destination");
        }

        async Task ReadObserver(string route)
        {
            string directory = Work("read-observer", route);
            await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), "first-generation");
            await File.WriteAllTextAsync(Path.Combine(directory, "second.txt"), "second-generation");
            var (vm, _) = await Execute(directory,
                "./first.txt -> ./shared.txt; ./shared.txt -> ./observed.txt; ./second.txt -> ./shared.txt;", route);
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "observed.txt")) == "first-generation",
                $"{route}: elimination or motion lost the first destination version's observer");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "shared.txt")) == "second-generation",
                $"{route}: a dependent read overtook the later write");
            Console.WriteLine($"OBSERVE {route}: copy-version observer admissions {vm.SchedulingStatistics.AdmittedTransfers}");
        }

        async Task ResultObserver(string route)
        {
            string directory = Work("result-observer", route);
            await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), "first-generation");
            await File.WriteAllTextAsync(Path.Combine(directory, "second.txt"), "second-generation");
            var (vm, _) = await Execute(directory,
                "let copied = ./first.txt -> ./shared.txt; copied as String -> ./copied.txt; ./second.txt -> ./shared.txt;", route);
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "copied.txt")) == "true",
                $"{route}: the demanded transfer result was lost");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "shared.txt")) == "second-generation",
                $"{route}: the last write was lost after consuming the first result");
            Require(vm.SchedulingStatistics.AdmittedTransfers == 3,
                $"{route}: result-observed I/O admission count is {vm.SchedulingStatistics.AdmittedTransfers}, expected three");
        }

        string Work(string name, string route)
        {
            string directory = Path.Combine(scratch, name + "-" + route);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    internal static async Task RunForwardingAsync(string scratch)
    {
        foreach (bool clobber in new[] { false, true })
        {
            string directory = Path.Combine(scratch, clobber ? "source-clobber" : "stable-source-forwarding");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "a"), "original-a");
            await File.WriteAllTextAsync(Path.Combine(directory, "d"), "replacement-d");
            string source = "./a -> ./b; " + (clobber ? "\"changed\" -> ./a; " : "")
                + "./b -> ./c; ./d -> ./b;";
            var (vm, _) = await Execute(directory, source, "source");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "c")) == "original-a",
                "Copy propagation lost the consumed earlier file version");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "b")) == "replacement-d",
                "Copy propagation lost the final destination version");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "a")) == (clobber ? "changed" : "original-a"),
                "Copy propagation lost an explicit source clobber");
            if (!clobber)
                Require(vm.SchedulingStatistics.AdmittedTransfers == 2,
                    $"Stable-source chain still materialized {vm.SchedulingStatistics.AdmittedTransfers} transfers instead of two");
            Console.WriteLine($"OBSERVE {(clobber ? "clobbered" : "stable")} source: {vm.SchedulingStatistics.AdmittedTransfers} actual transfers");
        }
        Console.WriteLine("PASS stable-source forwarding and source-clobber version preservation");
    }

    internal static async Task RunHelperAsync(string scratch)
    {
        foreach (string route in new[] { "source", "mso", "mexe" })
        {
            string directory = Path.Combine(scratch, "known-copy-helper-" + route);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "first"), "first-generation");
            await File.WriteAllTextAsync(Path.Combine(directory, "second"), "second-generation");
            const string source = "fn copy_file(source: Path, destination: Path) -> Bool { source -> destination }; "
                + "copy_file(./first, ./shared); copy_file(./second, ./shared);";
            var (vm, _) = await Execute(directory, source, route);
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "shared")) == "second-generation",
                $"{route}: the known helper lost the final write");
            Require(vm.SchedulingStatistics.AdmittedTransfers == 1,
                $"{route}: resolved copy helpers materialized {vm.SchedulingStatistics.AdmittedTransfers} transfers instead of one");
        }
        Console.WriteLine("PASS resolved finite-I/O helper calls collapse to one actual surviving transfer");
    }

    internal static async Task RunBatchFailureAsync(string scratch)
    {
        foreach (string route in new[] { "source", "mso", "mexe" })
        {
            string directory = Path.Combine(scratch, "batch-failure-" + route);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "good"), "kept");
            using StringWriter errors = new();
            var (vm, result) = await Execute(directory,
                "./good -> ./good-out; ./missing -> ./bad-out", route, jobs: 1, errors: errors);
            Require(result is BoolValue { Data: false }, $"{route}: the observed last transfer did not return false");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "good-out")) == "kept",
                $"{route}: a sibling's observed failure removed an earlier successful publication");
            Require(vm.FailedTransfers == 1, $"{route}: concrete failure accounting did not retain one failed transfer");
            Require(errors.ToString().Contains("missing", StringComparison.Ordinal), $"{route}: actual source failure lost its diagnostic");
        }
        Console.WriteLine("PASS mixed-observation local batch with Jobs=1: observed false, retained success and concrete failure");
    }

    private static async Task<(Interpreter Vm, Value Result)> Execute(string directory, string source, string route,
        int jobs = 4, TextWriter? errors = null)
    {
        string sourceName = Path.Combine(directory, "main.msync");
        Module module = Compiler.Compile(source, sourceName, optimization: 2);
        ExecutionOptions options = new(WorkingDirectory: directory, SourceFile: sourceName, Jobs: jobs, Error: errors);
        if (route != "source") module = RoundTrip(module);
        module = ContextualBackend.Prepare(Binding.Bind(module, new(directory, directory, sourceName)), options);
        if (route == "mexe") module = RoundTrip(module);
        Interpreter vm = new(module, options);
        Value result = await vm.ExecuteAsync();
        return (vm, result);
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
