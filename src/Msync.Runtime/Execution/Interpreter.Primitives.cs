using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryRunPrimitive<TFrame, TLayout>(Module image, Instruction instruction, int bound,
        ref TFrame frame, ref OperandStack<TFrame> stack, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        var operation = (NumericIntrinsic)(byte)bound;
        var transport = typeof(TLayout) == typeof(FrameSlots.RawWord) ? (WordTransport)(bound >> 8) : WordTransport.None;
        bool rawLeft = (transport & WordTransport.First) != 0, rawRight = (transport & WordTransport.Second) != 0;
        uint leftWord = 0, rightWord = 0;
        double left = 0, right = 0;
        int arity = instruction.A;
        if (rawLeft ? !stack.TryPeekWord32(arity - 1, out leftWord) : !stack.TryPeekNumber(arity - 1, out left))
            return DeclineWordTransport(ref stack, arity, transport);
        if (arity == 2 && (rawRight ? !stack.TryPeekWord32(0, out rightWord) : !stack.TryPeekNumber(0, out right)))
            return DeclineWordTransport(ref stack, arity, transport);

        if (operation == NumericIntrinsic.Power)
        {
            stack.Discard(2);
            stack.AddNumber(frame.Power(left, right, image, instruction));
            return true;
        }

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation == NumericIntrinsic.Floor)
            {
                stack.Discard(arity);
                stack.AddNumber(Math.Floor(left));
                return true;
            }
            uint result = Word32.Apply(operation, rawLeft ? leftWord : Word32.Operand(left),
                rawRight ? Word32.RightOperand(operation, rightWord) : Word32.RightOperand(operation, right));
            stack.Discard(arity);
            if ((transport & WordTransport.Result) != 0) stack.AddWord32(result);
            else stack.AddNumber(result);
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure(instruction.Text!, error);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DeclineWordTransport<TFrame>(ref OperandStack<TFrame> stack, int arity, WordTransport transport)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (transport != WordTransport.None) stack.WidenWordArguments(arity, transport);
        return false;
    }
}
