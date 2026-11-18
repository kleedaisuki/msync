using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeGuestAccess(NativeFrameSlot Base, NativeFrameSlot Value, int Displacement, NativeKind Kind);
internal readonly record struct NativeGuestReferenceLoad(NativeFrameSlot Base, NativeFrameSlot Target, NativeOffset Offset);
internal readonly record struct NativeGuestReferenceStore(NativeFrameSlot Base, NativeFrameSlot Source, NativeFieldLayout Field);

internal sealed partial class NativeExecutionFunction
{
    internal ref readonly NativeFrameSlot FrameSlot(int slot) => ref image.Frame(Index).Slots[slot];

    internal bool IsPrivateInteger64(int slot) => slot >= 0 && FrameSlot(slot) is
        { Binding: < 0, Type.Machine: { IsInteger: true, BitWidth: 64 } };

    internal bool CanStoreGuestNull(int slot) => slot >= 0 && RawWritable[slot]
        && FrameSlot(slot) is { Binding: < 0, Type: { IsUnion: true } type }
        && type.Accepts(NativeKind.Null) && type.Representation.References <= 2;

    internal bool CanComposeGuestFunction(int pc, int slot) => slot >= 0 && RawWritable[slot] && HasKind(slot, NativeKind.Function)
        && FrameSlot(slot).Binding < 0 && Metadata[pc].Inputs is { Count: 3 } inputs && IsPrivateInteger64(inputs[0])
        && IsPrivateNullable(inputs[1]) && IsPrivateNullable(inputs[2]);

    private bool IsPrivateNullable(int slot) => slot >= 0 && FrameSlot(slot).Binding < 0
        && FrameSlot(slot).Type.Accepts(NativeKind.Null);

    internal bool TryGuestCopy(int pc, in NativeExecutionInstruction instruction)
    {
        if (instruction.Op is not (NativeOp.Copy or NativeOp.Convert or NativeOp.Narrow)
            || !RawWritable[instruction.Result]) return false;
        ref readonly NativeFrameSlot source = ref FrameSlot(instruction.A);
        ref readonly NativeFrameSlot target = ref FrameSlot(instruction.Result);
        if (source.Binding >= 0 || target.Binding >= 0 || !IsReferenceCarrier(source.Type)
            || !IsReferenceCarrier(target.Type)) return false;
        ref readonly NativeExecutionOperand operand = ref Operands[pc];
        if (instruction.Op == NativeOp.Copy)
            return operand.Type is not { } copyType || copyType.Equals(source.Type);
        return !operand.NeedsLayoutCheck && operand.Type is { } refinement && refinement.Equals(target.Type)
            && image.Program.Types[Metadata[pc].Type] is { Pointee: < 0, Element: < 0 };
    }

    internal bool TryGuestReferenceLoad(int pc, in NativeExecutionInstruction instruction, out NativeGuestReferenceLoad load)
    {
        load = default;
        if (instruction.Op != NativeOp.Load || !RawWritable[instruction.Result]) return false;
        ref readonly NativeFrameSlot basis = ref FrameSlot(instruction.A);
        ref readonly NativeFrameSlot target = ref FrameSlot(instruction.Result);
        if (!basis.ExactAddress || basis.Binding >= 0 || target.Binding >= 0 || !IsReferenceCarrier(target.Type)) return false;
        ref readonly NativeExecutionOperand operand = ref Operands[pc];
        NativeFieldLayout field;
        if (instruction.Handler == NativeInterpreter.OffsetLoadHandler) field = operand.Field;
        else if (instruction.Handler == NativeInterpreter.ProjectLoadHandler && operand.Projection is [var only]) field = only.Field;
        else return false;
        if (!target.Type.Equals(field.Type)) return false;
        load = new(basis, target, field.Offset);
        return true;
    }

    private static bool IsReferenceCarrier(NativeType type)
    {
        const ulong address = 1UL << (int)NativeKind.Address;
        const ulong nullable = address | 1UL << (int)NativeKind.Null;
        return type.Kinds is address or nullable;
    }

    internal bool TryGuestReferenceStore(int pc, in NativeExecutionInstruction instruction, out NativeGuestReferenceStore store)
    {
        store = default;
        if (instruction.Op != NativeOp.Store) return false;
        ref readonly NativeFrameSlot basis = ref FrameSlot(instruction.A);
        ref readonly NativeFrameSlot source = ref FrameSlot(instruction.B);
        if (!basis.ExactAddress || basis.Binding >= 0 || source.Binding >= 0 || source.Type.IsNever
            || !(IsReferenceCarrier(source.Type) || Exact(source.Type, NativeKind.Null))) return false;
        ref readonly NativeExecutionOperand operand = ref Operands[pc];
        NativeFieldLayout target;
        if (instruction.Handler == NativeInterpreter.OffsetStoreHandler)
        {
            target = operand.Field;
        }
        else if (instruction.Handler == NativeInterpreter.ProjectStoreHandler && operand.Projection is { Length: > 0 } fields)
        {
            target = fields[0].Field;
            foreach (ref readonly NativeProjectionField field in fields.AsSpan())
                if (field.Field.Offset != target.Offset || !field.Field.Type.Equals(target.Type)) return false;
        }
        else return false;
        if (!IsReferenceCarrier(target.Type) || (source.Type.Kinds & ~target.Type.Kinds) != 0) return false;
        store = new(basis, source, target);
        return true;
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
        if (load && (value.Binding >= 0 || !CanWriteRaw(valueSlot))) return false;
        const ulong number = 1UL << (int)NativeKind.Number, boolean = 1UL << (int)NativeKind.Bool, nil = 1UL << (int)NativeKind.Null;
        ulong shape = value.Type.Kinds;
        if (shape is not (number or boolean) && (!load || shape is not ((number | nil) or (boolean | nil)))) return false;
        if (!TryGuestField(pc, instruction.Handler, basis.Pointee, value.Type, out int field)) return false;
        access = new(basis, value, field, (shape & number) != 0 ? NativeKind.Number : NativeKind.Bool);
        return true;
    }

    private bool TryGuestField(int pc, nint handler, NativeType? pointee, NativeType type, out int offset)
    {
        offset = 0;
        if (handler == NativeInterpreter.OffsetLoadHandler || handler == NativeInterpreter.OffsetStoreHandler)
        {
            NativeFieldLayout field = Operands[pc].Field;
            offset = field.Offset.Bytes;
            return field.Type.Equals(type);
        }
        if (handler == NativeInterpreter.ProjectLoadHandler || handler == NativeInterpreter.ProjectStoreHandler)
        {
            NativeProjectionField[] fields = Operands[pc].Projection!;
            offset = fields[0].Field.Offset.Bytes;
            foreach (ref readonly NativeProjectionField field in fields.AsSpan())
                if (field.Field.Offset.Bytes != offset || !field.Field.Type.Equals(type)) return false;
            return true;
        }
        return type.Equals(pointee);
    }

    private static bool Exact(NativeType type, NativeKind kind) => !type.IsUnion && !type.IsNever && type.Kind == kind;
}
