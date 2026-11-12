using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private ScopeAllocator? scopeAllocator;
    private BoundScopePlaces? scopePlaces;
    private BoundScopePlaces CurrentPlaces => scopePlaces ??= BoundScopePlaces.For(module);

    private BoundScopePlaces Places(Module image, ref BoundScopePlaces? places) =>
        places ??= ReferenceEquals(image, module) ? CurrentPlaces : BoundScopePlaces.For(image);

    private void LoadScopeStorage<TFrame>(Module image, int function, int pc, int slot, ref FrameSlots slots,
        ref OperandStack<TFrame> stack, ref TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (slots.Peek<FrameSlots.Raw>(slot) is { } value)
        {
            stack.Add(value);
            return;
        }
        var targets = ReferenceEquals(image, module) ? executionTargets : ExecutionTargets.For(image);
        if (targets.UsesScopeStorage(image, function, pc))
        {
            stack.AddNumber(slots.RawNumber(slot));
            frame.RawScopeStorageLoad();
        }
        else stack.Add(slots.Materialize(slot, ref frame));
    }

    private static void LoadFieldPlace<TFrame>(ref OperandStack<TFrame> stack, ScopeValue scope, int offset)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        var value = scope.GetOperandField(offset, out double number);
        if (value is null) stack.AddNumber(number);
        else stack.Add(value);
    }

    private Value CaptureConstant(Value value) => value switch
    {
        ScopeValue scope => scope.MapFields(CaptureConstant),
        RangeValue { SnapshotValues: { } values } range =>
            new RangeValue(values.Select(CaptureConstant).ToArray(), range.ItemType),
        FunctionValue { Captures: not null } function => function with { Captures = ClosureCaptures.Own(function.Captures.Select(CaptureConstant).ToArray()) },
        _ => Capture(value)
    };
}
