using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

if (args.FirstOrDefault() == "--child")
{
    Console.Write("echo:" + await Console.In.ReadToEndAsync());
    return;
}

string child = Environment.ProcessPath ?? throw new InvalidOperationException("Missing child executable path.");
var fixtures = new[] {
    (Name: "activation", Image: Fixtures.Activations(), Output: Fixtures.FrameOutput),
    (Name: "process", Image: Fixtures.Processes(child), Output: Fixtures.ProcessOutput),
    (Name: "effect-tails", Image: Fixtures.EffectTails(), Output: Fixtures.TailOutput),
    (Name: "raw-numbers", Image: Fixtures.RawNumbers(), Output: Fixtures.RawOutput),
};
if (args is ["--emit", var directory])
{
    Directory.CreateDirectory(directory);
    foreach (var fixture in fixtures) Artifact.Write(fixture.Image, Path.Combine(directory, fixture.Name + ".mso"));
    return;
}
bool metricsExpected = !args.Contains("--semantic-only", StringComparer.Ordinal);
bool native = RuntimeInformation.ProcessArchitecture == Architecture.X64
    && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
    && !string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase);
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "captured-call-frames-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var fixture in fixtures)
    {
        Module portable = RoundTrip(fixture.Image);
        Module executable = RoundTrip(Binding.Bind(portable, new(root, root)));
        foreach (Module image in new[] { portable, executable })
        {
            using StringWriter output = new();
            RuntimeProfile profile = new(instructionSites: fixture.Name != "effect-tails");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using var input = fixture.Name == "raw-numbers" ? new GatedLineInput() : null;
            var options = new ExecutionOptions(WorkingDirectory: root, Output: output) { Profile = profile, Input = input };
            Task<Value> execution = Interpreter.ExecuteAsync(image, options, deadline.Token).AsTask();
            if (input is not null)
            {
                Task ready = await Task.WhenAny(input.Requested, execution).WaitAsync(deadline.Token);
                if (ready == execution) _ = await execution;
                Require(ready == input.Requested && !execution.IsCompleted, "The borrowed read did not suspend.");
                input.Release();
            }
            Value result = await execution;
            if (input is null) Require(result is NullValue, "Expected a Null entry result.");
            else CheckRawResult(result);
            CheckOutput(fixture.Output, output);
            if (metricsExpected) CheckFrames(fixture.Name, profile.Drain(), native);
        }
    }
    using StringWriter sessionOutput = new();
    RuntimeProfile sessionProfile = new();
    await using var session = new ExecutionSession(new(WorkingDirectory: root, Output: sessionOutput) { Profile = sessionProfile });
    Require(await session.ExecuteAsync(fixtures[0].Image) is NullValue, "Session changed return.");
    CheckOutput(fixtures[0].Output, sessionOutput);
    Require(sessionProfile.Drain().Where(sample => sample.Name == "vm.function").All(frame =>
        frame.Metrics.GetValueOrDefault("split_frames") == 0 && frame.Metrics["borrowed_frames"] == 0),
        "Session reused persistent slots as a split borrowed frame.");
    using StringWriter rawSessionOutput = new();
    using StringReader rawSessionInput = new("release" + Environment.NewLine);
    RuntimeProfile rawSessionProfile = new();
    await using var rawSession = new ExecutionSession(new(WorkingDirectory: root, Output: rawSessionOutput)
        { Profile = rawSessionProfile, Input = rawSessionInput });
    CheckRawResult(await rawSession.ExecuteAsync(fixtures[^1].Image));
    CheckOutput(Fixtures.RawOutput, rawSessionOutput);
    Require(rawSessionProfile.Drain().Where(sample => sample.Name == "vm.function").All(frame =>
        frame.Metrics.GetValueOrDefault("raw_arguments") == 0 && frame.Metrics["borrowed_frames"] == 0),
        "Session counted raw arguments as borrowed from persistent slots.");
    DispatchResumption.Check(root);
    Console.WriteLine("PASS captured call frames: standalone portable, executable, and session fallback");
}
finally { Directory.Delete(root, recursive: true); }

