using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Dictionary<string, ScopeLayout>? scopeLayouts;

    private ScopeValue MakeScope(Instruction instruction, Value[] values)
    {
        var spelling = instruction.Text ?? "";
        var layouts = scopeLayouts ??= new(StringComparer.Ordinal);
        if (!layouts.TryGetValue(spelling, out var layout))
            layouts.Add(spelling, layout = ScopeLayout.Parse(spelling));
        return new(layout, values) { AllocationSite = instruction.B };
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
        if (hint > 0 && scope.IsField(hint - 1, name)) scope.SetField(hint - 1, value);
        else scope.SetMember(name, value);
        return Value.Null;
    }

    private static Value ScopeMember(ScopeValue scope, string name, int hint)
    {
        if (hint > 0 && scope.IsField(hint - 1, name)) return scope.GetField(hint - 1);
        if (scope.Members.TryGetValue(name, out var member)) return member;
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
