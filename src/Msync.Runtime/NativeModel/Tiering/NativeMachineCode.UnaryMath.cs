namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private void UnaryMath(NativeFastOperation operation)
        {
            switch (operation)
            {
                case NativeFastOperation.NumberAbs:
                    Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0, 0x48, 0x0f, 0xba, 0xf0, 0x3f, 0x66, 0x48, 0x0f, 0x6e, 0xc0);
                    return;
                case NativeFastOperation.NumberSqrt:
                    Bytes(0xf2, 0x0f, 0x51, 0xc0);
                    return;
            }
            // ROUNDSD uses an immediate rounding mode, independent of MXCSR.RC.
            byte mode = operation switch
            {
                NativeFastOperation.NumberRoundEven => 0,
                NativeFastOperation.NumberFloor => 1,
                NativeFastOperation.NumberCeiling => 2,
                NativeFastOperation.NumberTruncate => 3,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            Bytes(0x66, 0x0f, 0x3a, 0x0b, 0xc0, (byte)(mode | 8));
        }
    }

    private sealed partial class Arm64
    {
        private void UnaryMath(NativeFastOperation operation) => Word(operation switch
        {
            NativeFastOperation.NumberFloor => 0x1e654000, // FRINTM d0, d0
            NativeFastOperation.NumberCeiling => 0x1e64c000,
            NativeFastOperation.NumberTruncate => 0x1e65c000,
            NativeFastOperation.NumberRoundEven => 0x1e644000,
            NativeFastOperation.NumberAbs => 0x1e60c000,
            NativeFastOperation.NumberSqrt => 0x1e61c000,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
    }
}
