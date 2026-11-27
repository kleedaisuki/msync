using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

const string fixedCalls = """
    fn leaf(x: Number) -> Number { x + 2 };
    fn nonleaf(x: Number) -> Number { leaf(x) * 3 };
    nonleaf(5)
    """;

const string swap = """
    fn swap_steps(a: Number, b: Number, limit: Number) -> Number {
        let x = a;
        let y = b;
        let i = 0;
        loop {
            if i >= limit { break x * 10 + y; };
            let old = x;
            x = y;
            y = old;
            i = i + 1;
        }
    };
    swap_steps(1, 2, 3)
    """;

const string mutation = """
    let s = object { n: 5 };
    let q = s;
    q::n = 9;
    fn bump() -> Number { s::n = s::n + 1; 2 };
    let value = s::n + bump();
    value * 10 + q::n
    """;

const string escapingClosure = """
    fn make(seed: Number) {
        let n = seed;
        fn next(step: Number) -> Number { n = n + step; n };
        next
    };
    fn churn(limit: Number) -> Number {
        let i = 0;
        loop {
            if i >= limit { break i; };
            let temporary = make(i);
            i = temporary(1);
        }
    };
    let first = make(8);
    let separate = make(20);
    let count = churn(20000);
    first(3) + first(4) + separate(1) + count
    """;

const string finiteCallable = """
    fn triple(n: Number) -> Number { n * 3 };
    fn quint(n: Number) -> Number { n * 5 };
    fn choose(n: Number, first: Bool) -> Number {
        let call = if first { triple } else { quint };
        call(n)
    };
    choose(2, true) * 100 + choose(2, false)
    """;

const string layoutChoice = """
    fn observe(choice: Bool) -> Number {
        let s = if choice { object { n: 41 } } else { object { a: 0, n: 17 } };
        s::n + 1
    };
    observe(true) * 100 + observe(false)
    """;

const string sameOffset = """
    fn observe(choice: Bool) -> Number {
        let s = if choice { object { n: 41 } } else { object { n: 17, z: 0 } };
        s::n + 1
    };
    observe(true) * 100 + observe(false)
    """;

const string vectorGc = """
    fn churn(limit: Number) -> Number {
        let i = 0;
        loop {
            if i >= limit { break i; };
            let temporary = object { value: i };
            i = temporary::value + 1;
        }
    };
    let first = object { n: 2 };
    let alias = first;
    let second = object { n: 7 };
    let view = [first, second];
    first = object { n: 100 };
    alias::n = 20;
    let sum = 0;
    for row in view {
        let work = churn(12000);
        sum = sum + row::n + (work - 12000);
    };
    sum
    """;

const string word32 = """
    fn combine(n: Number, m: Number) -> Number {
        $std::bits::bit_xor($std::bits::rotate_right(n, 1), m)
    };
    combine(1, 2)
    """;

if (args is ["hpir-inspect", var path])
{
    using Stream image = File.OpenRead(path);
    HpirDisassembly.Write(HpirArtifact.ReadTrusted(image), Console.Out);
    return;
}

if (HpirMachineBlockChecks.ProcessCommand(args) || HpirTransferChecks.ProcessCommand(args)) return;
if (args is ["path-test"]) { HpirPathChecks.Run(); return; }
if (args is ["connection-display-test"]) { HpirConnectionDisplayChecks.Run(); return; }
if (args is ["selection-iterator-test"]) { HpirSelectionIteratorChecks.Run(); return; }
if (args is ["rename-test"]) { HpirFileRenameChecks.Run(); return; }
if (args is ["directory-test"]) { HpirDirectoryChecks.Run(); return; }
if (args is ["directory-authority-test"]) { HpirDirectoryAuthorityChecks.Run(); return; }
if (args is ["path-filter-test"]) { HpirPathFilterChecks.Run(); return; }
if (args is ["global-test"]) { HpirGlobalChecks.Run(); return; }
if (args is ["type-test"]) { HpirTypeChecks.Run(); return; }
if (args is ["session-test"]) { HpirSessionChecks.Run(); return; }
if (args is ["promotion-test"]) { HpirPromotionChecks.Run(); return; }
if (args is ["cooperative-test"]) { HpirCooperativeChecks.Run(); return; }
if (args is ["tail-test"]) { HpirTailChecks.Run(); return; }
if (args is ["frontier-test"]) { HpirFrontierChecks.Run(); return; }
if (args is ["math-test"]) { HpirScalarImportChecks.Run(); Console.WriteLine("PASS HPIR scalar imports and native math selection"); return; }
if (args is ["range-test"]) { HpirRangeChecks.Run(); return; }
if (args is ["transform-test"]) { HpirTransformChecks.Run(); return; }
if (args is ["counted-range-test"]) { HpirCountedRangeChecks.Run(); return; }
if (args is ["zip-test"]) { HpirZipChecks.Run(); return; }
if (args is ["text-chunks-test"]) { HpirTextChunkChecks.Run(); return; }
if (args is ["concat-range-test"]) { HpirConcatChecks.Run(); return; }
if (args is ["sorted-range-test"]) { HpirSortedChecks.Run(); return; }
if (args is ["future-test"]) { HpirFutureSourceChecks.Run(); return; }
if (args is ["process-source-test"]) { HpirProcessSourceChecks.Run(); return; }
if (args is ["transfer-receipt-test"]) { HpirTransferReceiptChecks.Run(); return; }
if (args is ["number-effect-test"]) { HpirNumberEffectChecks.Run(); return; }
if (args is ["union-transfer-test"]) { HpirUnionTransferChecks.Run(); return; }
if (args is ["union-future-test"]) { HpirUnionFutureChecks.Run(); return; }
if (args is ["file-write-test"]) { HpirFileWriteChecks.Run(); return; }
if (args is ["file-byte-test"]) { HpirFileByteChecks.Run(); return; }
if (args is ["vector-access-test"]) { HpirVectorAccessChecks.Run(); return; }
if (args is ["nullable-vector-test"]) { HpirNullableVectorChecks.Run(); return; }
if (args is ["cursor-field-test"]) { HpirSnapshotFieldChecks.Run(); return; }
if (args is ["vector-quantifier-test"]) { HpirVectorQuantifierChecks.Run(); return; }
if (args is ["member-join-test"]) { HpirMemberJoinChecks.Run(); return; }
if (args is ["scalar-cast-test"]) { HpirScalarCastChecks.Run(); return; }
if (args is ["text-observation-test"]) { HpirTextObservationChecks.Run(); return; }
if (args is ["text-delivery-test"]) { HpirTextDeliveryChecks.Run(); return; }
if (args is ["string-test"]) { HpirStringChecks.Run(); return; }
if (args is ["regex-test"]) { HpirRegexChecks.Run(); return; }
if (args is ["regex-find-test"]) { HpirRegexFindChecks.Run(); return; }
if (args is ["live-text-test"]) { HpirLiveTextChecks.Run(); return; }
if (args is ["live-text-join-test"]) { HpirLiveTextChecks.RunJoins(); return; }
if (args is ["file-metadata-test"]) { HpirFileMetadataChecks.Run(); return; }
if (args is ["file-text-test"]) { HpirFileTextChecks.Run(); return; }
if (args is ["shared-transfer-test"]) { HpirSharedTransferChecks.Run(); return; }

