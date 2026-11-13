using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using SourceCompiler = Msync.Compiler.Compiler;

(string Name, string Source)[] cases =
[
    ("mixed-namespace", """
        let item = scope {
            name: "payload",
            file: ./relative/file,
            endpoint: $local,
            count: 7,
            enabled: true,
            empty?: null,
        };
        let alias = item;
        alias::count = 9;
        item::count
        """),
    ("captured-location", """
        let state = 1;
        let next = fn() { state = state + 1; state };
        let first = next();
        state = 10;
        first + next()
        """),
    ("finite-branch", """
        fn choose(flag: Bool) {
            if flag { scope { text: "left", count: 1 } }
            else { scope { count: 2, text: "right" } }
        };
        let value = choose(false);
        value::count
        """),
    ("iteration", """
        let total = 0;
        for item in [1, 2, 3, 4] {
            total = total + item;
        };
        total
        """),
    ("recursive-function", """
        fn count(n: Number) {
            if n == 0 { 0 } else { 1 + count(n - 1) }
        };
        count(12)
        """),
    ("returned-closure", """
        fn make(start: Number) {
            let value = start;
            fn() { value = value + 1; value }
        };
        let next = make(10);
        let first = next();
        first + next()
        """),
    ("nullable-branch", """
        let record = scope { number?: null };
        record::number = 3;
        let value? = record::number;
        if value is null { 0 } else { value + 1 }
        """)
];

int checkedPrograms = 0;
foreach ((string name, string source) in cases)
{
    HirModule hir = SourceCompiler.Analyze(source, name + ".msync");
    using MemoryStream hirBytes = new();
    HirArtifact.Write(hir, hirBytes);
    hirBytes.Position = 0;
    HirModule standalone = HirArtifact.Read(hirBytes);
    foreach (int level in new[] { 0, 2 })
    {
        byte[]? sourceBytes = null;
        foreach (HirModule input in new[] { hir, standalone })
        {
            NativeProgram program = SourceCompiler.LowerInstructions(input, level);
            NativeArtifact.Validate(program);
            using MemoryStream encoded = new();
            NativeArtifact.WriteProduced(program, encoded);
            byte[] currentBytes = encoded.ToArray();
            if (sourceBytes is not null && !currentBytes.AsSpan().SequenceEqual(sourceBytes))
                throw new InvalidOperationException($"{name}: standalone HIR changed native artifact bytes at O{level}.");
            sourceBytes = currentBytes;
            if (program.Constants.Any(constant => constant.OriginId != -1 || constant.OriginOffset != 0))
                throw new InvalidOperationException($"{name}: non-session artifact retained ephemeral occurrence identities.");
            encoded.Position = 0;
            NativeProgram decoded = NativeArtifact.Read(encoded);
            if (decoded.Functions.Count != program.Functions.Count
                || decoded.Layouts.Count != program.Layouts.Count)
                throw new InvalidOperationException($"{name}: native artifact changed the lowered program.");
            foreach (NativeFunctionCode function in decoded.Functions)
                foreach (NativeInstruction instruction in function.Instructions)
                    if (instruction.Op == NativeOp.CallNative && instruction.Symbol is { } symbol
                        && (symbol.StartsWith("arena.", StringComparison.Ordinal)
                            || symbol.StartsWith("storage.", StringComparison.Ordinal)
                            || symbol.StartsWith("slots.", StringComparison.Ordinal)))
                        throw new InvalidOperationException($"{name}: source allocation escaped into a host allocator.");
            checkedPrograms++;
        }
    }
    Console.WriteLine($"PASS {name}: source and standalone HIR, O0/O2, checked native artifact");
}
Console.WriteLine($"PASS {checkedPrograms} direct native lowering/artifact routes");

List<CompilationObservation> observations = [];
CompilationTrace trace = new(observations.Add, includeOptimizerPasses: true);
NativeProgram traced = SourceCompiler.Compile(cases[0].Source, "native-trace.msync", 2, trace);
CompilationObservation lowering = observations.Single(observation => observation.Name == "compiler.lower");
CompilationObservation optimization = observations.Single(observation => observation.Name == "compiler.optimize");
CompilationSize expected = new(traced.Functions.Count,
    traced.Functions.Sum(function => (long)function.Instructions.Count),
    traced.Functions.Sum(function => (long)function.Slots.Count));
if (lowering.Outcome != CompilationOutcome.Ok || optimization.Outcome != CompilationOutcome.Ok
    || lowering.Output is null || lowering.Output != optimization.Input || optimization.Output != expected)
    throw new InvalidOperationException("Native compiler observations must describe actual lowered and optimized register code.");
CompilationObservation[] passes = observations.Where(observation => observation.Name.StartsWith("optimizer.", StringComparison.Ordinal)).ToArray();
if (passes.Length == 0 || passes.Any(pass => !pass.Aggregated || pass.Invocations < 1
    || pass.Input is null || pass.Outcome != CompilationOutcome.Ok)
    || !passes.Any(pass => pass.ChangedInvocations > 0))
    throw new InvalidOperationException("Native optimizer profiles must report actual analyses, passes and changes.");
observations.Clear();
_ = SourceCompiler.Compile(cases[0].Source, "native-phases.msync", 2, new(observations.Add));
if (observations.Any(observation => observation.Name.StartsWith("optimizer.", StringComparison.Ordinal)))
    throw new InvalidOperationException("Phase-only observation must not enable optimizer pass profiling.");
Console.WriteLine("PASS native compiler trace sizes describe actual register code");