static Module RoundTrip(Module image)
{
    using MemoryStream stream = new();
    Artifact.Write(image, stream);
    stream.Position = 0;
    return Artifact.Read(stream, formBlocks: false);
}

static void CheckOutput(string[] expected, StringWriter output) =>
    Require(output.ToString() == string.Join(Environment.NewLine, [.. expected, ""]), $"Output changed: {output}");

static void CheckRawResult(Value result)
{
    var values = result as ScopeValue ?? throw new InvalidOperationException("Expected raw test output Scope.");
    var detached = (ScopeValue)values["detached"];
    CheckBits(values["zero"], long.MinValue, "contiguous raw return");
    CheckBits(values["nan"], Fixtures.NegativeNaNPayload, "split raw return");
    CheckBits(values["promoted"], long.MinValue, "promoted raw parameter");
    CheckBits(detached["before"], long.MinValue, "live old argument");
    CheckBits(detached["nan"], Fixtures.NegativeNaNPayload, "other argument after detach");
    Require(detached["after"] is NumberValue { Data: 42 } && values["resumed"] is NumberValue { Data: 207 },
        "Mutation or suspended raw call result changed.");

    static void CheckBits(Value value, long expected, string label) =>
        Require(value is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == expected,
            $"{label} lost its Number bits: {value} (expected {expected:x16}).");
}

static void CheckFrames(string fixture, IReadOnlyList<RuntimeProfileSample> samples, bool native)
{
    var frames = samples.Where(sample => sample.Name == "vm.function").ToDictionary(frame => frame.Attributes["function.name"]);
    if (fixture == "activation")
    {
        Check("multi-read", 3, 3, 3, 0);
        Check("write-through", 2, 2, 2, 0);
        Check("replace-capture", 2, 2, 2, 2);
        Check("promote-argument", 2, 2, 2, 2);
        Check("read-owned", 3, 3, 0, 0);
        Check("extra-local", 1, 0, 0, 0);
        Check("replace-plain-capture", 2, 2, 2, 2);
        Check("copied-cell-argument", 1, 1, 1, 1);
    }
    else if (fixture == "effect-tails")
    {
        Check("read-captured-tail", 137, 137, 137, 0, 137);
        Check("write-captured-tail", 137, 137, 137, 0, 137);
        Check("commit-raw-tail", 137, 137, 137, 137, 137);
        Require(frames["read-captured-tail"].Metrics.GetValueOrDefault("jit_tail_entries") == (native ? 74UL : 0UL),
            "Ready read-only captured Cells did not use the borrowed native tail.");
        Require(frames["write-captured-tail"].Metrics.GetValueOrDefault("jit_tail_entries") == 0,
            "Writable captured Cell was used by a native tail.");
        Require(frames["commit-raw-tail"].Metrics.GetValueOrDefault("jit_tail_entries") == (native ? 74UL : 0UL),
            "The numeric tail could not commit an ordinary raw slot.");
    }
    else if (fixture == "raw-numbers")
    {
        Check("raw-contiguous-identity", 1, 1, 0, 0, 1);
        Check("raw-split-identity", 1, 1, 1, 0, 1);
        Check("raw-detach", 1, 1, 1, 1, 2);
        Check("raw-promote", 1, 1, 1, 1, 1);
        Check("raw-promoted-read", 1, 1, 0, 0);
        Check("raw-await", 1, 1, 1, 0, 1);
    }
    else
    {
        Check("return-process", 1, 1, 1, 0);
        Check("write-original-argument", 1, 1, 1, 1, 1);
        Check("write-captured-cell", 1, 1, 1, 0);
    }

    void Check(string name, ulong calls, ulong borrowed, ulong split, ulong detached, ulong raw = 0)
    {
        var metrics = frames[name].Metrics;
        Require((metrics["calls"], metrics["borrowed_frames"], metrics.GetValueOrDefault("split_frames"),
                 metrics["detached_frames"], metrics.GetValueOrDefault("raw_arguments"))
            == (calls, borrowed, split, detached, raw),
            $"{name}: expected calls/borrowed/split/detached/raw {calls}/{borrowed}/{split}/{detached}/{raw}, got {string.Join(", ", metrics)}");
    }
}

static void Require(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
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
