using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirNullableVectorChecks
{
    private const string SourceName = "nullable-number-vector.msync";
    private const string Traversal = """
        let values = [null, 5];
        let pulls = 0;
        let nulls = 0;
        let sum = 0;
        for item? in values {
            pulls = pulls + 1;
            if item is null { nulls = nulls + 1; } else { sum = sum + item; };
        };
        for item? in values {
            pulls = pulls + 1;
            if item is null { nulls = nulls + 1; } else { sum = sum + item; };
        };
        pulls * 100 + nulls * 10 + sum
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; level++)
        {
            List<CompilationObservation> selection = [];
            HpirProgram program = Compile(Traversal, level, new(selection.Add, includeOptimizerPasses: true));
            var allocations = program.Imports.Where(import => import.Service == HpirService.AllocateObject).ToArray();
            if (allocations is not [{ Binding: var layout }] || program.Layouts[layout].Size != HpirTransformImports.VectorHeaderBytes + 32
                || !selection.Any(item => item.Metrics?.GetValueOrDefault("builtin.range.nullable_number_items_packed") == 2))
                throw new Exception($"O{level} did not emit one concrete inline nullable Number snapshot.");
            RequireNumber(program, 430, "two cursors both see Null and Number before EOF");
        }

        const string indexed = """
            let values = [null, 5];
            let first? = $std::ranges::at(values, 0);
            let second? = $std::ranges::at(values, 1);
            if first is null and second is not null { second } else { -1 }
            """;
        RequireNumber(Compile(indexed, 2), 5, "indexed reads preserve order and the Null item");
        HpirProgram absent = Compile("$std::ranges::at([null, 5], 2);", 2);
        RequireAbsent(() => HpirExecutor.Run(absent));
        if (HpirMachineImage.IsSupported)
        {
            using var image = HpirMachineImage.Compile(absent);
            RequireAbsent(() => HpirExecutor.RunMachineCandidate(image));
        }
        RetainOpenCursor();
        Console.WriteLine("PASS HPIR nullable Number snapshot: one owner, repeated cursors, Null item, absent index and collection");
    }

    private static void RequireAbsent(Func<ulong> run)
    {
        try { _ = run(); }
        catch (MsyncException error) when (error.Span?.Source == SourceName
            && error.Message.Contains("no item at index 2", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("An absent index in a nonempty snapshot was turned into a Null item.");
    }

    private static void RetainOpenCursor()
    {
        const string source = """
            fn churn() {
                let i = 0;
                let size = 0;
                loop {
                    if i == 4500 { break size; };
                    size = size + $std::string::length($std::string::repeat("😀", 512));
                    i = i + 1;
                }
            };
            let values = [null, 5];
            let result = 0;
            for item? in values {
                if item is null { result = churn(); } else { result = result + item; };
            };
            result
            """;
        HpirProgram program = Compile(source, 2);
        var interpreted = new RuntimeProfile();
        RequireCollection(HpirExecutor.Run(program, new() { Profile = interpreted }), interpreted);
        if (!HpirMachineImage.IsSupported) return;
        using var image = HpirMachineImage.Compile(program, observe: true);
        var machine = new RuntimeProfile();
        RequireCollection(HpirExecutor.RunMachineCandidate(image, new() { Profile = machine }), machine);
    }

    private static void RequireCollection(ulong bits, RuntimeProfile profile)
    {
        if (BitConverter.UInt64BitsToDouble(bits) != 4500 * 512 + 5
            || !profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") != 0))
            throw new Exception("A live nullable snapshot cursor did not survive actual collection between items.");
    }

    private static void RequireNumber(HpirProgram program, double expected, string scenario)
    {
        ulong interpreted = HpirExecutor.Run(program);
        if (BitConverter.UInt64BitsToDouble(interpreted) != expected)
            throw new Exception($"Interpreted nullable snapshot failed: {scenario}.");
        if (!HpirMachineImage.IsSupported) return;
        using var image = HpirMachineImage.Compile(program);
        if (HpirExecutor.RunMachineCandidate(image) != interpreted)
            throw new Exception($"Native nullable snapshot differs: {scenario}.");
    }

    private static HpirProgram Compile(string source, int level, CompilationTrace? trace = null)
    {
        HpirProgram program = Compiler.LowerHpir(Compiler.Analyze(source, SourceName), level, trace);
        string cwd = Environment.CurrentDirectory;
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(HpirBinding.Prepare(program, new(cwd, cwd)), stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
