using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryInlineSlots<TFrame>(Module image, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (stack.PeekReference(instruction.A - 1) is not SlotArenaValue owner) return false;
        double index = 0;
        if (instruction.A == 2 && !stack.TryPeekNumber(0, out index)) return false;
        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instruction.A == 1)
            {
                stack.Discard();
                stack.AddNumber(owner.Count);
            }
            else
            {
                Value value = owner.Get(index);
                stack.Discard(2);
                stack.Add(value);
            }
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure(instruction.Text!, error);
            throw;
        }
    }
}
