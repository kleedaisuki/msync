using System.Diagnostics;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

internal static partial class NativeWorker
{
    internal static byte[]? Emit(NativeWorkerPlan plan, bool counted, Architecture architecture, bool windows) => architecture switch
    {
        Architecture.X64 => new X64Emitter(plan, counted, windows).Emit(),
        Architecture.Arm64 when !windows => new Arm64Emitter(plan, counted).Emit(),
        _ => null
    };

    internal static NativeCodePage? Compile(NativeWorkerPlan plan, bool counted, out int byteCount)
    {
        byteCount = 0;
        if (!NumericLoop.NativeEnabled) return null;
        byte[]? bytes = Emit(plan, counted, RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows());
        if (bytes is null || NativeCodePage.Create(bytes) is not { } page) return null;
        byteCount = bytes.Length;
        return page;
    }

    internal static int[] ResumeTargets(NativeWorkerPlan plan)
    {
        HashSet<int> targets = [];
        for (int pc = 0; pc < plan.Instructions.Length; pc++)
        {
            var op = plan.Instructions[pc];
            if (op.Data <= pc && op.Op is NativeWorkerOp.Jump or NativeWorkerOp.BranchIfFalse or NativeWorkerOp.BranchIfTrue)
                targets.Add(op.Data);
        }
        return [.. targets.Order()];
    }

    internal sealed class Version(NativeWorkerPlan plan, NativeCodePage page, bool counted, bool[]? physicalOperations)
    {
        private const int Fuel = 1024;
        private const int StackWords = 256;

        internal ulong RunWritable(Span<byte> rows, ReadOnlySpan<ulong> arguments, CancellationToken cancellationToken,
            RuntimeProfile? profile, Module image, int target, IndexedMethodPlan prepared, Span<ulong> outputs) =>
            Run(rows, arguments, cancellationToken, profile, image, target, prepared, outputs);

        internal unsafe ulong Run(ReadOnlySpan<byte> rows, ReadOnlySpan<ulong> arguments,
            CancellationToken cancellationToken, RuntimeProfile? profile, Module image, int target,
            IndexedMethodPlan prepared, Span<ulong> outputs)
        {
            int size = plan.Slots.Length + (counted ? plan.Instructions.Length : 0);
            Span<ulong> memory = size <= StackWords ? stackalloc ulong[size] : new ulong[size];
            for (int i = 0; i < plan.Parameters.Length; i++)
                memory[plan.Parameters[i]] = arguments[i];
            Span<ulong> counts = memory[plan.Slots.Length..];
            counts.Clear();

            long started = counted ? Stopwatch.GetTimestamp() : 0;
            int yields = 0;
            int resume = 0;
            try
            {
                fixed (byte* storage = rows)
                fixed (ulong* slots = memory)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        resume = page.RunWorker(storage, slots, resume, Fuel);
                        if (resume < 0) break;
                        yields++;
                    }
                }
                Debug.Assert(resume == -1);
                ulong result = memory[plan.Result];
                if (!outputs.IsEmpty)
                {
                    if (result == 0)
                    {
                        if (!prepared.ReturnsNull) outputs[0] = memory[prepared.GuestResult];
                    }
                    else
                    {
                        var live = prepared.Exits[(int)result - 1].Locals;
                        for (int i = 0; i < live.Length; i++)
                            if (live[i].Access < 0) outputs[i] = memory[live[i].Register];
                    }
                }
                return result;
            }
            finally
            {
                if (counted) profile!.NativeWorker(image, target, plan,
                    counts, physicalOperations, Stopwatch.GetElapsedTime(started).Ticks * 100, yields);
            }
        }
    }
}
