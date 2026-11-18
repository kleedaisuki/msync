using System.Runtime.CompilerServices;
using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class ArgumentSourceChecks
{
    internal static void Run()
    {
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile("fn id(n: Number) { n }; id(1)", "argument-source.msync", 0));
        int function = Enumerable.Range(0, program.Functions.Count).First(index => program.Functions[index].ParameterCount == 1);
        NativeImage image = new(program, new());
        using NativeGuestHeap heap = new(rawEdges: IntPtr.Size == sizeof(ulong));
        using NativeMemory memory = new();
        NativeArguments[] variants = [Values(), Frame(image, function, memory), Storage(), Snapshot(program, memory, heap)];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (int index = 0; index < variants.Length; index++)
        {
            NativeArguments arguments = variants[index];
            if (arguments.Count != 1 || arguments.Read(0, memory).Value.AsNumber() != index + 11
                || arguments.Materialize(memory).Count != 1)
                throw new InvalidOperationException("An argument source lost its owner or count across GC");
            if (arguments.IsFrameView != (index == 1) || (arguments.Storage is not null) != (index == 2)
                || arguments.IsSnapshot != (index == 3))
                throw new InvalidOperationException("An argument descriptor exposed an inactive source");
        }
        NativeArguments empty = default;
        if (empty.Count != 0 || empty.Materialize(memory).Count != 0 || empty.IsFrameView || empty.Storage is not null)
            throw new InvalidOperationException("Default arguments are not empty");
        CheckPendingReuse(heap, memory);
        Console.WriteLine($"PASS argument sources, GC retention and empty state; descriptor bytes={Unsafe.SizeOf<NativeArguments>()}");
    }

    private static void CheckPendingReuse(NativeGuestHeap heap, NativeMemory memory)
    {
        const int count = 24;
        NativeType number = NativeType.Of(NativeKind.Number);
        NativeLayout element = new(number);
        NativeAllocation owner = heap.NewObject(NativeLayout.Repeat(number, count), -1, 1, 0, default);
        TaskCompletionSource<NativeValue>[] completions = new TaskCompletionSource<NativeValue>[count];
        for (int i = 0; i < count; i++)
        {
            TaskCompletionSource<NativeValue> pending = completions[i] = new();
            memory.Write(owner.Field(i), new(new NativeFuture(pending.Task, logicalType: Msync.Core.TypeRef.Number)));
        }
        for (int i = 0; i < count; i += 2) memory.Clear(owner.Field(i));
        for (int i = 1; i < count; i += 2)
        {
            completions[i].SetResult(NativeValue.FromNumber(i));
            NativeAddress alias = owner.Index(i, element).OffsetBy(default, number);
            if (memory.Read(alias).Value.AsNumber() != i)
                throw new InvalidOperationException("Pending cells lost their identities across resize and deletion.");
        }
        memory.ResetFrame(owner);
        if (memory.HasAnyPending || memory.HasPending(owner))
            throw new InvalidOperationException("Native owner retirement left pending slots active.");
        memory.Write(owner.Field(0), new(NativeValue.FromNumber(123)));
        if (memory.Read(owner.Field(0)).Value.AsNumber() != 123)
            throw new InvalidOperationException("Reused storage retained stale pending state.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeArguments Values() => new(new NativeDatum[] { new(NativeValue.FromNumber(11)) });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeArguments Frame(NativeImage image, int function, NativeMemory memory)
    {
        NativeFrame frame = new(image, function, null, [new NativeDatum(NativeValue.FromNumber(12))], memory);
        return new(frame, [image.Program.Functions[function].CaptureCount], [true], 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeArguments Storage()
    {
        NativeAllocation allocation = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        allocation.Field(0).Store(NativeValue.FromNumber(13));
        return new(allocation, [true], 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeArguments Snapshot(NativeProgram program, NativeMemory memory, NativeGuestHeap heap)
    {
        NativeFunctionCode[] functions = program.Functions.ToArray();
        NativeFunctionCode entry = functions[program.EntryPoint];
        NativeCallStorage call = entry.Storage!.Calls.Single(site => entry.Instructions[site.Instruction].Inputs!.Count == 1);
        int slot = entry.Instructions[call.Instruction].Inputs![0];
        functions[program.EntryPoint] = entry with
        {
            Storage = entry.Storage with { Calls = [new(call.Instruction, true, [false])] }
        };
        NativeImage image = new(program with { Functions = functions }, new());
        NativeFrame frame = new(image, program.EntryPoint, null, default(NativeArguments), memory, heap: heap);
        TaskCompletionSource<NativeValue> completion = new();
        NativeFuture future = new(completion.Task, logicalType: Msync.Core.TypeRef.Number);
        frame.SetDatum(slot, new(future));
        NativeArguments snapshot = frame.SnapshotArguments([slot], call.Instruction);
        frame.Set(slot, NativeValue.FromNumber(99));
        if (!ReferenceEquals(snapshot.Read(0, memory).Future, future))
            throw new InvalidOperationException("The snapshot lost or reread its unresolved argument.");
        completion.SetResult(NativeValue.FromNumber(14));
        if (!snapshot.IsSnapshot || snapshot.SnapshotExtent.Bytes != sizeof(double) || snapshot.Read(0, memory).Value.AsNumber() != 14)
            throw new InvalidOperationException("The snapshot did not resolve in its own typed packet.");
        return snapshot;
    }
}
