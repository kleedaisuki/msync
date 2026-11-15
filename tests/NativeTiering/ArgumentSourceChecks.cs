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
        NativeMemory memory = new();
        NativeArguments[] variants = [Values(), Frame(image, function, memory), Storage(), Snapshot()];
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
        Console.WriteLine($"PASS argument sources, GC retention and empty state; descriptor bytes={Unsafe.SizeOf<NativeArguments>()}");
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
    private static NativeArguments Snapshot() => new(
        [new NativeDatum(NativeValue.FromNumber(14)), new NativeDatum(NativeValue.FromNumber(99))], 1, false);
}
