using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

string repository = Environment.CurrentDirectory;
while (!File.Exists(Path.Combine(repository, "Directory.Build.props")))
    repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Repository not found.");
string root = Path.Combine(repository, ".temp", "native-preparation-contracts", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    NativeProgram program = new(
        [new(NativeKind.Path, "./missing"), new(NativeKind.Path, "./out"), new(NativeKind.String, "last")],
        [new("aliased-transfer-result", [new(0), new(3), new(2), new(2), new(1), new(0), new(0)],
            [
                new(NativeOp.FrameAddress, 1, A: 0),
                new(NativeOp.Constant, 2, A: 0, Type: 2),
                new(NativeOp.Constant, 3, A: 1, Type: 2),
                new(NativeOp.Transfer, 0, A: 2, B: 3),
                new(NativeOp.Constant, 4, A: 2, Type: 1),
                new(NativeOp.Transfer, 5, A: 4, B: 3),
                new(NativeOp.Drop, A: 5),
                new(NativeOp.Load, 6, A: 1, Type: 0),
                new(NativeOp.Return, A: 6)
            ], new([], 0, []))],
        [new(TypeKind.Bool), new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Never, Pointee: 0)], [])
    { OptimizationLevel = 0 };
    NativeArtifact.Validate(program);
    NativeProgram prepared = NativePreparation.Prepare(program, new BindingContext(root, root, null));
    Require(prepared.ExecutionPlans is [{ EliminatedTransfers: 0, Transfers.Count: 2 }],
        "A transfer writing an aliased result location remains observed.");
    using MemoryStream encoded = new();
    NativeArtifact.Write(prepared, encoded);
    encoded.Position = 0;
    NativeProgram detached = NativeArtifact.Read(encoded);
    Require(ReferenceEquals(detached, NativePreparation.Prepare(detached)), "An executable is never replanned after decoding.");
    using StringWriter diagnostics = new();
    await using NativeInterpreter interpreter = new(detached, new ExecutionOptions { Error = diagnostics });
    NativeValue result = await interpreter.ExecuteAsync();
    Require(!result.AsBool(), "The alias observes the actual failed transfer Bool, not a synthesized True.");
    Require(interpreter.FailedTransfers == 1, "An observed failure contributes its real transfer failure.");
    Require(diagnostics.ToString().Contains("missing", StringComparison.Ordinal), "Concrete source context remains in diagnostics.");
    Require(File.ReadAllText(Path.Combine(root, "out")) == "last", "The later write still executes after the observed failure.");

    NativeProgram session = program with
    {
        PreserveLocals = true,
        Functions = [program.Functions[0] with
        {
            Slots = program.Functions[0].Slots.Select((slot, index) => index == 0
                ? slot with { Role = NativeSlotRole.Local } : slot).ToArray()
        }]
    };
    NativeArtifact.Validate(session);
    NativeProgram preparedSession = NativePreparation.Prepare(session, new BindingContext(root, root, null));
    using MemoryStream sessionWire = new();
    NativeArtifact.Write(preparedSession, sessionWire);
    sessionWire.Position = 0;
    using StringWriter sessionErrors = new();
    await using NativeInterpreter sessionInterpreter = new(NativeArtifact.Read(sessionWire), new(Error: sessionErrors));
    Require(!(await sessionInterpreter.ExecuteAsync()).AsBool() && sessionInterpreter.FailedTransfers == 1,
        "A session-capable contextual program prepares ephemerally, then executes with its real persistence owner.");
    Require(sessionErrors.ToString().Contains("missing", StringComparison.Ordinal),
        "Preparing session locals preserves the concrete transfer failure.");

    NativeProgram compute = new([new(NativeKind.Null)],
        [new("compute-only", [new(0)], [new(NativeOp.Constant, 0, A: 0, Type: 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
        [new(TypeKind.Null)], []);
    NativeProgram emptyPlan = NativePreparation.Prepare(compute, new BindingContext(root, root, null));
    using MemoryStream emptyWire = new();
    NativeArtifact.Write(emptyPlan, emptyWire);
    emptyWire.Position = 0;
    NativeProgram decodedEmpty = NativeArtifact.Read(emptyWire);
    Require(decodedEmpty.ExecutionPlans is null && ReferenceEquals(decodedEmpty, NativePreparation.Prepare(decodedEmpty)),
        "Empty plan encoding cannot turn an executable into a new preparation request.");

    using CancellationTokenSource cancellation = new();
    cancellation.Cancel();
    bool canceled = false;
    try { NativePreparation.Prepare(program, cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Require(canceled, "Cancellation applies before any preparation work.");
    await PreparationSemanticChecks.RunAsync(root);
    Console.WriteLine("PASS: aliased native transfer result, detached execution, diagnostics, cancellation and no replanning");
}
finally { Directory.Delete(root, recursive: true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
