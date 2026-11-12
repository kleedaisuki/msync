namespace Msync.Optimization.Execution;

/// <summary>A bound primitive. Its register representation is separate from the guest Number type.</summary>
public readonly record struct ExecutionNumericOperation(NumericIntrinsic Operation, int Arity)
{
    public NumericRepresentation Representation => Operation is NumericIntrinsic.Floor or NumericIntrinsic.Power
        ? NumericRepresentation.Binary64 : NumericRepresentation.Word32;

    internal static ExecutionNumericOperation? Bind(Instruction instruction) =>
        instruction.Op == OpCode.NativeCall && instruction.Text is { } name
        && NativeSignatures.TryGet(name, out var signature)
        && signature.Numeric != NumericIntrinsic.None && signature.Parameters.Count == instruction.A
            ? new(signature.Numeric, instruction.A) : null;
}
