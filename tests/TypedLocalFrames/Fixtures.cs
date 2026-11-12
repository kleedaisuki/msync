using Msync.Core;

internal static class Fixtures
{
    internal const long NegativeNaN = unchecked((long)0xfff8000000001234UL);
    internal const string Echo = "echo:original argument";
    internal const string Entry = "typed-locals";
    internal const string Detached = "suspended-rebind";
    internal const string Capture = "update-cell";
    internal const string Loop = "numeric-exit";

    internal static Module Mixed(string child)
    {
        const int zero = 0, nan = 1, one = 2, two = 3, path = 4, args = 5, payload = 6;
        const int state = 0, privateZero = 1, privateNaN = 2, shared = 3, old = 4, resumed = 5, nullable = 6;
        const int mutate = 1, suspended = 2;
        const int number = 0, text = 1, texts = 2, optionalProcess = 3, optionalNumber = 4,
            stateType = 5, resumedType = 6, resultType = 7;
        const int stateLayout = 0, resumedLayout = 1, resultLayout = 2;

        return new(
            [new NumberValue(0), new NumberValue(BitConverter.Int64BitsToDouble(NegativeNaN)),
             new NumberValue(1), new NumberValue(2), new StringValue(child),
             new RangeValue([new StringValue("--child")], TypeRef.String), new StringValue("original argument")],
            [
                new FunctionCode(Entry, 0, 7, [
                    new(OpCode.Constant, zero), new(OpCode.Negate), new(OpCode.InitializeLocal, privateZero),
                    new(OpCode.LoadLocal, privateZero), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, privateZero),
                    new(OpCode.Constant, nan), new(OpCode.Negate), new(OpCode.InitializeLocal, privateNaN),
                    new(OpCode.LoadLocal, privateNaN), new(OpCode.Negate), new(OpCode.StoreLocal, privateNaN),
                    new(OpCode.LoadLocal, privateNaN), new(OpCode.Negate), new(OpCode.StoreLocal, privateNaN),
                    new(OpCode.LoadLocal, privateNaN), new(OpCode.Negate), new(OpCode.StoreLocal, privateNaN),
                    new(OpCode.LoadLocal, privateZero), new(OpCode.InitializeLocal, shared),
                    new(OpCode.LoadCell, shared, number + 1), new(OpCode.MakeClosure, mutate, 1), new(OpCode.Call, 0, mutate + 1),
                    new(OpCode.InitializeLocal, old),
                    new(OpCode.Null), new(OpCode.NewScope, stateLayout), new(OpCode.InitializeLocal, state),
                    new(OpCode.Constant, path), new(OpCode.Constant, args), new(OpCode.MakeClosure, suspended, 2),
                    new(OpCode.LoadLocal, privateZero), new(OpCode.LoadLocal, state), new(OpCode.LoadLocal, privateNaN),
                    new(OpCode.Call, 3, suspended + 1),
                    new(OpCode.InitializeLocal, resumed),
                    new(OpCode.LoadLocal, state), new(OpCode.LoadField),
                    new(OpCode.Constant, payload), new(OpCode.NativeCall, 2, Text: "process.write_stdin"), new(OpCode.Pop),
                    new(OpCode.LoadLocal, state), new(OpCode.LoadField),
                    new(OpCode.NativeCall, 1, Text: "process.close_stdin"), new(OpCode.Pop),
                    new(OpCode.LoadLocal, state), new(OpCode.LoadField),
                    new(OpCode.NativeCall, 1, Text: "process.wait"), new(OpCode.NativeCall, 1, Text: "process.stdout"),
                    new(OpCode.LoadLocal, privateZero), new(OpCode.Negate), new(OpCode.InitializeLocal, nullable),
                    new(OpCode.Null), new(OpCode.StoreLocal, nullable),
                    new(OpCode.LoadLocal, privateZero), new(OpCode.LoadStorage, privateNaN), new(OpCode.LoadLocal, old),
                    new(OpCode.LoadLocal, shared), new(OpCode.LoadLocal, resumed), new(OpCode.LoadLocal, nullable),
                    new(OpCode.NewScope, resultLayout), new(OpCode.Return),
                ], TypeRef.Scope) { CodeSignature = new([], resultType, []) },
                new FunctionCode(Capture, 0, 2, [
                    new(OpCode.LoadLocal, 0),
                    new(OpCode.Constant, one), new(OpCode.Constant, two), new(OpCode.Add), new(OpCode.InitializeLocal, 1),
                    new(OpCode.LoadLocal, 1), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, 1),
                    new(OpCode.LoadLocal, 1), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, 1),
                    new(OpCode.LoadLocal, 1), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, 1),
                    new(OpCode.LoadLocal, 1), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, 0),
                    new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 1) { CodeSignature = new([], number, [new(number, true)]) },
                new FunctionCode(Detached, 3, 5, [
                    // The numeric parameter replacement detaches before an external suspension.
                    new(OpCode.LoadLocal, 2), new(OpCode.Negate), new(OpCode.StoreLocal, 2),
                    new(OpCode.LoadLocal, 2), new(OpCode.Negate), new(OpCode.StoreLocal, 2),
                    new(OpCode.LoadLocal, 2), new(OpCode.Negate), new(OpCode.StoreLocal, 2),
                    new(OpCode.LoadLocal, 2), new(OpCode.Constant, one), new(OpCode.Multiply), new(OpCode.StoreLocal, 2),
                    new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1),
                    new(OpCode.NativeCall, 2, Text: "process.spawn"), new(OpCode.StoreField), new(OpCode.Pop),
                    new(OpCode.Null), new(OpCode.InitializeLocal, 3),
                    new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Pop),
                    new(OpCode.LoadLocal, 2), new(OpCode.LoadStorage, 4),
                    new(OpCode.NewScope, resumedLayout), new(OpCode.Return),
                ], TypeRef.Scope, CaptureCount: 2, ParameterTypes: [TypeRef.Number, TypeRef.Scope, TypeRef.Number])
                { CodeSignature = new([number, stateType, number], resumedType, [new(text, false), new(texts, false)]) },
            ], OptimizationLevel: 0)
        {
            CodeTypes = [new(TypeKind.Number), new(TypeKind.String), new(TypeKind.Range, Element: text),
                new(TypeKind.Process | TypeKind.Null), new(TypeKind.Number | TypeKind.Null),
                new(TypeKind.Scope, ScopeLayouts: [stateLayout]), new(TypeKind.Scope, ScopeLayouts: [resumedLayout]),
                new(TypeKind.Scope, ScopeLayouts: [resultLayout])],
            ScopeLayouts = [
                new([new("child", optionalProcess)]), new([new("updated", number), new("untouched", number)]),
                new([new("echo", text), new("zero", number), new("nan", number), new("before", number), new("shared", number),
                    new("resumed", resumedType), new("nullable", optionalNumber)])
            ],
            FieldProjections = [new("child", [new(stateLayout, 0)])]
        };
    }

    internal static Module NumericExit(int iterations) => new(
        [new NumberValue(0), new NumberValue(1), new NumberValue(iterations),
         new NumberValue(BitConverter.Int64BitsToDouble(NegativeNaN))],
        [new FunctionCode(Loop, 0, 3,
            [new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.Add), new(OpCode.InitializeLocal, 0),
             new(OpCode.Constant, 0), new(OpCode.Negate), new(OpCode.InitializeLocal, 1),
             new(OpCode.Constant, 3), new(OpCode.InitializeLocal, 2),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.Less), new(OpCode.JumpIfFalse, 24),
             new(OpCode.LoadLocal, 1), new(OpCode.Negate), new(OpCode.StoreLocal, 1),
             new(OpCode.LoadLocal, 2), new(OpCode.Negate), new(OpCode.StoreLocal, 2),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.StoreLocal, 0),
             new(OpCode.Jump, 9), new(OpCode.LoadStorage, 0),
             new(OpCode.LoadLocal, 1), new(OpCode.LoadStorage, 2),
             new(OpCode.NewScope), new(OpCode.Return)],
            TypeRef.Scope) { CodeSignature = new([], 1, []) }], OptimizationLevel: 0)
    {
        CodeTypes = [new(TypeKind.Number), new(TypeKind.Scope, ScopeLayouts: [0])],
        ScopeLayouts = [new([new("steps", 0), new("value", 0), new("nan", 0)])]
    };
}
