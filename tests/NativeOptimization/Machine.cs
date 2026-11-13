using Msync.Core;
using Msync.Core.Native;

internal static class Machine
{
    internal static NativeValue Run(NativeProgram program, IReadOnlyList<NativeValue> arguments)
    {
        NativeFunctionCode function = program.Functions[program.EntryPoint];
        NativeLayout[] layouts = program.Layouts.Select(layout => new NativeLayout(layout.Fields.Select(
            field => new NativeField(field.Name, Storage(program.Types[field.Type]))))).ToArray();
        NativeAllocation frame = new(new NativeLayout(function.Slots.Select((slot, index) =>
            new NativeField(index.ToString(System.Globalization.CultureInfo.InvariantCulture), Storage(program.Types[slot.Type])))));
        NativeAddress[] locations = Enumerable.Range(0, function.Slots.Count).Select(frame.Field).ToArray();
        for (int index = 0; index < arguments.Count; index++) locations[index].Store(arguments[index]);
        NativeValue Get(int slot) => locations[slot].Load();
        void Set(int slot, NativeValue value) => locations[slot].Store(value);
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            switch (instruction.Op)
            {
                case NativeOp.Nop: break;
                case NativeOp.Drop: locations[instruction.A].Clear(); break;
                case NativeOp.Constant: Set(instruction.Result, Literal(program.Constants[instruction.A])); break;
                case NativeOp.Copy: Set(instruction.Result, Get(instruction.A)); break;
                case NativeOp.Move:
                    Set(instruction.Result, Get(instruction.A)); locations[instruction.A].Clear(); break;
                case NativeOp.FrameAddress: Set(instruction.Result, NativeValue.FromAddress(locations[instruction.A])); break;
                case NativeOp.BindLocation:
                    locations[instruction.A] = Get(instruction.B).AsAddress().OffsetBy(default, Storage(program.Types[function.Slots[instruction.A].Type])); break;
                case NativeOp.Allocate: Set(instruction.Result, NativeValue.FromAddress(new NativeAllocation(layouts[instruction.A], instruction.A).Address)); break;
                case NativeOp.AddressOffset:
                    NativeFieldLayout field = layouts[instruction.B][instruction.C];
                    Set(instruction.Result, NativeValue.FromAddress(Get(instruction.A).AsAddress().OffsetBy(field.Offset, field.Type))); break;
                case NativeOp.Load:
                    Set(instruction.Result, NativeStorage.Load(Storage(program.Types[instruction.Type]), Get(instruction.A).AsAddress())); break;
                case NativeOp.Store:
                    NativeStorage.Store(Storage(program.Types[instruction.Type]), Get(instruction.A).AsAddress(), Get(instruction.B)); break;
                case NativeOp.Binary: Set(instruction.Result, Binary((NativeBinary)instruction.Immediate, Get(instruction.A), Get(instruction.B))); break;
                case NativeOp.Jump: pc = instruction.A - 1; break;
                case NativeOp.Branch: pc = (Get(instruction.A).AsBool() ? instruction.B : instruction.C) - 1; break;
                case NativeOp.Acquire: if (instruction.Result >= 0) Set(instruction.Result, Get(instruction.A)); break;
                case NativeOp.Return: return Get(instruction.A);
                default: throw new InvalidOperationException($"The independent test machine has no {instruction.Op}.");
            }
        }
        return NativeValue.Null;
    }

    private static NativeType Storage(NativeCodeType type)
    {
        if (type.Pointee >= 0) return NativeType.Of(NativeKind.Address);
        List<NativeType> choices = [];
        foreach (NativeKind kind in Enum.GetValues<NativeKind>())
            if ((type.Kind & SourceKind(kind)) != 0) choices.Add(NativeType.Of(kind));
        return choices.Count == 0 ? NativeType.Never : NativeType.Union(choices.ToArray());
    }
    private static TypeKind SourceKind(NativeKind kind) => kind switch
    {
        NativeKind.Null => TypeKind.Null, NativeKind.Bool => TypeKind.Bool, NativeKind.Number => TypeKind.Number,
        NativeKind.String => TypeKind.String, NativeKind.Path => TypeKind.Path, NativeKind.Connection => TypeKind.Connection,
        NativeKind.Function => TypeKind.Function, NativeKind.Range => TypeKind.Range, NativeKind.Process => TypeKind.Process,
        NativeKind.Type => TypeKind.Type, NativeKind.Address => TypeKind.Scope,
        _ => throw new InvalidOperationException()
    };
    private static NativeValue Literal(NativeConstant value) => value.Kind switch
    {
        NativeKind.Null => NativeValue.Null, NativeKind.Bool => NativeValue.FromBool(value.Boolean),
        NativeKind.Number => NativeValue.FromNumber(value.Number), NativeKind.String => NativeValue.FromString(value.Text!),
        NativeKind.Path => NativeValue.FromPath(value.Path ?? new(value.Text!)),
        NativeKind.Connection => NativeValue.FromConnection(new(value.ConnectionKind, value.Text!)),
        NativeKind.Type => NativeValue.FromType(value.Type!), _ => throw new InvalidOperationException()
    };
    private static NativeValue Binary(NativeBinary operation, NativeValue left, NativeValue right)
    {
        double a = left.AsNumber(), b = right.AsNumber();
        return operation switch
        {
            NativeBinary.Add => NativeValue.FromNumber(a + b), NativeBinary.Subtract => NativeValue.FromNumber(a - b),
            NativeBinary.Multiply => NativeValue.FromNumber(a * b), NativeBinary.Divide => NativeValue.FromNumber(a / b),
            NativeBinary.Remainder => NativeValue.FromNumber(a % b), NativeBinary.Power => NativeValue.FromNumber(Math.Pow(a, b)),
            NativeBinary.Equal => NativeValue.FromBool(a == b), NativeBinary.NotEqual => NativeValue.FromBool(a != b),
            NativeBinary.Less => NativeValue.FromBool(a < b), NativeBinary.LessEqual => NativeValue.FromBool(a <= b),
            NativeBinary.Greater => NativeValue.FromBool(a > b), NativeBinary.GreaterEqual => NativeValue.FromBool(a >= b),
            _ => throw new InvalidOperationException()
        };
    }
}
