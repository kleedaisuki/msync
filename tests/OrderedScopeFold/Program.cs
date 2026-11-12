using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;

string root = Environment.CurrentDirectory;
bool verifyMetrics = !args.Contains("--semantic-only", StringComparer.Ordinal);
bool nativeOff = string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase);
bool? native = nativeOff ? false : args.Contains("--expect-native", StringComparer.Ordinal) ? true
    : RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
        Architecture.Arm64 when OperatingSystem.IsLinux() => null,
        _ => false,
    };

if (args is ["--emit", var directory])
{
    Directory.CreateDirectory(directory);
    foreach (var test in Fixtures.Cases())
    {
        Module portable = RoundTrip(Fixtures.ModuleFor(test));
        Artifact.Write(portable, Path.Combine(directory, test.Name + ".mso"));
        Artifact.Write(Binding.Relocate(portable, new(root, root)), Path.Combine(directory, test.Name + ".mexe"));
    }
    return;
}

foreach (var test in Fixtures.Cases())
{
    var portable = RoundTrip(Fixtures.ModuleFor(test));
    var bound = RoundTrip(Binding.Relocate(portable, new(root, root)));
    if (verifyMetrics && test.Called && test.Admission == Fixtures.Admission.Hit)
        CheckPrivateFrameProof(bound);
    foreach (Module image in new[] { portable, bound })
    {
        await CheckResult(test, image, null);
        RuntimeProfile aggregate = new();
        RuntimeProfile canonical = new(instructionSites: true);
        await CheckResult(test, image, aggregate);
        await CheckResult(test, image, canonical);
        if (verifyMetrics) CheckObservation(test, image.Stage, aggregate.Drain(), canonical.Drain(), native);
    }
    Console.WriteLine($"PASS {test.Name}: format-6 portable and bound");
}

async Task CheckResult(Fixtures.Case test, Module image, RuntimeProfile? profile)
{
    using StringWriter output = new();
    var result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root, Output: output) { Profile = profile });
    if (test.Escape != Fixtures.Escape.None)
    {
        var envelope = (ScopeValue)result;
        result = envelope["result"];
        Value observed = envelope["observed"];
        if (test.Escape == Fixtures.Escape.Row) observed = ((ScopeValue)observed)["sample"];
        EqualNumber(observed, test.Observed!.Value, test.Name + "/observed");
    }
    EqualNumber(result, test.Expected, test.Name + "/" + image.Stage);
    string expected = test.Printed is { } messages ? string.Concat(messages.Select(m => m + Environment.NewLine)) : "";
    if (output.ToString() != expected)
        throw new InvalidOperationException($"{test.Name}/{image.Stage}: selected effects changed: {output}");
}

static void CheckObservation(Fixtures.Case test, ArtifactStage stage, IReadOnlyList<RuntimeProfileSample> aggregate,
    IReadOnlyList<RuntimeProfileSample> canonical, bool? native)
{
    var frame = Entry(aggregate);
    var original = Entry(canonical);
    bool hit = test.Admission == Fixtures.Admission.Hit;
    ulong rows = hit ? (ulong)test.Values.Length : 0;
    ulong chunks = frame.GetValueOrDefault("number_fold_native_chunks");
    var expectedMisses = test.Admission == Fixtures.Admission.GuardMiss ? 1UL : 0UL;
    if (test.Called && (frame.GetValueOrDefault("calls") != 1 || frame.GetValueOrDefault("borrowed_frames") != 0))
        throw new InvalidOperationException($"{test.Name}/{stage}: the fold did not run in the called private frame.");
    if (frame.GetValueOrDefault("number_folds") != (hit ? 1UL : 0UL) || frame.GetValueOrDefault("number_fold_rows") != rows
        || frame.GetValueOrDefault("number_fold_misses") != expectedMisses
        || Sum(aggregate, "folded_literal_rows") != rows
        || original.GetValueOrDefault("number_folds") != 0 || original.GetValueOrDefault("number_fold_native_chunks") != 0
        || original.GetValueOrDefault("number_fold_misses") != 0 || Sum(canonical, "folded_literal_rows") != 0)
        throw new InvalidOperationException($"{test.Name}/{stage}: wrong admission: {string.Join(", ", frame)}");
    if ((!hit || rows < 512 || native == false) ? chunks != 0 : native == true && chunks < 2)
        throw new InvalidOperationException($"{test.Name}/{stage}: wrong native chunk count {chunks}.");
    if (hit && (Sum(aggregate, "literal_factories") != 0 || Sum(canonical, "literal_factories") != rows))
        throw new InvalidOperationException($"{test.Name}/{stage}: a folded literal created a guest row or canonical demand lost it.");
    if (!OpCounts(aggregate).SequenceEqual(OpCounts(canonical)))
        throw new InvalidOperationException($"{test.Name}/{stage}: aggregate fold changed canonical opcode totals.");
}

static void CheckPrivateFrameProof(Module image)
{
    if (ExecutionTargets.For(image).Dispatch(0)?.Any(i => i.Op == ExecutionTargets.ReadonlyNumberFoldEntry) != true)
        throw new InvalidOperationException("The called fold was not prepared.");
    var functions = image.Functions.ToArray();
    // A direct Module producer may bypass the wire reader. Preparing a fold
    // must not treat undeclared slots as proof of private frame storage.
    functions[0] = functions[0] with { LocalCount = functions[0].ParameterCount };
    if (ExecutionTargets.For(image with { Functions = functions }).Dispatch(0)
        ?.Any(i => i.Op == ExecutionTargets.ReadonlyNumberFoldEntry) == true)
        throw new InvalidOperationException("The bound proof admitted a fold beyond the declared private frame.");
}

static IReadOnlyDictionary<string, ulong> Entry(IEnumerable<RuntimeProfileSample> samples) =>
    samples.Single(s => s.Name == "vm.function" && s.Attributes["function.name"] == "entry").Metrics;

static ulong Sum(IEnumerable<RuntimeProfileSample> samples, string metric) => samples.Where(s => s.Name == "vm.function")
    .Aggregate(0UL, (total, s) => total + s.Metrics.GetValueOrDefault(metric));

static IEnumerable<(string Function, string Opcode, ulong Visits)> OpCounts(IEnumerable<RuntimeProfileSample> samples) => samples
    .Where(s => s.Name == "vm.opcode")
    .Select(s => (s.Attributes["function.id"], s.Attributes["opcode"], s.Metrics["hits"]))
    .OrderBy(s => s.Item1, StringComparer.Ordinal).ThenBy(s => s.Item2, StringComparer.Ordinal);

static void EqualNumber(Value value, double expected, string label)
{
    if (value is NumberValue number && (double.IsNaN(expected)
        ? double.IsNaN(number.Data) : BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected))) return;
    throw new InvalidOperationException($"{label}: expected {BitConverter.DoubleToInt64Bits(expected):x16} ({expected}), got {value}.");
}

static Module RoundTrip(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    stream.Position = 0;
    return Artifact.Read(stream, formBlocks: false);
}
