using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
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

    private static Value StoreMember(Value receiver, string name, Value value)
    {
        if (receiver is not ScopeValue scope) throw new MsyncException($"Cannot assign a field on {receiver.Type}");
        scope.SetMember(name, value);
        return Value.Null;
    }

    private Value CaptureConstant(Value value) => value switch
    {
        ScopeValue scope => new ScopeValue(scope.Members.ToDictionary(pair => pair.Key, pair => CaptureConstant(pair.Value))) { AllocationSite = scope.AllocationSite },
        FunctionValue { Captures: not null } function => function with { Captures = function.Captures.Select(CaptureConstant).ToArray() },
        _ => Capture(value)
    };
}
