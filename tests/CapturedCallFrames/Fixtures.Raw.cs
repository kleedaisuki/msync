using Msync.Core;

internal static partial class Fixtures
{
    internal static readonly string[] RawOutput = ["-0", "NaN", "-0", "42", "NaN", "-0", "207"];
    internal const long NaNPayload = 0x7ff8000000001234;
    internal const long NegativeNaNPayload = unchecked((long)0xfff8000000001234UL);

    internal static Module RawNumbers()
    {
        const int zero = 0, nan = 1, seven = 2, eleven = 3, fortyTwo = 4, text = 5, hundred = 6, three = 7, four = 8;
        const int directZero = 0, directNan = 1, detached = 2, promoted = 3, resumed = 4;
        const int identity = 1, split = 2, detach = 3, promote = 4, promotedRead = 5, suspend = 6;
        List<Instruction> code =
        [
            new(OpCode.MakeClosure, identity), new(OpCode.Constant, zero), new(OpCode.Negate),
            new(OpCode.Call, 1, identity + 1), new(OpCode.InitializeLocal, directZero),
        ];
        PrintSlot(directZero);
        Closure(split);
        RawNaN();
        code.AddRange([new(OpCode.Call, 1, split + 1), new(OpCode.InitializeLocal, directNan)]);
        PrintSlot(directNan);
        Closure(detach);
        RawZero();
        RawNaN();
        code.AddRange([new(OpCode.Call, 2, detach + 1), new(OpCode.InitializeLocal, detached)]);
        for (int field = FirstDetachedField; field <= LastDetachedField; field++)
        {
            code.AddRange([new(OpCode.LoadLocal, detached), new(OpCode.LoadField, field)]);
            PrintNumber(code, text);
        }
        Closure(promote);
        RawZero();
        code.AddRange([new(OpCode.Call, 1, promote + 1), new(OpCode.Call, 0, promotedRead + 1), new(OpCode.InitializeLocal, promoted)]);
        PrintSlot(promoted);
        // The suspended caller has raw values both below and inside the callee's input slice.
        code.AddRange([new(OpCode.Constant, hundred), new(OpCode.Constant, hundred), new(OpCode.Add)]);
        Closure(suspend);
        code.AddRange([
            new(OpCode.Constant, three), new(OpCode.Constant, four), new(OpCode.Add), new(OpCode.Call, 1, suspend + 1),
            new(OpCode.Add), new(OpCode.InitializeLocal, resumed),
        ]);
        PrintSlot(resumed);
        code.AddRange([
            new(OpCode.LoadLocal, directZero), new(OpCode.LoadLocal, directNan), new(OpCode.LoadLocal, detached),
            new(OpCode.LoadLocal, promoted), new(OpCode.LoadLocal, resumed),
            new(OpCode.NewScope, RawResultLayout), new(OpCode.Return),
        ]);
        return Typed(new(
            [
                new NumberValue(0), new NumberValue(BitConverter.Int64BitsToDouble(NaNPayload)),
                new NumberValue(7), new NumberValue(11), new NumberValue(42), new TypeValue(TypeRef.String),
                new NumberValue(100), new NumberValue(3), new NumberValue(4),
            ],
            [
                new("raw-main", 0, 5, code.ToArray(), TypeRef.Scope),
                new("raw-contiguous-identity", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)],
                    TypeRef.Number, ParameterTypes: [TypeRef.Number]),
                new("raw-split-identity", 1, 3, [new(OpCode.LoadLocal, 2), new(OpCode.Return)],
                    TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("raw-detach", 2, 4, [
                    new(OpCode.LoadLocal, 2), new(OpCode.Constant, fortyTwo), new(OpCode.StoreLocal, 2),
                    new(OpCode.LoadLocal, 2), new(OpCode.LoadStorage, 3),
                    new(OpCode.NewScope, DetachedLayout), new(OpCode.Return),
                ], TypeRef.Scope, CaptureCount: 2, ParameterTypes: [TypeRef.Number, TypeRef.Number]),
                new("raw-promote", 1, 3, [
                    new(OpCode.LoadCell, 2, NumberCode + 1), new(OpCode.MakeClosure, promotedRead, 1), new(OpCode.Return),
                ], TypeRef.Function, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("raw-promoted-read", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)],
                    TypeRef.Number, CaptureCount: 1),
                new("raw-await", 1, 3, [
                    new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Pop), new(OpCode.LoadLocal, 2), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
            ], OptimizationLevel: 0),
            new([], RawResultCode, []),
            new([NumberCode], NumberCode, []),
            new([NumberCode], NumberCode, [NumberValueCapture, NumberValueCapture]),
            new([NumberCode, NumberCode], DetachedCode, [NumberValueCapture, NumberValueCapture]),
            new([NumberCode], FunctionCodeId, [NumberValueCapture, NumberValueCapture]),
            new([], NumberCode, [NumberCell]),
            new([NumberCode], NumberCode, [NumberValueCapture, NumberValueCapture]));

        void PrintSlot(int slot)
        {
            code.Add(new(OpCode.LoadLocal, slot));
            PrintNumber(code, text);
        }
        void Closure(int function) =>
            code.AddRange([new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, function, 2)]);
        void RawZero() => code.AddRange([new(OpCode.Constant, zero), new(OpCode.Negate)]);
        void RawNaN() => code.AddRange([new(OpCode.Constant, nan), new(OpCode.Negate)]);
    }
}
