using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using static Msync.Core.Hpir.HpirOp;
using static Msync.Core.Hpir.HpirWidth;

const int depth = 10_000;
SourceSpan recursiveSite = new("hpir-frame-test.msync", 74, 12);
HpirProgram program = new(
[
    new("entry", 64, U64, [],
    [
        F64(0, 9),
        new(AddressOf, D: 8, A: 0),
        new(CopyU64, D: 16, A: ~8),
        F64(24, 1),
        F64(32, depth),
        new(Call, D: 40, A: 0),
        new(Call, D: 48, A: 0),
        new(AddF64, D: 56, A: 16, B: 40),
        new(AddF64, D: 56, A: 56, B: 48),
        new(AddF64, D: 56, A: 56, B: ~8),
        new(Return, A: 56)
    ], [new(1, [8, 24, 32])], []),
    new("descend", 64, U64, [new(0, U64), new(8, U64), new(16, U64)],
    [
        F64(24, 0),
        new(GtF64, D: 32, A: 16, B: 24),
        new(Branch, D: 6, A: 32, B: 3),
        new(SubF64, D: 40, A: 16, B: 8),
        new(Call, D: 48, A: 0),
        new(Return, A: 48),
        new(Call, D: 48, A: 1),
        new(Return, A: 48)
    ], [new(1, [0, 8, 40]), new(2, [0])], [new(4, recursiveSite)]),
    new("bump", 16, U64, [new(0, U64)],
    [
        F64(~0, 10),
        F64(8, 2),
        new(Return, A: 8)
    ], [], [])
], 0);

var records = new List<RuntimeTimelineRecord>();
var profile = new RuntimeProfile(instructionSites: true);
var options = new ExecutionOptions { Profile = profile, Timeline = new(records.Add, "test") };
Require(HpirExecutor.Run(program) == Bits(23), "physical return without recording");
Require(HpirExecutor.Run(program, options) == Bits(23), "shared field mutation and pre-call snapshot");
Require(HpirExecutor.Run(program, options) == Bits(23), "repeat execution with the same image");

IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
RuntimeProfileSample descend = samples.Single(sample => sample.Name == "vm.function"
    && sample.Attributes["function.name"] == "descend");
Require(descend.Metrics["calls"] == 4 * (depth + 1), "recursive frame count across drains");
Require(descend.Metrics["returns"] == descend.Metrics["calls"], "completed fixed calls");
Require(descend.Metrics["call_physical_argument_bytes"] == 24 * descend.Metrics["calls"], "exact call argument transfer");
Require(descend.Metrics["stack_segment_allocations"] > 0, "physical segment growth");
Require(descend.Metrics["stack_segment_reentries"] > 0, "segment reentry after ordinary returns");
Require(records.Any(record => record.Name == "hpir.stack.segment"
    && record.Attributes.GetValueOrDefault("stack.segment.action") == "reenter"), "segment reentry timeline");
Require(records.Any(record => record.Name == "hpir.stack.segment"
    && record.Attributes.GetValueOrDefault("source.file") == recursiveSite.Source
    && record.Metrics.GetValueOrDefault("source.offset") == (ulong)recursiveSite.Start), "growth call-site context");
Require(samples.Any(sample => sample.Name == "vm.call"
    && sample.Attributes["function.name"] == "descend"
    && sample.Attributes.GetValueOrDefault("source.file") == recursiveSite.Source
    && sample.Metrics["hits"] == 4 * depth), "source-attributed recursive call site");
Require(profile.Drain().Count == 0, "no duplicate results after drain");

records.Clear();
using var cancel = new CancellationTokenSource();
var cancellation = new ExecutionOptions
{
    Timeline = new(record =>
    {
        records.Add(record);
        if (record.Name == "hpir.stack.segment" && record.Attributes.ContainsKey("caller.function.id"))
            cancel.Cancel();
    }, "test")
};
try { _ = HpirExecutor.Run(program, cancellation, cancel.Token); throw new Exception("Outstanding guest call ignored cancellation."); }
catch (OperationCanceledException error) when (error.CancellationToken == cancel.Token) { }
RuntimeTimelineRecord failure = records.Single(record => record.Name == "hpir.execute");
Require(failure.Outcome == "cancelled" && failure.Metrics["stack.frames_discarded"] > 1
    && failure.Attributes["failure.function.name"] == "descend", "nested cancellation context and stack discard");
Require(HpirExecutor.Run(program) == Bits(23), "independent stack after cancellation");

HpirProgram indirectResult = new(
[
    new("entry", 48, U64, [],
    [
        F64(0, 9),
        F64(8, 42),
        new(AddressOf, D: 16, A: 0),
        new(AddressOf, D: 24, A: 8),
        new(AddressOf, D: 32, A: 16),
        new(Call, D: ~16, A: 0),
        new(AddF64, D: 40, A: 0, B: 8),
        new(Return, A: 40)
    ], [new(1, [32, 24])], []),
    new("retarget", 24, U64, [new(0, U64), new(8, U64)],
    [
        new(CopyU64, D: ~0, A: 8),
        F64(16, 11),
        new(Return, A: 16)
    ], [], [])
], 0);
Require(HpirExecutor.Run(indirectResult) == Bits(53), "indirect result destination is the pre-call address");

HpirProgram unaligned = new(
[
    new("entry", 24, U16, [],
    [
        new(AddressOf, D: 8, A: 0),
        new(ConstU64, D: 16, Immediate: 3),
        new(AddPointer, D: 8, A: 8, B: 16),
        new(ConstU16, D: ~8, Immediate: 0xf010),
        new(CopyU16, D: 0, A: ~8),
        new(Return, A: 0)
    ], [], [])
], 0);
Require(HpirExecutor.Run(unaligned) == 0xf010, "unaligned narrow physical read, write and return");

HpirHeapChecks.Run();
HpirCollectionChecks.Run();
HpirFutureChecks.Run();
HpirProcessFutureChecks.Run();
HpirTextInputChecks.Run();
Console.WriteLine("HPIR physical stack, calls, aliasing, collection and cancellation passed.");

static HpirInstruction F64(int offset, double value) => new(ConstU64, D: offset, Immediate: BitConverter.DoubleToInt64Bits(value));
static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);
static void Require(bool condition, string detail)
{
    if (!condition) throw new Exception(detail);
}
