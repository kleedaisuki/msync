using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

if (args is ["--child"])
{
    Console.Write("echo:" + await Console.In.ReadToEndAsync());
    return;
}

bool metrics = !args.Contains("--semantic-only", StringComparer.Ordinal);
bool? native = string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase)
    ? false
    : RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
        Architecture.Arm64 when OperatingSystem.IsLinux() => null,
        _ => false,
    };
string child = Environment.ProcessPath ?? throw new InvalidOperationException("Missing apphost path.");
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "typed-local-frames-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ScopeStorageChecks.Run(root, metrics, Routes);
    if (args.Contains("--scope-storage-only", StringComparer.Ordinal)) return;

    foreach (Module image in Routes(Fixtures.Mixed(child), root))
    {
        RuntimeProfile profile = new(instructionSites: true);
        using GatedLineInput input = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        Task<Value> pending = Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Input = input, Profile = profile },
            deadline.Token).AsTask();
        Task first = await Task.WhenAny(input.Requested, pending).WaitAsync(deadline.Token);
        if (first == pending) _ = await pending;
        Require(first == input.Requested && !pending.IsCompleted, "Callee did not suspend after detaching.");
        input.Release();
        CheckMixed(await pending.WaitAsync(deadline.Token));
        if (metrics) CheckMixedMetrics(profile.Drain());
        Console.WriteLine($"PASS mixed typed-local boundaries: {image.Stage}");
    }

    await CompactNumericSlotsChecks.Run(root, metrics, Routes);

    foreach (int count in new[] { 0, 257, 258 })
        foreach (Module source in CompactNumericSlotsChecks.ExitShapes(count))
            foreach (Module image in Routes(source, root))
            {
                RuntimeProfile profile = new();
                var result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
                CheckNumber(result["steps"], count, "exact resumed iteration count");
                CheckBits(result["value"], count % 2 == 0 ? long.MinValue : 0L, "native exit sign");
                CheckBits(result["nan"], count % 2 == 0 ? Fixtures.NegativeNaN : Fixtures.NegativeNaN ^ long.MinValue,
                    "native exit NaN payload and sign");
                if (metrics) CheckExitMetrics(profile.Drain(), count, native);
            }

    // A session may retain bindings beyond the activation. Use the same guest
    // operations rather than asserting standalone admission from stage alone.
    RuntimeProfile sessionProfile = new(instructionSites: true);
    await using var session = new ExecutionSession(new(WorkingDirectory: root) { Profile = sessionProfile });
    var sessionResult = (ScopeValue)await session.ExecuteAsync(RoundTrip(Fixtures.NumericExit(257)));
    CheckNumber(sessionResult["steps"], 257, "session result");
    CheckNumber(session.GetLocalValue(0)!, 257, "session retained loop counter");
    CheckBits(session.GetLocalValue(1)!, 0, "session retained sign");
    if (metrics)
        Require(sessionProfile.Drain().Where(sample => sample.Name == "vm.function")
            .All(sample => sample.Metrics.GetValueOrDefault("raw_local_writes") == 0), "Session used a private numeric lane.");

    Console.WriteLine("PASS typed Number locals: format-6 portable and bound, suspension, original Process roots, capture alias, native exit, session fallback");
}
finally { Directory.Delete(root, recursive: true); }

static IEnumerable<Module> Routes(Module module, string root)
{
    var portable = RoundTrip(module);
    yield return portable;
    yield return RoundTrip(Binding.Bind(portable, new(root, root)));
}

static Module RoundTrip(Module module)
{
    using MemoryStream encoded = new();
    Artifact.Write(module, encoded);
    encoded.Position = 0;
    return Artifact.Read(encoded, formBlocks: false);
}

static void CheckMixed(Value value)
{
    var result = (ScopeValue)value;
    Require(result["echo"] is StringValue { Data: Fixtures.Echo }, "The Process in the original argument Scope did not survive.");
    CheckBits(result["zero"], long.MinValue, "caller Number across borrowed detachment");
    CheckBits(result["nan"], Fixtures.NegativeNaN, "NaN payload/sign at LoadStorage");
    CheckBits(result["before"], long.MinValue, "captured Number before mutation");
    CheckNumber(result["shared"], 3, "capture mutation through shared Cell");
    var resumed = (ScopeValue)result["resumed"];
    CheckBits(resumed["updated"], 0, "detached Number across asynchronous input");
    CheckBits(resumed["untouched"], Fixtures.NegativeNaN, "second raw input across detachment");
    Require(result["nullable"] is NullValue, "Null overwrite left an older raw Number visible.");
}

static void CheckMixedMetrics(IReadOnlyList<RuntimeProfileSample> profile)
{
    var root = Function(profile, Fixtures.Entry);
    var suspended = Function(profile, Fixtures.Detached);
    Require(root.GetValueOrDefault("raw_local_writes") > 0, "No private numeric locals reached the raw lane.");
    Require(suspended.GetValueOrDefault("raw_local_writes") == 4 && suspended["detached_frames"] == 1
        && suspended.GetValueOrDefault("raw_arguments") == 2,
        "Suspending writable borrowed activation did not detach onto a private lane.");
    Require(Function(profile, Fixtures.Capture).GetValueOrDefault("raw_local_writes") == 4,
        "Writing through a captured Cell used the private lane.");
}

static void CheckExitMetrics(IReadOnlyList<RuntimeProfileSample> profile, int count, bool? native)
{
    var frame = Function(profile, Fixtures.Loop);
    Require(frame.GetValueOrDefault("raw_local_writes") > 0, "The numeric exit fixture did not enter the local lane.");
    Require(frame["number_sidecar_arrays"] == 1 && frame["number_sidecar_slots"] == 3,
        "The numeric loop reserved slots outside its writable suffix.");
    Require(frame["numeric_loop_entries"] == (count > 0 ? 1UL : 0UL), "Unexpected numeric region admission.");
    ulong entries = frame.GetValueOrDefault("jit_entries");
    Require(count == 0 || native == false ? entries == 0 : native == true ? entries == 1 : entries <= 1,
        "Unexpected native exit.");
    if (count > 0)
        Require(frame.GetValueOrDefault("numeric_local_commits") > 0, "Numeric tier materialized changed private exit locals.");
}

static IReadOnlyDictionary<string, ulong> Function(IReadOnlyList<RuntimeProfileSample> samples, string name) =>
    samples.Single(sample => sample.Name == "vm.function" && sample.Attributes["function.name"] == name).Metrics;

static void CheckNumber(Value value, double expected, string label) =>
    CheckBits(value, BitConverter.DoubleToInt64Bits(expected), label);

static void CheckBits(Value value, long expected, string label) =>
    Require(value is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == expected,
        $"{label}: expected Number bits {expected:x16}, got {value}.");

static void Require(bool predicate, string message)
{
    if (!predicate) throw new InvalidOperationException(message);
}

internal sealed class GatedLineInput : TextReader
{
    private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> line = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Requested => requested.Task;
    internal void Release() => line.TrySetResult("release");

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        requested.TrySetResult();
        return new(line.Task.WaitAsync(cancellationToken));
    }
}
