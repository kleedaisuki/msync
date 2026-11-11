using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Dictionary<string, ScopeLayout>? scopeLayouts;
    private ScopeAllocator? scopeAllocator;

    private ScopeValue MakeScope(Instruction instruction, ref OperandStack stack)
    {
        var spelling = instruction.Text ?? "";
        var layouts = scopeLayouts ??= new(StringComparer.Ordinal);
        if (!layouts.TryGetValue(spelling, out var layout))
            layouts.Add(spelling, layout = ScopeLayout.Parse(spelling));
        return stack.PopScope(scopeAllocator ??= new(), layout, instruction.A, instruction.B);
    }
    private static Value ReadLocal(Value[] locals, int slot) =>
        locals[slot] is CellValue cell ? cell.Data : locals[slot];

    private static void WriteLocal(Value[] locals, int slot, Value value)
    {
        if (locals[slot] is CellValue cell) cell.Data = value;
        else locals[slot] = value;
    }

    private static Value CaptureLocal(Value[] locals, int slot)
    {
        if (locals[slot] is CellValue cell) return cell;
        return locals[slot] = new CellValue(locals[slot]);
    }

    private static Value StoreMember(Value receiver, string name, Value value, int hint = 0)
    {
        if (receiver is not ScopeValue scope) throw new MsyncException($"Cannot assign a field on {receiver.Type}");
        scope.SetField(ScopeOffset(scope, name, hint), value);
        return Value.Null;
    }

    private static void StoreNumberMember(Value receiver, string name, double value, int hint)
    {
        if (receiver is not ScopeValue scope) throw new MsyncException($"Cannot assign a field on {receiver.Type}");
        scope.SetNumber(ScopeOffset(scope, name, hint), value);
    }

    private void LoadMember(ref OperandStack stack, string name, int hint)
    {
        Value receiver = stack.Pop();
        if (receiver is not ScopeValue scope)
        {
            stack.Add(Member(receiver, name, hint));
            return;
        }
        var value = scope.GetOperandField(ScopeOffset(scope, name, hint), out double number);
        if (value is null) stack.AddNumber(number);
        else stack.Add(value);
    }

    private static Value ScopeMember(ScopeValue scope, string name, int hint)
        => scope.GetField(ScopeOffset(scope, name, hint));

    private static int ScopeOffset(ScopeValue scope, string name, int hint)
    {
        if (hint > 0 && scope.IsField(hint - 1, name)) return hint - 1;
        if (scope.TryGetFieldOffset(name, out int offset)) return offset;
        throw new MsyncException($"Scope has no member '{name}'");
    }

    private Value CaptureConstant(Value value) => value switch
    {
        ScopeValue scope => scope.MapFields(CaptureConstant),
        RangeValue { SnapshotValues: { } values } range =>
            new RangeValue(values.Select(CaptureConstant).ToArray(), range.ItemType),
        FunctionValue { Captures: not null } function => function with { Captures = function.Captures.Select(CaptureConstant).ToArray() },
        _ => Capture(value)
    };
}
