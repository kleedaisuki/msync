using System.Collections;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeMachineParameter(int Offset, int Descriptor, NativeKind Kind, int Reference, bool Omitted);
internal readonly record struct NativeMachineCapture(int Index, int Offset, int Descriptor, NativeKind Kind, bool Location);
internal readonly record struct NativeMachineReturnWindow(int Parameter, int CopyPc);

internal sealed record NativeMachineLeaf(NativeMachineParameter[] Parameters, NativeMachineCapture[] Captures, int CaptureCount,
    int FrameBytes, int SlotCount, int GuestReferences,
    int ResultOffset, NativeKind ResultKind, NativeMachineReturnWindow? ReturnWindow)
{
    internal int DataOffset => NativeTierLayout.HeaderBytes + SlotCount * 32;
    internal int InitOffset => DataOffset + FrameBytes;
    internal int EdgesOffset => InitOffset + FrameBytes;
    internal int WorkspaceBytes => (EdgesOffset + GuestReferences * sizeof(long) + 15) & ~15;

    internal static bool Candidate(NativeExecutionFunction function) =>
        function.IsPureSynchronous && function.Definition.IteratorCount == 0
        && function.Instructions.Length is > 0 and <= 96
        && !function.Instructions.Any(static instruction => instruction.Op == NativeOp.Call);

    internal static bool CanReplayBridge(NativeExecutionFunction function, int pc)
    {
        NativeExecutionInstruction instruction = function.Instructions[pc];
        return instruction.Op == NativeOp.Unreachable || instruction.Op == NativeOp.Copy
            && function.RawWritable[instruction.Result] && function.FrameSlot(instruction.Result).Binding < 0;
    }

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
        bool referenceLocals = false;
        bool replay = false, backward = false;
        bool returnsAddress = image.Type(function.Definition.Signature.Result) is
            { IsNever: false, IsUnion: false, Kind: NativeKind.Address };
        NativeKind result = default;
        for (int pc = 0, previous = -1; pc < operations.Length; previous = pc, pc += function.Instructions[pc].Span)
        {
            NativeExecutionInstruction instruction = function.Instructions[pc];
            switch (operations[pc])
            {
                case NativeFastOperation.Bridge:
                    if (!returnsAddress) return null;
                    if (!CanReplayBridge(function, pc)
                        && (previous < 0 || layout.LayoutBranches?.Contains(previous) != true)) return null;
                    replay = true;
                    break;
                case NativeFastOperation.GuestStore:
                case NativeFastOperation.GuestReferenceStore:
                case NativeFastOperation.GuestView:
                case NativeFastOperation.GuestComposeFunction:
                case NativeFastOperation.GuestNullConstant:
                    return null;
                case NativeFastOperation.GuestHasLayout:
                case NativeFastOperation.GuestCopy:
                case NativeFastOperation.GuestReferenceLoad:
                    referenceLocals = true;
                    break;
                case NativeFastOperation.GuestLoad when instruction.A < captureCount || instruction.A >= parameterEnd:
                    return null;
                case NativeFastOperation.Jump when instruction.A <= pc:
                case NativeFastOperation.Branch when instruction.B <= pc || instruction.C <= pc:
                    backward = true;
                    break;
                case NativeFastOperation.NumberReturn:
                case NativeFastOperation.BoolReturn:
                case NativeFastOperation.NullReturn:
                case NativeFastOperation.AddressReturn:
                    if (returns) return null;
                    returns = true;
                    returnSlot = instruction.A;
                    result = operations[pc] switch
                    {
                        NativeFastOperation.NullReturn => NativeKind.Null,
                        NativeFastOperation.BoolReturn => NativeKind.Bool,
                        NativeFastOperation.AddressReturn => NativeKind.Address,
                        _ => NativeKind.Number
                    };
                    break;
            }
        }
        if (!returns || returnSlot >= 0 && !function.HasKind(returnSlot, result)) return null;
        if (backward && ((!returnsAddress && captureCount != 0)
            || new NativeControlFlow(function.Metadata).Cyclic.Contains(true))) return null;
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
                guestReferences = Math.Max(guestReferences, entry.Offset.References + 2);
            }
            else return null;
            if (entry.Binding >= 0 && (!entry.Borrowable || entry.HeapLayout is not null || entry.Persistent)) return null;
            parameters[index] = new(entry.Offset.Bytes, entry.Binding < 0 ? -1 : layout.Scalars[slot], kind,
                entry.Offset.References, index < 64 && (frame.UnreadParameters & (1UL << index)) != 0);
        }
        if (referenceLocals || result == NativeKind.Address) guestReferences = frame.Layout.ReferenceCount;
        if (layout.RefreshSlots.Any(slot => !function.HasKind(slot, NativeKind.Number)
                && !function.HasKind(slot, NativeKind.Bool) && !function.HasKind(slot, NativeKind.Null)
                && !(captureCount == 0 && frame.Slots[slot].Binding < 0 && NullableScalar(frame.Slots[slot].Type))
                && !(guestReferences != 0 && frame.Slots[slot].Binding < 0
                     && (frame.Slots[slot].Type.Kinds & ~((1UL << (int)NativeKind.Address) | (1UL << (int)NativeKind.Null))) == 0)
            || slot >= parameterEnd && frame.Slots[slot].Binding >= 0))
            return null;
        // Pointer leaves can return to the original call on a missing field.
        if (guestReferences != 0 && captures.Any(static capture => capture.Location)) return null;
        if (replay && (captureCount != 0 || guestReferences == 0)) return null;
        if (captures.Count != 0 && !InitializedAlongReachablePaths(function, operations, captureCount + parameters.Length))
            return null;
        return new(parameters, captures.ToArray(), captureCount, (frame.Layout.RawSize + 7) & ~7, layout.ScalarCount, guestReferences,
            returnSlot < 0 ? 0 : frame.Slots[returnSlot].Offset.Bytes, result,
            captureCount == 0 && result == NativeKind.Address ? Window(function, operations) : null);
    }

    private static bool NullableScalar(NativeType type) => type.Kinds is
        ((1UL << (int)NativeKind.Number) | (1UL << (int)NativeKind.Null)) or
        ((1UL << (int)NativeKind.Bool) | (1UL << (int)NativeKind.Null));

    private static NativeMachineReturnWindow? Window(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        int parameter = -1, copyPc = -1;
        bool returned = false;
        for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
        {
            NativeFastOperation op = operations[pc];
            if (op == NativeFastOperation.Nop) continue;
            NativeExecutionInstruction instruction = function.Instructions[pc];
            if (!returned && op == NativeFastOperation.GuestCopy && copyPc < 0)
            {
                parameter = instruction.A;
                if (!function.HasKind(parameter, NativeKind.Address)
                    || !function.HasKind(instruction.Result, NativeKind.Address)) return null;
                copyPc = pc;
                continue;
            }
            if (returned || op != NativeFastOperation.AddressReturn) return null;
            if (copyPc < 0) parameter = instruction.A;
            else if (instruction.A != function.Instructions[copyPc].Result) return null;
            returned = true;
        }
        return returned && (uint)parameter < (uint)function.Definition.ParameterCount
            && function.FrameSlot(parameter).Binding < 0 ? new(parameter, copyPc) : null;
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
            if (op is NativeFastOperation.NullReturn or NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn or NativeFastOperation.AddressReturn)
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
                if (op is not (NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant
                    or NativeFastOperation.NullConstant or NativeFastOperation.Integer64Constant)
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
    NativeMachineCallableSource? Source, NativeOffset? ArgumentWindow)
{
    internal int WorkspaceBytes => ArgumentWindow.HasValue ? (NativeTierLayout.HeaderBytes + 15) & ~15 : Leaf.WorkspaceBytes;

    internal static NativeOffset? LinkWindow(NativeMachineLeaf leaf, IReadOnlyList<int> inputs, NativeFrameSlot[] caller)
    {
        if (leaf.ReturnWindow is not { } window) return null;
        NativeMachineParameter parameter = leaf.Parameters[window.Parameter];
        ref readonly NativeFrameSlot input = ref caller[inputs[window.Parameter]];
        if (input.Binding >= 0 || parameter.Descriptor >= 0) return null;
        // These bases are used only with the captured formal displacement.
        // This does not expose intervening caller cells to the leaf.
        return new(input.Offset.Bytes - parameter.Offset, input.Offset.References - parameter.Reference);
    }
}
