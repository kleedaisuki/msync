using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirMemberJoinChecks
{
    private const string Source = """
        let ranges = $std::ranges;
        let first = scope { n: 2 };
        let second = scope { n: 4 };
        let seen = 0;
        let items = [first, first, second];
        let view = ranges::map(items, fn(item) {
            item::n = item::n + 1;
            seen = seen + 1;
            item::n as String
        });
        first::n = 5;
        let before = seen;
        let joined = $std::string::join(view, "|");
        if before == 0 and seen == 3 and first::n == 7 and second::n == 5 {
            joined -> ./result.txt;
        } else {
            "wrong" -> ./result.txt;
        };
        acquire;
        """;

    private const string Witnesses = """
        let meta = $std::meta;
        let nested = meta::range_of(meta::add_null(Number));
        let same_shape = meta::range_of(meta::add_null(Number));
        let nullable_path = meta::add_null(Path);
        let input? = $std::io::read_line();
        let actual = if input? { nested } else { nullable_path };
        let wanted = if input? { same_shape } else { nullable_path };
        let other = if input? { nullable_path } else { nested };
        fn label(value: Type, equal: Type, different: Type) -> String {
            if meta::is_same(value, equal) and not meta::is_same(value, different) {
                meta::type_name(value)
            } else {
                "wrong Type identity"
            }
        };
        label(actual, wanted, other) -> ./result.txt;
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "scope-member-join", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                CheckMembers(root, level);
                CheckWitnesses(root, level);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR member join and Type display: delayed callbacks, repeated alias identity, structural witnesses and exact Text");
    }

    private static void CheckMembers(string root, int level)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram executable = Prepare(root, Source, level, trace);
        if (!observations.Any(record => record.Metrics?.GetValueOrDefault("range.transform.scope_text_vector_plans") == 1))
            throw new InvalidOperationException($"O{level}: Scope snapshot did not select the physical Text map");
        CheckOutput(root, executable, level, "6|7|5");
    }

    private static void CheckWitnesses(string root, int level)
    {
        HpirProgram executable = Prepare(root, Witnesses, level);
        if (!executable.Imports.Any(import => import.Service == HpirService.TypeName))
            throw new InvalidOperationException($"O{level}: dynamic Type display was not selected");
        CheckOutput(root, executable, level, "Range<Number?>", input: "");
        CheckOutput(root, executable, level, "Path?", input: "present\n");
    }

    private static HpirProgram Prepare(string root, string program, int level, CompilationTrace? trace = null)
    {
        string source = Path.Combine(root, "scope-join.msync");
        File.WriteAllText(source, program);
        string hir = Path.ChangeExtension(source, ".mhir");
        Msync.Hir.HirArtifact.WriteProduced(Compiler.Analyze(File.ReadAllText(source), source), hir);
        File.Delete(source);
        var portable = Reload(Compiler.LowerHpir(Msync.Hir.HirArtifact.Read(hir), level, trace));
        File.Delete(hir);
        return Reload(HpirBinding.Prepare(portable, new(root, root)));
    }

    private static void CheckOutput(string root, HpirProgram executable, int level, string expected, string? input = null)
    {
        using var physical = input is null ? null : new StringReader(input);
        var options = new ExecutionOptions(WorkingDirectory: root) { Input = physical };
        Verify(root, level, expected, HpirExecutor.RunOutcome(executable, options));
        if (!HpirMachineImage.IsSupported) return;
        using var native = HpirMachineImage.Compile(executable);
        using var nativeInput = input is null ? null : new StringReader(input);
        Verify(root, level, expected, HpirExecutor.RunMachineOutcome(native, options with { Input = nativeInput }));
    }

    private static void Verify(string root, int level, string expected, HpirRunOutcome outcome)
    {
        string path = Path.Combine(root, "result.txt");
        string recorded = File.ReadAllText(path);
        File.Delete(path);
        if (outcome.FailedTransfers != 0 || recorded != expected)
            throw new InvalidOperationException($"O{level}: expected '{expected}'; wrote '{recorded}'.");
    }

    private static HpirProgram Reload(HpirProgram image)
    {
        using MemoryStream bytes = new();
        HpirArtifact.WriteProduced(image, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }
}
