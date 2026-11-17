using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeGuestView(int Source, int Layout, bool Captured)
{
    private readonly record struct Shape(int Argument, int Layout, bool Captured);

    internal static Dictionary<int, NativeGuestView>? Link(NativeImage image, NativeExecutionFunction caller,
        NativeFastOperation[] operations)
    {
        Dictionary<int, Shape?>? shapes = null;
        Dictionary<int, NativeGuestView>? views = null;
        for (int pc = 0; pc < operations.Length; pc += caller.Instructions[pc].Span)
        {
            NativeExecutionInstruction call = caller.Instructions[pc];
            if (call.Op != NativeOp.Call || call.Handler != NativeInterpreter.DirectCallHandler
                || call.B < 0 || call.C >= 0 || !caller.RawWritable[call.Result]
                || !caller.HasKind(call.A, NativeKind.Function)) continue;
            ref readonly NativeFrameSlot result = ref caller.FrameSlot(call.Result);
            if (result.Binding >= 0 || !NullableAddress(result.Type) || caller.FrameSlot(call.A).Binding >= 0) continue;
            shapes ??= [];
            if (!shapes.TryGetValue(call.B, out Shape? found))
                shapes.Add(call.B, found = Describe(image, call.B));
            if (found is not { } shape) continue;
            int source = caller.Metadata[pc].Inputs![shape.Argument];
            ref readonly NativeFrameSlot input = ref caller.FrameSlot(source);
            if (input.Binding >= 0 || !NullableAddress(input.Type)) continue;
            (views ??= []).Add(pc, new(source, shape.Layout, shape.Captured));
            operations[pc] = NativeFastOperation.GuestView;
        }
        return views;
    }

    private static Shape? Describe(NativeImage image, int index)
    {
        NativeFunctionCode body = image.Program.Functions[index];
        NativeBackendFunction linked = image.Program.Backend!.Functions[index];
        if (!linked.ContextFree || !linked.PureSynchronous || linked.Instructions.Count is < 2 or > 16
            || !NullableAddress(image.Type(body.Signature.Result))) return null;
        int formalEnd = body.CaptureCount + body.ParameterCount;
        int converted = -1, layout = -1, argument = -1;
        uint nulls = 0;
        if (body.Slots.Count > 32) return null;
        for (int pc = 0; pc < linked.Instructions.Count; pc += linked.Instructions[pc].Span)
        {
            NativeBackendInstruction instruction = linked.Instructions[pc];
            if (instruction.Handler == NativeBackendHandler.Nop) continue;
            if (instruction.Op == NativeOp.Return)
                return pc + instruction.Span == linked.Instructions.Count && converted >= 0 && instruction.A == converted
                    ? new(argument, layout, body.CaptureCount != 0) : null;
            if (instruction.Result < formalEnd || !linked.RawWritable[instruction.Result]) return null;
            uint bit = 1U << instruction.Result;
            switch (instruction.Op)
            {
                case NativeOp.Constant when image.Program.Constants[instruction.A].Kind == NativeKind.Null:
                case NativeOp.Copy when (nulls & (1U << instruction.A)) != 0:
                    if (converted >= 0 || !image.Type(body.Slots[instruction.Result].Type).Accepts(NativeKind.Null)) return null;
                    nulls |= bit;
                    continue;
                case NativeOp.Convert when converted < 0 && instruction.A >= body.CaptureCount && instruction.A < formalEnd:
                    int type = body.Instructions[instruction.Operand].Type;
                    if (image.Program.Types[type] is not { Pointee: < 0, Kind: TypeKind.Scope | TypeKind.Null, Layouts: [int exact] }
                        || !NativeGuestObjectTable.EncodesLayout(exact)
                        || !NullableAddress(image.Type(body.Slots[instruction.A].Type))
                        || !NullableAddress(image.Type(body.Slots[instruction.Result].Type))) return null;
                    layout = exact;
                    argument = instruction.A - body.CaptureCount;
                    converted = instruction.Result;
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool NullableAddress(NativeType type) =>
        type.Kinds == ((1UL << (int)NativeKind.Address) | (1UL << (int)NativeKind.Null));
}
