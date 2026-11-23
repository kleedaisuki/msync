using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime.Hpir;

internal static class HpirVectorQuantifierChecks
{
    private const string Source = """
        let ranges = $std::ranges;
        let visits = 0;
        fn wanted(candidate: Type) -> Bool {
            visits = visits + 1;
            candidate is Number
        };

        let all = ranges::all_of([Number, String, Path], wanted);
        let some = ranges::any_of([Path, Number, String], wanted);
        let none = ranges::none([String, Number, Path], wanted);
        let exhausted = ranges::all_of([Number, Number], wanted);
        let one = ranges::take([Number, String], if visits == 8 { 1 } else { 0 });
        let split = ranges::all_of(one, wanted);
        let zero = ranges::take([Number, Path], visits - 9);
        let empty_taken = ranges::any(zero, wanted);
        let empty: Range<Type> = [];
        fn should_not_run(item) { visits = visits + 100; false };
        let empty_all = ranges::all_of(empty, should_not_run);
        let empty_any = ranges::any_of(empty, should_not_run);
        let empty_none = ranges::none_of(empty, should_not_run);
        if not all and some and not none and exhausted and split and not empty_taken
            and empty_all and not empty_any and empty_none { visits } else { -1 }
        """;

    private const string Reflection = """
        let ranges = $std::ranges;
        let reflected = 0;
        let visits = 0;
        fn never_called(value: Number) -> String {
            reflected = reflected + 1;
            "unexpected" -> call-ran.txt;
            value as String
        };
        let signature = $std::meta::signature_of(never_called);
        let members = $std::meta::members_of({
            reflected = reflected + 1;
            "unexpected" -> operand-ran.txt;
            scope { label: "item", file?: ./input }
        });
        let ok = ranges::all_of(signature::parameters, fn(type: Type) {
            visits = visits + 1;
            type is Number
        });
        if ok and ranges::size(members) == 2 and reflected == 0 { visits } else { -1 }
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "quantifier-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; ++level)
                VerifyJourney(root, Source, level, expected: 9);
            VerifyJourney(root, Reflection, 2, expected: 1);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR Type vector quantifiers: exact early exit, empty and split snapshots, no reflected operand execution");
    }

    private static void VerifyJourney(string root, string program, int level, int expected)
    {
        string source = Path.Combine(root, "quantifier.msync");
        File.WriteAllText(source, program);
        var typed = Compiler.Analyze(File.ReadAllText(source), source);
        string hir = Path.Combine(root, "quantifier.mhir");
        Msync.Hir.HirArtifact.WriteProduced(typed, hir);
        File.Delete(source);
        var obj = Reload(Compiler.LowerHpir(Msync.Hir.HirArtifact.Read(hir), level));
        File.Delete(hir);
        var executable = Reload(HpirBinding.Prepare(obj, new(root, root)));
        using var native = HpirMachineImage.IsSupported ? HpirMachineImage.Compile(executable) : null;
        Verify(HpirExecutor.Run(executable), level, "physical", root, expected);
        if (native is not null) Verify(HpirExecutor.RunMachineCandidate(native), level, "x64", root, expected);
    }

    private static void Verify(ulong bits, int level, string engine, string root, int expected)
    {
        double visits = BitConverter.UInt64BitsToDouble(bits);
        if (visits != expected || File.Exists(Path.Combine(root, "call-ran.txt")) || File.Exists(Path.Combine(root, "operand-ran.txt")))
            throw new InvalidOperationException($"Expected {expected} visited predicates and no reflection effects, O{level} {engine}; visited {visits}.");
    }

    private static HpirProgram Reload(HpirProgram image)
    {
        using MemoryStream bytes = new();
        HpirArtifact.WriteProduced(image, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }
}
