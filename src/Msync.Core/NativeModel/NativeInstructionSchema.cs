namespace Msync.Core.Native;

[Flags]
public enum NativeOperands : byte
{
    None = 0, Result = 1, A = 2, B = 4, C = 8, Type = 16, Immediate = 32, Symbol = 64, Inputs = 128
}

public readonly record struct NativeInstructionSchema(NativeOperands Encoded, NativeOperands Values, bool OptionalResult = false)
{
    public bool AcceptsResult => OptionalResult || (Encoded & NativeOperands.Result) != 0;
    public bool RequiresResult => !OptionalResult && AcceptsResult;
}

public static partial class NativeInstructionFacts
{
    public static NativeInstructionSchema Schema(NativeOp op) => op switch
    {
        NativeOp.Nop or NativeOp.Unreachable => new(0, 0),
        NativeOp.Constant or NativeOp.FrameAddress => new(NativeOperands.Result | NativeOperands.A, 0),
        NativeOp.Copy or NativeOp.Move => new(NativeOperands.Result | NativeOperands.A, NativeOperands.A),
        NativeOp.Drop or NativeOp.Return => new(NativeOperands.A, NativeOperands.A),
        NativeOp.Jump or NativeOp.CloseIterator or NativeOp.PublishStatic => new(NativeOperands.A, 0),
        NativeOp.ExecutePlan => new(NativeOperands.A, 0, OptionalResult: true),
        NativeOp.BindLocation => new(NativeOperands.A | NativeOperands.B, NativeOperands.B),
        NativeOp.OpenIterator => new(NativeOperands.A | NativeOperands.B, NativeOperands.A),
        NativeOp.Allocate => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B, 0),
        NativeOp.ProjectAddress or NativeOp.EnvironmentAddress =>
            new(NativeOperands.Result | NativeOperands.A | NativeOperands.B, NativeOperands.A),
        NativeOp.AdaptFunction => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B, NativeOperands.A | NativeOperands.Inputs),
        NativeOp.AddressOffset => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.C, NativeOperands.A),
        NativeOp.IndexAddress => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.C, NativeOperands.A | NativeOperands.B),
        NativeOp.CopyMemory => new(NativeOperands.A | NativeOperands.B | NativeOperands.C | NativeOperands.Immediate, NativeOperands.A | NativeOperands.B | NativeOperands.C),
        NativeOp.ClearMemory => new(NativeOperands.A | NativeOperands.C | NativeOperands.Immediate, NativeOperands.A | NativeOperands.C),
        NativeOp.Load => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Type, NativeOperands.A),
        NativeOp.Store => new(NativeOperands.A | NativeOperands.B | NativeOperands.C | NativeOperands.Type, NativeOperands.A | NativeOperands.B),
        NativeOp.Branch => new(NativeOperands.A | NativeOperands.B | NativeOperands.C, NativeOperands.A),
        NativeOp.Unary => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Immediate, NativeOperands.A),
        NativeOp.Binary => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.Immediate, NativeOperands.A | NativeOperands.B),
        NativeOp.Convert or NativeOp.Narrow or NativeOp.DeferRange => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Type, NativeOperands.A),
        NativeOp.MakeFunction => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.Inputs, NativeOperands.Inputs),
        NativeOp.Call => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.Inputs, NativeOperands.A | NativeOperands.Inputs),
        NativeOp.CallNative => new(NativeOperands.Result | NativeOperands.Symbol | NativeOperands.Inputs, NativeOperands.Inputs),
        NativeOp.MakeRange => new(NativeOperands.Result | NativeOperands.Type | NativeOperands.Inputs, NativeOperands.Inputs),
        NativeOp.MakeLazyRange => new(NativeOperands.Result | NativeOperands.Type | NativeOperands.Inputs | NativeOperands.Symbol, NativeOperands.Inputs),
        NativeOp.RangeFromMemory => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B | NativeOperands.C | NativeOperands.Type | NativeOperands.Immediate,
            NativeOperands.A | NativeOperands.B | NativeOperands.Inputs),
        NativeOp.NextIterator => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B, 0),
        NativeOp.Transfer => new(NativeOperands.Result | NativeOperands.A | NativeOperands.B, NativeOperands.A | NativeOperands.B),
        NativeOp.ProcessCall => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Inputs, NativeOperands.A | NativeOperands.Inputs),
        NativeOp.SelectPath => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Symbol, NativeOperands.A),
        NativeOp.Barrier => new(NativeOperands.Immediate, 0),
        NativeOp.Acquire => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Immediate, NativeOperands.A, OptionalResult: true),
        NativeOp.PlanResult or NativeOp.TestStatic or NativeOp.StaticAddress => new(NativeOperands.Result | NativeOperands.A, 0),
        NativeOp.MakeType => new(NativeOperands.Result | NativeOperands.A | NativeOperands.Type | NativeOperands.Immediate, NativeOperands.A),
        _ => throw new MsyncException($"Unknown native opcode {(byte)op}")
    };
}
