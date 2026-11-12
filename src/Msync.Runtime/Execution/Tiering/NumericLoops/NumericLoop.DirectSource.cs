using Msync.Core;

namespace Msync.Runtime;

internal sealed partial class NumericLoop
{
    internal bool TryRunSource<TFrame>(FunctionCode body, double captured, double argument, CellValue? cell,
        ref TFrame frame, CancellationToken cancellationToken, out double result)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        result = default;
        bool counted = frame.NativeLoopObservation == NativeLoopObservation.Counts;
        var page = native ?? (attemptedNative ? null : CompileNative(counted, ref frame));
        if (page is null || nativeCounted != counted) return false;

        Span<double> registers = stackalloc double[registerCount];
        int captureRegister = -1;
        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i] == body.CaptureCount) registers[i] = argument;
            else { registers[i] = captured; captureRegister = i; }
        }
        constants.AsSpan().CopyTo(registers[locals.Length..]);
        registers[StackBase..].Clear();
        _ = RunNative(page, registers, body.Instructions, counted, ref frame, cancellationToken);

        // With no native effects or aliasing local slots, one numeric write
        // can commit here to the actual capture. A plain capture is private.
        if (cell is not null && captureRegister >= 0 && (written & (1UL << captureRegister)) != 0
            && BitConverter.DoubleToInt64Bits(registers[captureRegister]) != BitConverter.DoubleToInt64Bits(captured))
        {
            cell.Data = new NumberValue(registers[captureRegister]);
            frame.Box();
        }
        result = registers[StackBase];
        return true;
    }
}