for (int level = 0; level <= 3; ++level)
{
    Run("fixed-calls", fixedCalls, 21, level);
    Run("loop-swap", swap, 21, level);
    Run("two-code-entries", finiteCallable, 610, level);
    Run("two-scope-layouts", layoutChoice, 4218, level);
    Run("uniform-offset", sameOffset, 4218, level);
    Run("vector-membership-and-gc", vectorGc, 27, level, collects: true);
    Run("word32", word32, 2147483650, level);
    Run("field-snapshot", mutation, 120, level);
    Run("escaping-closure-gc", escapingClosure, 20047, level, collects: true);
}
HpirTransferChecks.Run();
HpirPathChecks.Run();
HpirConnectionDisplayChecks.Run();
HpirSelectionIteratorChecks.Run();
HpirDirectoryChecks.Run();
HpirPathFilterChecks.Run();
HpirGlobalChecks.Run();
HpirTypeChecks.Run();
HpirScalarImportChecks.Run();
HpirStringChecks.Run();
HpirLiveTextChecks.Run();
HpirMachineBlockChecks.Run();
HpirPromotionChecks.Run();
HpirCooperativeChecks.Run();
HpirTailChecks.Run();
HpirFrontierChecks.Run();
HpirRangeChecks.Run();
HpirTransformChecks.Run();
HpirCountedRangeChecks.Run();
HpirZipChecks.Run();
HpirFutureSourceChecks.Run();
HpirProcessSourceChecks.Run();
HpirTransferReceiptChecks.Run();
HpirUnionTransferChecks.Run();
HpirUnionFutureChecks.Run();
HpirNumberEffectChecks.Run();
HpirFileWriteChecks.Run();
HpirVectorAccessChecks.Run();
HpirVectorQuantifierChecks.Run();
HpirMemberJoinChecks.Run();
HpirScalarCastChecks.Run();
HpirTextObservationChecks.Run();
Console.WriteLine("PASS HPIR source → optimized LLVM → physical image → guest VM, O0–O3");

static void Run(string name, string source, double expected, int level, bool collects = false)
{
    var observations = new List<CompilationObservation>();
    var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
    var module = Compiler.Analyze(source, name + ".msync", trace);
    HpirProgram produced = Compiler.LowerHpir(module, level, trace);
    bool shapes = produced.Imports.Any(import => import.Service == HpirService.HasLayout);
    if (name == "two-scope-layouts" && !shapes || name is ("field-snapshot" or "uniform-offset") && shapes)
        throw new Exception($"{name} O{level}: physical layout dispatch was not restricted to real shape alternatives");
    using var image = new MemoryStream();
    HpirArtifact.WriteProduced(produced, image, trace);
    image.Position = 0;
    HpirProgram detached = HpirArtifact.ReadTrusted(image, trace);
    HpirProgram executable = HpirBinding.Prepare(detached, new(Environment.CurrentDirectory, Environment.CurrentDirectory), trace: trace);
    if (!ReferenceEquals(detached.Functions, executable.Functions))
        throw new Exception($"{name} O{level}: binding replaced the physical code instead of linking resources");
    image.Position = 0;
    image.SetLength(0);
    HpirArtifact.WriteProduced(executable, image, trace);
    image.Position = 0;
    HpirProgram loaded = HpirArtifact.ReadTrusted(image, trace);
    var profile = collects ? new RuntimeProfile() : null;
    double actual = BitConverter.UInt64BitsToDouble(HpirExecutor.Run(loaded, profile is null ? null : new() { Profile = profile }));
    if (profile is not null && !profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") != 0))
        throw new Exception($"{name} O{level}: the fixture did not trigger a real guest collection");
    if (actual != expected) throw new Exception($"{name} O{level}: expected {expected}, got {actual}");
    if (!observations.Any(item => item.Name == "compiler.hpir.select.function"
        && item.Attributes?.ContainsKey("function.name") == true
        && item.Metrics?.ContainsKey("frame.bytes") == true))
        throw new Exception($"{name} O{level}: missing producer attribution");
}
