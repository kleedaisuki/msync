using Msync.Core;

namespace Msync.Runtime;

internal sealed partial class NumericLoop
{
    private enum Kind : byte { Nop, Move, Negate, Binary, Branch, Jump }

    private readonly record struct Operation(Kind Kind, OpCode Code, int Left, int Right, int Output, int Target,
        int FirstPc, int LastPc);

    private readonly int[] locals;
    private readonly double[] constants;
    private readonly Operation[] code;
    private readonly int registerCount;
    private readonly ulong written;
    private readonly int prefix;
    private readonly int entryDepth;
    private readonly ulong entryNumbers;
    private readonly Dictionary<int, int>? exitDepths;
    private int StackBase => locals.Length + constants.Length;

    private NumericLoop(int[] locals, double[] constants, Operation[] code, int registerCount, ulong written,
        int prefix, int entryDepth, ulong entryNumbers, Dictionary<int, int> exits)
    {
        this.locals = locals;
        this.constants = constants;
        this.code = code;
        this.registerCount = registerCount;
        this.written = written;
        this.prefix = prefix;
        this.entryDepth = entryDepth;
        this.entryNumbers = entryNumbers;
        exitDepths = exits.Values.Any(static depth => depth != 0) ? exits : null;
    }

    public bool TryRun<TFrame>(ref FrameSlots values, IReadOnlyList<Instruction> original,
        ref OperandStack<TFrame> stack, ref TFrame frame,
        CancellationToken cancellationToken, out int continuation) where TFrame : struct, RuntimeProfile.IFrame
    {
        continuation = 0;
        Span<double> registers = stackalloc double[registerCount];
        if (!Prepare(ref values, ref stack, registers, readCells: false)) return false;
        var operands = registers[StackBase..];

        frame.EnterNumericLoop();
        try
        {
            NativeLoopObservation observation = frame.NativeLoopObservation;
            bool counted = observation == NativeLoopObservation.Counts;
            bool permitNative = nativeEnabled && observation != NativeLoopObservation.InstructionSites;
            if (permitNative && native is { } existing && nativeCounted == counted)
            {
                continuation = RunNative(existing, registers, original, counted, ref frame, cancellationToken);
                RestoreStack(continuation, operands, ref stack);
                return true;
            }

            int budget = permitNative && !attemptedNative ? NativeWarmup : -1;
            int pc = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                int step = pc++;
                ref readonly var op = ref code[step];
                if (typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame))
                    for (int source = op.FirstPc; source <= op.LastPc; source++)
                        frame.Visit(source, original[source].Op);
                frame.NumericOperation();
                pc = Execute(op, registers, pc);
                if (budget > 0 && (uint)pc <= (uint)step && --budget == 0
                    && CompileNative(counted, ref frame) is { } compiled)
                {
                    continuation = RunNative(compiled, registers, original, counted, ref frame, cancellationToken, pc);
                    RestoreStack(continuation, operands, ref stack);
                    return true;
                }
            } while (pc >= 0);
            continuation = ~pc;
            RestoreStack(continuation, operands, ref stack);
            return true;
        }
        finally { CommitLocals(ref values, registers, ref frame); }
    }

    private bool Prepare<TFrame>(ref FrameSlots values, ref OperandStack<TFrame> stack, Span<double> registers, bool readCells)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (stack.Count != prefix + entryDepth) return false;
        for (int i = 0; i < locals.Length; i++)
        {
            // A straight-line suffix has no host effects. If a read Cell
            // is aliased by a written slot, that slot's guard rejects it.
            if (!values.TryNumber(locals[i], readCells && (written & (1UL << i)) == 0, out registers[i]))
                return false;
        }
        constants.AsSpan().CopyTo(registers[locals.Length..]);
        var operands = registers[StackBase..];
        operands.Clear();
        for (ulong remaining = entryNumbers; remaining != 0; remaining &= remaining - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(remaining);
            if (!stack.TryPeekNumber(entryDepth - 1 - i, out operands[i])) return false;
        }
        return true;
    }

    private void CommitLocals<TFrame>(ref FrameSlots values, ReadOnlySpan<double> registers, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        // A cancelled session may retain the locations written before cancellation.
        for (ulong remaining = written; remaining != 0; remaining &= remaining - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(remaining);
            int slot = locals[i];
            if (values.TryNumber(slot, readCell: false, out double previous)
                && BitConverter.DoubleToInt64Bits(previous) == BitConverter.DoubleToInt64Bits(registers[i])) continue;
            values.CommitNumber(slot, registers[i], ref frame);
        }
    }

    private void RestoreStack<TFrame>(int continuation, ReadOnlySpan<double> operands, ref OperandStack<TFrame> stack)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        stack.Discard(entryDepth);
        int depth = exitDepths is null ? 0 : exitDepths[continuation];
        foreach (double value in operands[..depth]) stack.AddNumber(value);
    }

    private static int Execute(in Operation op, Span<double> registers, int next)
    {
        switch (op.Kind)
        {
            case Kind.Move: registers[op.Output] = registers[op.Left]; break;
            case Kind.Negate: registers[op.Output] = -registers[op.Left]; break;
            case Kind.Binary: registers[op.Output] = NumberArithmetic.Apply(registers[op.Left], registers[op.Right], op.Code); break;
            case Kind.Branch:
                if (NumberArithmetic.Test(registers[op.Left], registers[op.Right], op.Code) == (op.Output != 0))
                    return op.Target;
                break;
            case Kind.Jump: return op.Target;
        }
        return next;
    }
}
