using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;

int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

Module constant = new([new PathValue("./input", "captured-cwd", LocalSpelling: "native-input")],
    [new("main", 0, 0, [new(OpCode.Constant), new(OpCode.Return)], TypeRef.Path)]);
using MemoryStream bytes = new();
Artifact.Write(constant, bytes);
byte[] image = bytes.ToArray();
Require(BitConverter.ToUInt16(image, 8) == 5, "Writer did not publish current format 5");
bytes.Position = 0;
Module loaded = Artifact.Read(bytes);
Require(loaded.Constants[0].Equals(constant.Constants[0]), "Roundtrip changed captured path projection");
Require((await Interpreter.ExecuteAsync(loaded)) is PathValue { Raw: "./input", LocalSpelling: "native-input" }, "Current artifact did not execute");
foreach (ushort old in new ushort[] { 1, 2, 3, 4 })
{
    using MemoryStream obsolete = new();
    using (BinaryWriter writer = new(obsolete, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        writer.Write("MSYNCBC\0"u8);
        writer.Write(old);
    }
    obsolete.Position = 0;
    try { Artifact.Read(obsolete); throw new InvalidOperationException($"Accepted obsolete format {old}"); }
    catch (MsyncException error)
    {
        Require(error.Message.Contains($"format {old}", StringComparison.Ordinal), "Lost original format diagnostic");
        Require(error.Message.Contains("Recompile", StringComparison.Ordinal), "Missing recompilation guidance");
    }
}
using MemoryStream invalid = new();
using (BinaryWriter writer = new(invalid, System.Text.Encoding.UTF8, leaveOpen: true))
{
    writer.Write("MSYNCBC\0"u8); writer.Write(Artifact.FormatVersion);
    writer.Write(Artifact.LanguageVersion); writer.Write((byte)0);
}
invalid.Position = 0;
try { Artifact.Read(invalid); throw new InvalidOperationException("Accepted obsolete execution semantics"); }
catch (MsyncException error)
{
    Require(error.Message.Contains("execution semantics", StringComparison.Ordinal), "Invalid semantics failed in unrelated decoder logic");
}
PlanResource firstResource = new("local", "/prepared/first.txt");
PlanResource secondResource = new("local", "/prepared/second.txt");
ExecutionPlan preparedPlan = new(0, 10, 42,
[
    new(0, new StringValue("Klee\n"), new PathValue("first.txt", "/prepared"),
        PlannedTransferKind.WriteText, [], [firstResource], [], new("plan.msync", 7, 21)),
    new(1, new PathValue("first.txt", "/prepared"), new PathValue("second.txt", "/prepared"),
        PlannedTransferKind.CopyFile, [firstResource], [secondResource], [0], new("plan.msync", 29, 24))
], [new([0], "local"), new([1], "local")], 3, ResultTransfers: [1]);
Module planModule = new([], [new("prepared", 0, 0,
    [new(OpCode.ExecutePlan), new(OpCode.PlanResult), new(OpCode.Return)], TypeRef.Bool)],
    Stage: ArtifactStage.Executable, Context: new("/prepared", "/home/klee"))
{ ExecutionPlans = [preparedPlan] };
using MemoryStream planBytes = new();
Artifact.Write(planModule, planBytes);
planBytes.Position = 0;
Module planRoundtrip = Artifact.Read(planBytes);
ExecutionPlan restoredPlan = planRoundtrip.ExecutionPlans!.Single();
Require(restoredPlan.Function == 0 && restoredPlan.Start == 10 && restoredPlan.End == 42,
    "Plan region provenance was confused with rewritten bytecode offsets");
Require(restoredPlan.EliminatedTransfers == 3 && restoredPlan.ResultTransfers!.SequenceEqual([1]),
    "Plan liveness and observed result did not roundtrip");
Require(restoredPlan.Transfers[0].Source is StringValue { Data: "Klee\n" }
    && restoredPlan.Transfers[0].Reads.Count == 0 && restoredPlan.Transfers[0].Writes.SequenceEqual([firstResource]),
    "Literal write payload or resource footprint changed");
Require(restoredPlan.Transfers[1].Kind == PlannedTransferKind.CopyFile
    && restoredPlan.Transfers[1].Reads.SequenceEqual([firstResource])
    && restoredPlan.Transfers[1].Predecessors.SequenceEqual([0])
    && restoredPlan.Transfers[1].Span == new SourceSpan("plan.msync", 29, 24),
    "Concrete transfer kind, dependencies or source provenance changed");
Require(restoredPlan.Batches.Count == 2 && restoredPlan.Batches[1].Transfers.SequenceEqual([1]),
    "Execution batches did not roundtrip");
using MemoryStream modeBytes = new();
Artifact.Write(planModule with { ExecutionPlans = [preparedPlan with
    { Transfers = [preparedPlan.Transfers[0], preparedPlan.Transfers[1] with { Mode = TransferDestinationMode.Merge }] }] }, modeBytes);
modeBytes.Position = 0;
Require(Artifact.Read(modeBytes).ExecutionPlans![0].Transfers[1].Mode == TransferDestinationMode.Merge,
    "A concrete container leaf lost its merge publication semantics");
IReadOnlyList<PlanResource> multipleReads = [firstResource with { Subtree = true }, secondResource];
IReadOnlyList<PlanResource> multipleWrites =
    [new("local", "/prepared/tree", Subtree: true), new("local", "/mounted/target", Subtree: true)];
RangeValue cohortSources = new(new Value[] { new PathValue("first.txt", "/prepared"),
    new PathValue("second.txt", "/prepared") }, TypeRef.Path);
ExecutionPlan cohortPlan = new(0, 0, 9,
    [new(0, cohortSources, new PathValue("tree", "/prepared"), PlannedTransferKind.GenericCopy,
        multipleReads, multipleWrites, [], new("cohort.msync", 3, 8), TransferDestinationMode.Replace)],
    [new([0], "local")], 0, [0]);
using MemoryStream cohortBytes = new();
Artifact.Write(planModule with { ExecutionPlans = [cohortPlan] }, cohortBytes);
cohortBytes.Position = 0;
PlannedTransfer restoredCohort = Artifact.Read(cohortBytes).ExecutionPlans![0].Transfers[0];
Require(restoredCohort.Source is RangeValue { SnapshotValues.Count: 2 }
    && restoredCohort.Reads.SequenceEqual(multipleReads) && restoredCohort.Writes.SequenceEqual(multipleWrites)
    && restoredCohort.Mode == TransferDestinationMode.Replace,
    "A replacement cohort lost its finite sources or aliased read/write footprints");
ExecutionPlan ensurePlan = cohortPlan with
{
    Transfers = [new(0, Value.Null, new PathValue("tree", "/prepared"), PlannedTransferKind.EnsureDirectory,
        [], multipleWrites, [], Mode: TransferDestinationMode.Replace)]
};
using MemoryStream ensureBytes = new();
Artifact.Write(planModule with { ExecutionPlans = [ensurePlan] }, ensureBytes);
ensureBytes.Position = 0;
Require(Artifact.Read(ensureBytes).ExecutionPlans![0].Transfers[0] is
    { Kind: PlannedTransferKind.EnsureDirectory, Source: NullValue },
    "An unconditional directory operation acquired a synthetic source pathname");
try
{
    Artifact.Validate(planModule with { ExecutionPlans = [ensurePlan with
    { Transfers = [ensurePlan.Transfers[0] with { Kind = PlannedTransferKind.CopyFile }] }] });
    throw new InvalidOperationException("A file copy accepted the source-free directory operation's Null input");
}
catch (MsyncException error)
{
    Require(error.Message.Contains("source value", StringComparison.Ordinal), "Source-kind mismatch lost its structural diagnostic");
}
using MemoryStream emptyBytes = new();
Artifact.Write(planModule with { ExecutionPlans = [preparedPlan with
    { Transfers = [], Batches = [], ResultTransfers = [] }] }, emptyBytes);
emptyBytes.Position = 0;
Require(Artifact.Read(emptyBytes).ExecutionPlans![0].ResultTransfers is { Count: 0 },
    "An observed empty cohort lost its true Bool result by becoming an unobserved plan");
Module selectedMembers = new([new RangeValue(
    new Value[] { new PathValue("first.pick", "/prepared"), new PathValue("second.pick", "/prepared") }, TypeRef.Path)],
    [new("members", 0, 0, [new(OpCode.Constant), new(OpCode.Return)], new(TypeKind.Range, TypeRef.Path))]);
using MemoryStream memberBytes = new();
Artifact.Write(selectedMembers, memberBytes);
memberBytes.Position = 0;
RangeValue members = (RangeValue)Artifact.Read(memberBytes).Constants[0];
Require(members.ItemType == TypeRef.Path && members.SnapshotValues is { Count: 2 }
    && members.SnapshotValues[1] is PathValue { Raw: "second.pick", WorkingDirectory: "/prepared" },
    "Expanded Range members did not remain a concrete source-free snapshot");
static async IAsyncEnumerable<Value> LazyMembers()
{
    yield return new PathValue("not-a-frozen-selection");
    await Task.CompletedTask;
}
try
{
    RangeValue lazy = new(LazyMembers(), TypeRef.Path) { RetainedValues = [new PathValue("captured-input")] };
    using MemoryStream lazyBytes = new();
    Artifact.Write(selectedMembers with { Constants = [lazy] }, lazyBytes);
    throw new InvalidOperationException("Captured lazy inputs were serialized as expanded outputs");
}
catch (MsyncException error)
{
    Require(error.Message.Contains("snapshots", StringComparison.Ordinal), "Lazy Range lost the snapshot-specific diagnostic");
}
try
{
    Artifact.Validate(planModule with { ExecutionPlans = [preparedPlan with
    { Transfers = [preparedPlan.Transfers[0] with { Predecessors = [0] }, preparedPlan.Transfers[1]] }] });
    throw new InvalidOperationException("Accepted a cyclic serialized execution graph");
}
catch (MsyncException error)
{
    Require(error.Message.Contains("predecessors", StringComparison.Ordinal), "Invalid graph lost its structural diagnostic");
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "artifact-current-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string source = Path.Combine(root, "input");
    string output = Path.Combine(root, "output");
    await File.WriteAllTextAsync(source, "current artifact transfer");
    Module transfer = new([new PathValue(source), new PathValue(output)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Return)], TypeRef.Bool)]);
    Module executable = Binding.Bind(transfer, new(root, root));
    using MemoryStream bound = new();
    Artifact.Write(executable, bound); bound.Position = 0;
    Require(await Interpreter.ExecuteAsync(Artifact.Read(bound)) is BoolValue { Data: true }, "Executable transfer failed");
    Require(await File.ReadAllTextAsync(output) == "current artifact transfer", "Executable transfer changed bytes");
    File.Delete(source);
    bound.Position = 0;
    using StringWriter errors = new();
    Interpreter missing = new(Artifact.Read(bound), new(Error: errors));
    Require(await missing.ExecuteAsync() is BoolValue { Data: false }, "Missing selected input did not return false");
    Require(missing.FailedTransfers == 1 && errors.ToString().Contains("input", StringComparison.Ordinal), "Selected failure lost accounting or the real source path");
    Require(await File.ReadAllTextAsync(output) == "current artifact transfer", "Failed retransmission changed completed destination bytes");
}
finally { Directory.Delete(root, recursive: true); }
Module singleton = new([new NumberValue(0), new NumberValue(1)],
    [new("main", 0, 1,
    [
        new(OpCode.GetSingleton, 1), new(OpCode.Dup), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 8),
        new(OpCode.Pop), new(OpCode.Constant, 0), new(OpCode.MakeScope, 1, Text: "count"), new(OpCode.SetSingleton, 1),
        new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "count"),
        new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.SetMember, Text: "count"), new(OpCode.Pop),
        new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "count"), new(OpCode.Return)
    ], TypeRef.Number)]);
Module sharedPlan = Binding.Bind(singleton, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
Artifact.Validate(sharedPlan);
Interpreter firstRun = new(sharedPlan);
Require(await firstRun.ExecuteAsync() is NumberValue { Data: 1 }, "First executable singleton initialization failed");
Require(await firstRun.ExecuteAsync() is NumberValue { Data: 2 }, "A cached plan skipped selected mutable operations");
Require(await Interpreter.ExecuteAsync(sharedPlan) is NumberValue { Data: 1 }, "Cached executable metadata leaked singleton state into another interpreter");
Console.WriteLine($"PASS: {assertions} current artifact and actual transfer contracts");
