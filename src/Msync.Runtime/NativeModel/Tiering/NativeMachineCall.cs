using System.Collections;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeMachineParameter(int Offset, int Descriptor, NativeKind Kind, int Reference);
internal readonly record struct NativeMachineCapture(int Index, int Offset, int Descriptor, NativeKind Kind, bool Location);

internal sealed record NativeMachineLeaf(NativeMachineParameter[] Parameters, NativeMachineCapture[] Captures, int CaptureCount,
    int FrameBytes, int SlotCount, int GuestReferences,
    int ResultOffset, NativeKind ResultKind)
{
    internal int DataOffset => NativeTierLayout.HeaderBytes + SlotCount * 32;
    internal int InitOffset => DataOffset + FrameBytes;
    internal int EdgesOffset => InitOffset + FrameBytes;
    internal int WorkspaceBytes => (EdgesOffset + GuestReferences * sizeof(long) + 15) & ~15;

    internal static bool Candidate(NativeExecutionFunction function) =>
        function.IsPureSynchronous && function.Definition.IteratorCount == 0
        && function.Instructions.Length is > 0 and <= 96
        && !function.Instructions.Any(static instruction => instruction.Op == NativeOp.Call);

    internal static NativeMachineLeaf? Describe(NativeImage image, NativeExecutionFunction function,
        NativeFastOperation[] operations, NativeTierLayout layout)
    {
        if (!Candidate(function)) return null;
        FrameAllocation frame = image.Frame(function.Index);
        if (frame.Layout.RawSize > 1024) return null;
        int captureCount = function.Definition.CaptureCount;
        int parameterEnd = captureCount + function.Definition.ParameterCount;
        int returnSlot = -1;
        bool returns = false;
        NativeKind result = default;
        for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
        {
            NativeExecutionInstruction instruction = function.Instructions[pc];
            switch (operations[pc])
            {
                case NativeFastOperation.Bridge:
                case NativeFastOperation.GuestStore:
                case NativeFastOperation.GuestHasLayout:
                case NativeFastOperation.GuestCopy:
                case NativeFastOperation.GuestView:
                    return null;
                case NativeFastOperation.GuestLoad when instruction.A < captureCount || instruction.A >= parameterEnd:
                    return null;
                case NativeFastOperation.Jump when instruction.A <= pc:
                case NativeFastOperation.Branch when instruction.B <= pc || instruction.C <= pc:
                    return null;
                case NativeFastOperation.NumberReturn:
                case NativeFastOperation.BoolReturn:
                case NativeFastOperation.NullReturn:
                    if (returns) return null;
                    returns = true;
                    returnSlot = instruction.A;
                    result = operations[pc] == NativeFastOperation.NullReturn ? NativeKind.Null
                        : operations[pc] == NativeFastOperation.BoolReturn ? NativeKind.Bool : NativeKind.Number;
                    break;
            }
        }
        if (!returns || returnSlot >= 0 && !function.HasKind(returnSlot, result)) return null;
        List<NativeMachineCapture> captures = [];
        for (int index = 0; index < captureCount; index++)
        {
            if (layout.Scalars[index] < 0) continue;
            NativeCaptureMode mode = function.Definition.Signature.Captures[index].Mode;
            ref readonly NativeFrameSlot entry = ref frame.Slots[index];
            NativeKind kind = entry.Type.Kind;
            if (kind is not (NativeKind.Number or NativeKind.Bool) || !function.HasKind(index, kind)
                || (entry.Binding >= 0) != (mode == NativeCaptureMode.Location)) return null;
            captures.Add(new(index, entry.Offset.Bytes, entry.Binding < 0 ? -1 : layout.Scalars[index], kind,
                mode == NativeCaptureMode.Location));
        }
        NativeMachineParameter[] parameters = new NativeMachineParameter[function.Definition.ParameterCount];
        int guestReferences = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            int slot = captureCount + index;
            ref readonly NativeFrameSlot entry = ref frame.Slots[slot];
            NativeKind kind;
            if (function.HasKind(slot, NativeKind.Number)) kind = NativeKind.Number;
            else if (function.HasKind(slot, NativeKind.Bool)) kind = NativeKind.Bool;
            else if (entry.ExactAddress && function.HasKind(slot, NativeKind.Address))
            {
                kind = NativeKind.Address;
                guestReferences = Math.Max(guestReferences, entry.Offset.References + 1);
            }
            else return null;
            if (entry.Binding >= 0 && (!entry.Borrowable || entry.HeapLayout is not null || entry.Persistent)) return null;
            parameters[index] = new(entry.Offset.Bytes, entry.Binding < 0 ? -1 : layout.Scalars[slot], kind,
                entry.Offset.References);
        }
        if (layout.RefreshSlots.Any(slot => !function.HasKind(slot, NativeKind.Number)
                && !function.HasKind(slot, NativeKind.Bool) && !function.HasKind(slot, NativeKind.Null)
                && !(slot >= captureCount && slot < parameterEnd && function.HasKind(slot, NativeKind.Address))
            || slot >= parameterEnd && frame.Slots[slot].Binding >= 0))
            return null;
        // Pointer leaves can return to the original call on a missing field.
        if (guestReferences != 0 && (captures.Any(static capture => capture.Location)
            || operations.Contains(NativeFastOperation.IsNull))) return null;
        if (captures.Count != 0 && !InitializedAlongReachablePaths(function, operations, captureCount + parameters.Length))
            return null;
        return new(parameters, captures.ToArray(), captureCount, (frame.Layout.RawSize + 7) & ~7, layout.ScalarCount, guestReferences,
            returnSlot < 0 ? 0 : frame.Slots[returnSlot].Offset.Bytes, result);
    }

    // After native writes to captured locations, the call cannot restart from its entry.
    private static bool InitializedAlongReachablePaths(NativeExecutionFunction function, NativeFastOperation[] operations, int inputs)
    {
        BitArray?[] incoming = new BitArray?[operations.Length];
        incoming[0] = new(function.Definition.Slots.Count);
        for (int slot = 0; slot < inputs; slot++) incoming[0]![slot] = true;
        for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
        {
            if (incoming[pc] is not { } ready) continue;
            NativeExecutionInstruction instruction = function.Instructions[pc];
            NativeFastOperation op = operations[pc];
            int next = pc + instruction.Span;
            if (op is NativeFastOperation.NullReturn or NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn)
            {
                if (instruction.A >= 0 && !ready[instruction.A]) return false;
                continue;
            }
            if (op is NativeFastOperation.Jump or NativeFastOperation.Branch)
            {
                if (op == NativeFastOperation.Branch)
                {
                    if (!ready[instruction.A]) return false;
                    if (!Merge(instruction.B, ready) || !Merge(instruction.C, ready)) return false;
                }
                else if (!Merge(instruction.A, ready)) return false;
                continue;
            }
            if (op != NativeFastOperation.Nop)
            {
                if (op is not (NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant or NativeFastOperation.NullConstant)
                    && !ready[op.FirstOperand(function, pc)]) return false;
                if (op.TakesSecondOperand() && !ready[instruction.B]) return false;
                if (op.IsScalarSelect() && (!ready[instruction.B] || !ready[instruction.C])) return false;
                if (op is NativeFastOperation.NumberMove or NativeFastOperation.BoolMove) ready[instruction.A] = false;
                ready[instruction.Result] = true;
            }
            if (!Merge(next, ready)) return false;
        }
        return true;

        bool Merge(int pc, BitArray ready)
        {
            if ((uint)pc >= (uint)incoming.Length) return false;
            if (incoming[pc] is { } existing) existing.And(ready);
            else incoming[pc] = new(ready);
            return true;
        }
    }
}

internal readonly record struct NativeMachineCallableSource(int Pc, int Base, NativeOffset? Field, int InlineArgument = -1);

internal sealed record NativeMachineCall(int Target, NativeMachineLeaf Leaf, IReadOnlyList<int> Inputs, nint Address,
    NativeMachineCallableSource? Source);
