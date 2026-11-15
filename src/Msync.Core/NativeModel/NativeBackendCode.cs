namespace Msync.Core.Native;

public enum NativeBackendHandler : byte
{
    Standard, Nop, FrameStore, OffsetLoad, OffsetStore, OffsetBind,
    ProjectLoad, ProjectStore, NumberBinary, ConnectionQualify, UnsignedBinary, DirectCall, CompiledCopy
}

[Flags]
public enum NativeNumberForm : byte { Float64 = 0, UnsignedA = 1, UnsignedB = 2, UnsignedResult = 4 }

public sealed record NativeBackendInstruction(NativeOp Op, int Result, int A, int B, int C,
    int Span, long Immediate, NativeBackendHandler Handler, int Operand,
    ulong OperandKinds, bool NeedsLayoutCheck, NativeNumberForm Numbers = NativeNumberForm.Float64, int Connection = -1);

public sealed record NativeBackendFunction(IReadOnlyList<NativeBackendInstruction> Instructions,
    IReadOnlyList<bool> RawWritable, bool PureSynchronous, bool ContextFree, bool ObservableBinary);

public sealed record NativeBackendCode(IReadOnlyList<NativeBackendFunction> Functions,
    IReadOnlyList<NativeConnection> Connections);
