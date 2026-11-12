using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

ScalarWorkers.Run();
ArenaWorkers.Run();
VectorWorkers.Run();
foreach (string path in args.SkipWhile(arg => arg == "--current"))
    CheckCurrentArtifact(path);

static void CheckCurrentArtifact(string path)
{
    string full = Path.GetFullPath(path);
    var original = Artifact.Read(full, formBlocks: false);
    var image = original.Stage == ArtifactStage.Executable ? original
        : Binding.BindInstructions(original, new(Path.GetDirectoryName(full)!, Environment.CurrentDirectory));
    var targets = ExecutionTargets.For(image);
    List<IndexedMethodPlan> admitted = [];
    bool arena = false, vector = false;
    for (int function = 0; function < image.Functions.Count; function++)
    {
        var body = image.Functions[function];
        arena |= body.Instructions.Any(i => i is { Op: OpCode.NativeCall, Text: "arena.key" });
        vector |= body.Instructions.Any(i => i is { Op: OpCode.NativeCall, Text: "storage.vector_set" });
        if (IndexedMethodPreparation.MayPrepare(body, includeEffectFrontier: true)
            && targets.TryExecutionWorker(image, function, out var plan) && plan is not null) admitted.Add(plan);
    }
    if (arena)
    {
        var find = admitted.FirstOrDefault(plan => plan is { UsesArenaRows: true, WritesArenaRows: false, Exits.IsEmpty: true }
            && plan.Worker.Slots[plan.Worker.Result] == NativeWorkerType.Index
            && plan.Worker.Instructions.Any(op => op.Op == NativeWorkerOp.LoadNumber))
            ?? throw new InvalidOperationException("A current checked numeric arena executable must derive its complete key lookup.");
        Console.WriteLine($"PASS {CheckArenaLookups(find)} exact raw numeric lookup oracles over shuffled arena rows");
    }
    if (vector && !admitted.Any(plan => plan.WritesScalarStorage
        && plan.Worker.Instructions.Count(op => op.Op == NativeWorkerOp.StoreNumber) >= 2))
        throw new InvalidOperationException("A current checked numeric Heap executable must derive its complete scalar writer.");
    if (!arena && !vector) throw new InvalidOperationException("Expected a current checked arena or vector executable.");
    Console.WriteLine($"PASS source-free current {Path.GetFileName(full)}: {admitted.Count} typed workers");
}

static int CheckArenaLookups(IndexedMethodPlan plan)
{
    int count = 0;
    foreach (int seed in new[] { 17, 503 })
    {
        foreach (int size in new[] { 0, 1, 31, 62, 257 })
        {
            double[] keys = Enumerable.Range(0, size).Select(i => 3d * (i - size / 2)).ToArray();
            Check(seed, keys, [.. keys, .. keys.Select(Math.BitIncrement), double.NaN, double.PositiveInfinity]);
        }
        Check(seed, [double.NegativeInfinity, -5, -0d, 2, double.PositiveInfinity, double.NaN],
            [double.NegativeInfinity, -5, -0d, 0d, double.Epsilon, -1, 2, 3, double.PositiveInfinity, double.NaN,
                BitConverter.Int64BitsToDouble(unchecked((long)0xfff800000000002a))]);
    }
    return count;

    void Check(int seed, double[] keys, double[] probes)
    {
        var arena = new OrderedArena(keys, seed);
        byte[] rows = arena.Build(ArenaNumberRowLayout.Stride, ArenaNumberRowLayout.Left,
            ArenaNumberRowLayout.Right, ArenaNumberRowLayout.Key);
        foreach (double key in probes)
        {
            long[] arguments = plan.Inputs.Select(input => plan.Accesses[input.Access].Kind == IndexedMethodPlan.AccessKind.ArenaRoot
                ? arena.Root : input.Type == NativeWorkerType.Number ? BitConverter.DoubleToInt64Bits(key)
                : throw new InvalidOperationException("The numeric lookup exposes only its live root and Number key.")).ToArray();
            long selected = WorkerModel.Run(plan.Worker, rows, arguments);
            if (selected != arena.Expected(key))
                throw new InvalidOperationException($"Wrong raw row for {key:R}, {keys.Length} keys, seed {seed}.");
            count++;
        }
    }
}
