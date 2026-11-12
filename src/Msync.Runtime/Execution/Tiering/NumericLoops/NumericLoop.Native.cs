using System.Diagnostics;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

internal enum NativeLoopObservation : byte { None, Counts, InstructionSites }

internal sealed partial class NumericLoop
{
    private const int NativeWarmup = 128;
    private const int NativeFuel = 4096;
    private static readonly bool nativeEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase)
        && NativeCodePage.IsSupported;
    internal static bool NativeEnabled => nativeEnabled;

    internal static bool CanRunTail(NativeLoopObservation observation) =>
        nativeEnabled && observation != NativeLoopObservation.InstructionSites;

    private NativeCodePage? native;
    private bool nativeCounted;
    private bool attemptedNative;

    internal bool TryRunTail<TFrame>(ref FrameSlots values, IReadOnlyList<Instruction> original, ref OperandStack<TFrame> stack,
        ref TFrame frame, CancellationToken cancellationToken, out int continuation)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        continuation = 0;
        if (attemptedNative && native is null) return false;
        Span<double> registers = stackalloc double[registerCount];
        if (!Prepare(ref values, ref stack, registers, readCells: true)) return false;
        bool counted = frame.NativeLoopObservation == NativeLoopObservation.Counts;
        var version = native ?? CompileNative(counted, ref frame);
        if (version is null || nativeCounted != counted) return false;
        try
        {
            continuation = RunNative(version, registers, original, counted, ref frame, cancellationToken, tail: true);
            RestoreStack(continuation, registers[StackBase..], ref stack);
            return true;
        }
        finally { CommitLocals(ref values, registers, ref frame); }
    }

    private NativeCodePage? CompileNative<TFrame>(bool counted, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        attemptedNative = true;
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        // A64 counted LDR/STR uses a scaled unsigned imm12 per operation.
        if (!CanEmitNative() || arm64 && counted && code.Length > 4096) return null;
        long started = counted ? Stopwatch.GetTimestamp() : 0;
        byte[] bytes = arm64
            ? new Arm64Emitter(this, counted).Emit()
            : new X64Emitter(this, counted, OperatingSystem.IsWindows()).Emit();
        native = NativeCodePage.Create(bytes);
        if (native is null) return null;
        nativeCounted = counted;
        if (counted) frame.JitCompiled(ElapsedNanoseconds(started), bytes.Length);
        return native;
    }

    private unsafe int RunNative<TFrame>(NativeCodePage page, Span<double> registers,
        IReadOnlyList<Instruction> original, bool counted, ref TFrame frame, CancellationToken cancellationToken,
        int resume = 0, bool tail = false)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        Span<ulong> counts = counted ? stackalloc ulong[code.Length] : [];
        counts.Clear();
        long started = counted ? Stopwatch.GetTimestamp() : 0;
        int yields = 0;
        try
        {
            fixed (double* values = registers)
            fixed (ulong* counters = counts)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int result = page.Run(values, NativeFuel, counters, resume);
                    if (result < 0) return ~result;
                    resume = result;
                    yields++;
                }
            }
        }
        finally
        {
            if (counted)
            {
                frame.JitExecuted(ElapsedNanoseconds(started), yields, tail);
                long total = 0;
                for (int i = 0; i < counts.Length; i++)
                {
                    long hits = (long)counts[i];
                    if (hits == 0) continue;
                    total += hits;
                    var operation = code[i];
                    for (int pc = operation.FirstPc; pc <= operation.LastPc; pc++)
                        frame.AddNativeInstruction(original[pc].Op, hits);
                }
                if (!tail) frame.AddNativeOperations(total);
            }
        }
    }

    private static long ElapsedNanoseconds(long timestamp) => Stopwatch.GetElapsedTime(timestamp).Ticks * 100;
}
