using Msync.Core;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private enum DispatchResume : byte
    {
        Condition, SourceCall, ResultAndTail, Result, Completion, QualifiedMember,
        DiscardedTransfer, TransferAdmission, Boolean, IteratorSource, IteratorNext, IteratorDispose,
    }

    // Only a genuinely incomplete operation moves an activation's registers
    // and stack into this continuation. The original operation is not replayed.
    private sealed class DispatchState<TFrame> where TFrame : struct, RuntimeProfile.IFrame
    {
        internal int Pc;
        internal Instruction Instruction = null!;
        internal TFrame Frame;
        internal FrameSlots Slots;
        internal OperandStack<TFrame> Stack;
        internal Instruction[]? Array;
        internal Dictionary<int, PlanSubmission>? Submissions;
        internal ExecutionTargets.NumericBackedges? NumericBackedges;
        internal ExecutionTargets.EffectTails? EffectTails;
        internal NumericLoopSites? NumericLoops;
        internal DispatchResume Resume;
        internal ValueTask<Value> Value;
        internal ValueTask<bool> Boolean;
        internal ValueTask Completion;
        internal ValueTask<Task<bool>> Admission;
        internal Value? Receiver;
        internal IAsyncEnumerator<Value>? Iterator;
        internal IteratorValue? Cursor;
        internal ValueTask<RangeValue> RangeSource;

        internal void ClearWait()
        {
            Value = default;
            Boolean = default;
            Completion = default;
            Admission = default;
            Receiver = null;
            Iterator = null;
            Cursor = null;
            RangeSource = default;
        }
    }

    private static DispatchState<TFrame> Pause<TFrame>(ref DispatchState<TFrame>? state, ref TFrame frame, DispatchResume resume)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        frame.DispatchPaused(state is null);
        var continuation = state ??= new();
        continuation.Resume = resume;
        return continuation;
    }

    private async ValueTask<Value> ResumeDispatchAsync<TFrame, TLayout>(Module image, int functionIndex,
        DispatchState<TFrame> suspended, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        DispatchState<TFrame>? state = suspended;
        while (true)
        {
            int pc = state!.Pc;
            Instruction instruction = state.Instruction;
            bool effectTail = false;
            try
            {
                switch (state.Resume)
                {
                    case DispatchResume.Condition:
                        if (AsBool(await state.Value) == (instruction.Op == OpCode.JumpIfTrue))
                            pc = instruction.A - 1;
                        break;
                    case DispatchResume.SourceCall:
                        {
                            Value result = await state.Value;
                            state.Stack.CompleteCall(instruction.A, result);
                            effectTail = true;
                            break;
                        }
                    case DispatchResume.ResultAndTail:
                    case DispatchResume.Result:
                        {
                            Value result = await state.Value;
                            state.Stack.Add(result);
                            effectTail = state.Resume == DispatchResume.ResultAndTail;
                            break;
                        }
                    case DispatchResume.Completion:
                        await state.Completion;
                        break;
                    case DispatchResume.QualifiedMember:
                        {
                            Value member = await state.Value;
                            state.Stack.Add(Member(state.Receiver!, Text(member)));
                            break;
                        }
                    case DispatchResume.DiscardedTransfer:
                        await state.Admission;
                        pc++;
                        break;
                    case DispatchResume.TransferAdmission:
                        {
                            var completion = await state.Admission;
                            bool succeeded = await state.Frame.Observe(completion, pc, instruction, "transfer_completion");
                            state.Stack.Add(succeeded ? Value.True : Value.False);
                            break;
                        }
                    case DispatchResume.Boolean:
                        {
                            bool succeeded = await state.Boolean;
                            state.Stack.Add(succeeded ? Value.True : Value.False);
                            break;
                        }
                    case DispatchResume.IteratorSource:
                        {
                            var cursor = state.Cursor!;
                            BindIterator(cursor, await state.RangeSource, cancellationToken);
                            if (cursor.Iterator is not { } iterator)
                            {
                                if (cursor.Numbers.TryNext(out double item)) state.Stack.AddNumber(item);
                                else pc = instruction.B - 1;
                                break;
                            }
                            state.Iterator = iterator;
                            state.Boolean = state.Frame.Observe(iterator.MoveNextAsync(), pc, instruction, "iterator_next");
                            goto case DispatchResume.IteratorNext;
                        }
                    case DispatchResume.IteratorNext:
                        if (await state.Boolean)
                        {
                            state.Stack.Add(state.Iterator!.Current);
                            break;
                        }
                        await state.Frame.Observe(state.Iterator!.DisposeAsync(), pc, instruction, "iterator_dispose");
                        iterators.Remove(state.Iterator);
                        pc = instruction.B - 1;
                        break;
                    case DispatchResume.IteratorDispose:
                        await state.Completion;
                        iterators.Remove(state.Iterator!);
                        pc = instruction.B - 1;
                        break;
                }
                if (effectTail && state.EffectTails is { } tails)
                    RunEffectTail(tails, image, functionIndex, image.Functions[functionIndex].Instructions,
                        ref state.Slots, ref state.NumericLoops, ref state.Stack, ref state.Frame, ref pc, cancellationToken);
            }
            catch (Exception error) when (IsDispatchError(error))
            {
                throw DispatchError(error, instruction);
            }

            state.ClearWait();
            var value = Dispatch<TFrame, TLayout>(image, functionIndex, null, pc + 1, cancellationToken,
                default, -1, null, null, -1, ref state);
            if (value is not null) return value;
        }
    }

    private static bool IsDispatchError(Exception error) =>
        error is MsyncException { Span: null } or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception;

    private static MsyncException DispatchError(Exception error, Instruction instruction) =>
        new(error is MsyncException ? error.Message : instruction.Op switch
        {
            OpCode.ProcessCall => "Cannot run the requested process.",
            OpCode.NativeCall => $"Native operation '{instruction.Text}' failed.",
            OpCode.Call => "The function call failed.",
            _ => "The operation failed."
        }, instruction.Span, error);
}
