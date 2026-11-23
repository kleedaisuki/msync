using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime.Hpir;

internal static class HpirTypeChecks
{
    private const string Source = """
        fn accepts_number(candidate: Type) -> Bool { candidate is Number };
        fn choose(flag: Bool) -> Type { if flag { Number } else { String } };
        fn value_kind(value: Number) -> Bool { decltype(value) is Number };
        let cold = accepts_number(choose(false));
        let warm = accepts_number(choose(true));
        let unselected = ssh://hpir-type-only.invalid;
        let connection_path = decltype(unselected @ ~) is Path;
        if not cold and warm and value_kind(1) and connection_path { 17 } else { 3 }
        """;

    internal static void Run()
    {
        string directory = Environment.CurrentDirectory;
        for (int level = 0; level <= 3; ++level)
        {
            HpirProgram portable = Load(Compiler.LowerHpir(Compiler.Analyze(Source, "actual-types.msync"), level));
            HpirProgram bound = Load(HpirBinding.Prepare(portable, new(directory, directory)));
            using var machine = HpirMachineImage.IsSupported ? HpirMachineImage.Compile(bound) : null;
            if (!bound.Imports.Any(import => import.Service == HpirService.TypeAccepts)
                || BitConverter.UInt64BitsToDouble(HpirExecutor.Run(bound)) != 17
                || machine is not null && BitConverter.UInt64BitsToDouble(HpirExecutor.RunMachineCandidate(machine)) != 17)
                throw new InvalidOperationException($"HPIR type witnesses did not preserve actual runtime alternatives, O{level}.");
        }
        Console.WriteLine("PASS HPIR detached concrete type witnesses across calls, O0–O3");
    }

    private static HpirProgram Load(HpirProgram program)
    {
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }
}
