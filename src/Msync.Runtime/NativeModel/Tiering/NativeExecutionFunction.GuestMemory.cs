using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeGuestAccess(NativeFrameSlot Base, NativeFrameSlot Value, int Displacement, NativeKind Kind);

internal sealed partial class NativeExecutionFunction
{
    internal ref readonly NativeFrameSlot FrameSlot(int slot) => ref image.Frame(Index).Slots[slot];

    internal bool TryGuestCopy(int pc, in NativeExecutionInstruction instruction)
    {
        if (instruction.Op != NativeOp.Copy || !RawWritable[instruction.Result]) return false;
        ref readonly NativeFrameSlot source = ref FrameSlot(instruction.A);
        ref readonly NativeFrameSlot target = ref FrameSlot(instruction.Result);
        const ulong address = 1UL << (int)NativeKind.Address;
        const ulong nullable = address | 1UL << (int)NativeKind.Null;
        return source.Binding < 0 && target.Binding < 0 && target.Type.Equals(source.Type)
            && source.Type.Kinds is address or nullable
            && (Operands[pc].Type is not { } copyType || copyType.Equals(source.Type));
    }

    internal bool TryGuestLayoutCheck(int pc, out NativeFrameSlot basis)
    {
        NativeExecutionInstruction instruction = Instructions[pc];
        basis = default;
        if (instruction.Op != NativeOp.Unary || instruction.Immediate != (long)NativeUnary.HasLayout
            || !NativeGuestObjectTable.EncodesLayout(instruction.B) || !CanWriteRaw(instruction.Result)) return false;
        NativeFrameSlot[] slots = image.Frame(Index).Slots;
        ref readonly NativeFrameSlot source = ref slots[instruction.A];
        ref readonly NativeFrameSlot destination = ref slots[instruction.Result];
        if (source.Binding >= 0 || !source.ExactAddress || destination.Binding >= 0
            || !Exact(destination.Type, NativeKind.Bool)) return false;
        basis = source;
        return true;
    }

    internal bool TryGuestAccess(int pc, out NativeGuestAccess access)
    {
        access = default;
        NativeExecutionInstruction instruction = Instructions[pc];
        if (instruction.Op is not (NativeOp.Load or NativeOp.Store)) return false;
        if (instruction.Handler == NativeInterpreter.FrameLoadHandler || instruction.Handler == NativeInterpreter.FrameStoreHandler) return false;
        NativeFrameSlot[] slots = image.Frame(Index).Slots;
        ref readonly NativeFrameSlot basis = ref slots[instruction.A];
        if (!basis.ExactAddress || basis.Binding >= 0 && (instruction.Op != NativeOp.Load || !basis.Borrowable)) return false;
        bool load = instruction.Op == NativeOp.Load;
        int valueSlot = load ? instruction.Result : instruction.B;
        ref readonly NativeFrameSlot value = ref slots[valueSlot];
        if (value.Type.IsUnion || value.Type.IsNever || value.Type.Kind is not (NativeKind.Number or NativeKind.Bool)
            || load && (value.Binding >= 0 || !CanWriteRaw(valueSlot))) return false;
        if (!TryGuestField(pc, instruction.Handler, basis.Pointee, value.Type.Kind, out int field)) return false;
        access = new(basis, value, field, value.Type.Kind);
        return true;
    }

    private bool TryGuestField(int pc, nint handler, NativeType? pointee, NativeKind kind, out int offset)
    {
        offset = 0;
        if (handler == NativeInterpreter.OffsetLoadHandler || handler == NativeInterpreter.OffsetStoreHandler)
        {
            NativeFieldLayout field = Operands[pc].Field;
            offset = field.Offset.Bytes;
            return Exact(field.Type, kind);
        }
        if (handler == NativeInterpreter.ProjectLoadHandler || handler == NativeInterpreter.ProjectStoreHandler)
        {
            NativeProjectionField[] fields = Operands[pc].Projection!;
            offset = fields[0].Field.Offset.Bytes;
            foreach (ref readonly NativeProjectionField field in fields.AsSpan())
                if (field.Field.Offset.Bytes != offset || !Exact(field.Field.Type, kind)) return false;
            return true;
        }
        return pointee is not null && Exact(pointee, kind);
    }

    private static bool Exact(NativeType type, NativeKind kind) => !type.IsUnion && !type.IsNever && type.Kind == kind;
}
