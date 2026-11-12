using Msync.Core;

internal static partial class Fixtures
{
    internal static readonly string[] FrameOutput = ["98", "7", "20", "25", "18", "-17", "-17", "18", "4", "5", "6", "23", "-24", "-24", "52"];
    internal static readonly string[] ProcessOutput = ["echo:returned", "5", "echo:original-argument", "echo:captured-cell"];
    internal static readonly string[] TailOutput = ["21324.5", "162.75"];
    private const int NumberCode = 0, NullCode = 1, FunctionCodeId = 2, TextCode = 3, TextsCode = 4,
        ProcessCode = 5, OptionalProcess = 6, StateCode = 7, DetachedCode = 8, RawResultCode = 9;
    private const int StateLayout = 0, DetachedLayout = 1, RawResultLayout = 2;
    private const int ChildField = 0, FirstDetachedField = 1, LastDetachedField = 3;
    private static readonly CodeCapture NumberValueCapture = new(NumberCode, false), NumberCell = new(NumberCode, true);

    internal static Module Activations()
    {
        const int global = 0, closure = 1, first = 2, second = 3;
        const int ten = 0, seven = 1, eleven = 2, hundred = 3, three = 4, five = 5, fortyTwo = 6, one = 7, text = 8, template = 9;
        const int read = 1, mutate = 2, replace = 3, promote = 4, readOwned = 5, extra = 6, replacePlain = 7, copied = 8;

        List<Instruction> code =
        [
            new(OpCode.Constant, ten), new(OpCode.InitializeLocal, global),
            new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, read, 2), new(OpCode.InitializeLocal, closure),
            // This live caller operand must not be confused with a capture or argument slot.
            new(OpCode.Constant, hundred), new(OpCode.LoadLocal, closure), new(OpCode.Constant, three), new(OpCode.Call, 1, read + 1), new(OpCode.Add),
            new(OpCode.LoadLocal, closure), new(OpCode.Constant, five), new(OpCode.Call, 1, read + 1), new(OpCode.Subtract),
        ];
        PrintNumber(code, text);
        code.AddRange([new(OpCode.Constant, template), new(OpCode.Constant, three), new(OpCode.Call, 1, read + 1)]);
        PrintNumber(code, text);
        code.AddRange([
            new(OpCode.LoadCell, global, NumberCode + 1), new(OpCode.LoadCell, global, NumberCode + 1), new(OpCode.Constant, seven),
            new(OpCode.MakeClosure, mutate, 3), new(OpCode.InitializeLocal, closure),
        ]);
        InvokeAndPrint(code, closure, three, mutate, text);
        InvokeAndPrint(code, closure, five, mutate, text);
        code.Add(new(OpCode.LoadLocal, global));
        PrintNumber(code, text);
        code.AddRange([
            new(OpCode.LoadCell, global, NumberCode + 1), new(OpCode.Constant, seven),
            new(OpCode.MakeClosure, replace, 2), new(OpCode.InitializeLocal, closure),
        ]);
        for (int i = 0; i < 2; i++)
        {
            code.AddRange([new(OpCode.LoadLocal, closure), new(OpCode.Call, 0, replace + 1)]);
            PrintNumber(code, text);
        }
        code.Add(new(OpCode.LoadLocal, global));
        PrintNumber(code, text);
        code.AddRange([
            new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, promote, 2), new(OpCode.InitializeLocal, closure),
            new(OpCode.LoadLocal, closure), new(OpCode.Constant, three), new(OpCode.Call, 1, promote + 1), new(OpCode.InitializeLocal, first),
            new(OpCode.LoadLocal, closure), new(OpCode.Constant, five), new(OpCode.Call, 1, promote + 1), new(OpCode.InitializeLocal, second),
        ]);
        foreach (int slot in new[] { first, first, second })
        {
            code.AddRange([new(OpCode.LoadLocal, slot), new(OpCode.Call, 0, readOwned + 1)]);
            PrintNumber(code, text);
        }
        code.AddRange([
            new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, extra, 2),
            new(OpCode.Constant, ten), new(OpCode.Constant, five), new(OpCode.Subtract),
            new(OpCode.Call, 1, extra + 1),
        ]);
        PrintNumber(code, text);
        code.AddRange([
            new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, replacePlain, 2),
            new(OpCode.InitializeLocal, closure),
        ]);
        for (int i = 0; i < 2; i++)
        {
            code.AddRange([new(OpCode.LoadLocal, closure), new(OpCode.Call, 0, replacePlain + 1)]);
            PrintNumber(code, text);
        }
        code.AddRange([
            new(OpCode.Constant, ten), new(OpCode.InitializeLocal, first),
            new(OpCode.LoadCell, first, NumberCode + 1), new(OpCode.InitializeLocal, second),
            new(OpCode.Constant, seven), new(OpCode.Constant, eleven), new(OpCode.MakeClosure, copied, 2),
            // Passing a value read through an alias does not transfer the raw location.
            new(OpCode.LoadLocal, second), new(OpCode.Call, 1, copied + 1),
            new(OpCode.LoadLocal, first), new(OpCode.Add),
        ]);
        PrintNumber(code, text);
        End(code);

        return Typed(new(
            [
                new NumberValue(10), new NumberValue(7), new NumberValue(11), new NumberValue(100),
                new NumberValue(3), new NumberValue(5), new NumberValue(42), new NumberValue(1),
                new TypeValue(TypeRef.String), new FunctionValue(read, [new NumberValue(11), new NumberValue(7)]),
            ],
            [
                new("activation-main", 0, 4, code.ToArray(), TypeRef.Null),
                new("multi-read", 1, 3, [
                    new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Subtract),
                    new(OpCode.LoadLocal, 2), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("write-through", 1, 4, [
                    // Neither retrieving the existing Cell nor writing its payload replaces a slot.
                    new(OpCode.LoadCell, 0, NumberCode + 1), new(OpCode.Pop),
                    new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 3), new(OpCode.Add), new(OpCode.StoreLocal, 0),
                    new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 3, ParameterTypes: [TypeRef.Number]),
                new("replace-capture", 0, 2, [
                    new(OpCode.LoadLocal, 0), new(OpCode.Constant, fortyTwo), new(OpCode.InitializeLocal, 0),
                    new(OpCode.LoadLocal, 0), new(OpCode.Subtract), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2),
                new("promote-argument", 1, 3, [
                    new(OpCode.LoadCell, 2, NumberCode + 1), new(OpCode.MakeClosure, readOwned, 1), new(OpCode.Return),
                ], TypeRef.Function, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("read-owned", 0, 1, [
                    new(OpCode.LoadLocal, 0), new(OpCode.Constant, one), new(OpCode.Add), new(OpCode.StoreLocal, 0),
                    new(OpCode.LoadLocal, 0), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 1),
                new("extra-local", 1, 4, [
                    new(OpCode.LoadLocal, 2), new(OpCode.InitializeLocal, 3), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1),
                    new(OpCode.Add), new(OpCode.LoadLocal, 3), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("replace-plain-capture", 0, 2, [
                    new(OpCode.LoadLocal, 0), new(OpCode.Constant, fortyTwo), new(OpCode.StoreLocal, 0),
                    new(OpCode.LoadLocal, 0), new(OpCode.Subtract), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2),
                new("copied-cell-argument", 1, 3, [
                    new(OpCode.Constant, fortyTwo), new(OpCode.StoreLocal, 2), new(OpCode.LoadLocal, 2), new(OpCode.Return),
                ], TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
            ], OptimizationLevel: 0),
            new([], NullCode, []),
            new([NumberCode], NumberCode, [NumberValueCapture, NumberValueCapture]),
            new([NumberCode], NumberCode, [NumberCell, NumberCell, NumberValueCapture]),
            new([], NumberCode, [NumberCell, NumberValueCapture]),
            new([NumberCode], FunctionCodeId, [NumberValueCapture, NumberValueCapture]),
            new([], NumberCode, [NumberCell]),
            new([NumberCode], NumberCode, [NumberValueCapture, NumberValueCapture]),
            new([], NumberCode, [NumberValueCapture, NumberValueCapture]),
            new([NumberCode], NumberCode, [NumberValueCapture, NumberValueCapture]));
    }

    internal static Module Processes(string child)
    {
        const int path = 0, arguments = 1, returned = 2, original = 3, captured = 4, two = 5, three = 6, text = 7;
        const int process = 0, state = 1, cell = 2;
        const int launch = 1, writeArgument = 2, writeCell = 3;
        List<Instruction> code =
        [
            new(OpCode.Constant, path), new(OpCode.Constant, arguments), new(OpCode.MakeClosure, launch, 2),
            new(OpCode.Constant, returned), new(OpCode.Call, 1, launch + 1), new(OpCode.InitializeLocal, process),
        ];
        Echo(code, process, returned);
        code.AddRange([
            new(OpCode.Null), new(OpCode.NewScope, StateLayout), new(OpCode.InitializeLocal, state),
            new(OpCode.Constant, path), new(OpCode.Constant, arguments), new(OpCode.MakeClosure, writeArgument, 2),
            // A raw Number comes before the original Scope in the borrowed argument slice.
            new(OpCode.Constant, two), new(OpCode.Constant, three), new(OpCode.Add),
            new(OpCode.LoadLocal, state), new(OpCode.Call, 2, writeArgument + 1), new(OpCode.Pop),
            new(OpCode.LoadLocal, state), new(OpCode.LoadField, ChildField), new(OpCode.InitializeLocal, process),
        ]);
        Echo(code, process, original);
        code.AddRange([
            new(OpCode.Null), new(OpCode.InitializeLocal, cell), new(OpCode.LoadCell, cell, OptionalProcess + 1),
            new(OpCode.Constant, path), new(OpCode.Constant, arguments), new(OpCode.MakeClosure, writeCell, 3),
            new(OpCode.Call, 0, writeCell + 1), new(OpCode.Pop), new(OpCode.LoadLocal, cell), new(OpCode.InitializeLocal, process),
        ]);
        Echo(code, process, captured);
        End(code);
        return Typed(new(
            [
                new StringValue(child),
                new RangeValue([new StringValue("--child")], TypeRef.String),
                new StringValue("returned"), new StringValue("original-argument"), new StringValue("captured-cell"),
                new NumberValue(2), new NumberValue(3), new TypeValue(TypeRef.String),
            ],
            [
                new("process-main", 0, 3, code.ToArray(), TypeRef.Null),
                new("return-process", 1, 3, [
                    new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 2, Text: "process.spawn"),
                    new(OpCode.Return),
                ], TypeRef.Process, CaptureCount: 2, ParameterTypes: [TypeRef.String]),
                new("write-original-argument", 2, 4, [
                    new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1),
                    new(OpCode.NativeCall, 2, Text: "process.spawn"), new(OpCode.StoreField, ChildField), new(OpCode.Pop),
                    // Caller roots include the original Scope, not the rebound parameter.
                    new(OpCode.Null), new(OpCode.InitializeLocal, 3),
                    // The other argument's raw value remains valid after detachment.
                    new(OpCode.LoadLocal, 2), new(OpCode.Constant, text), new(OpCode.NativeCall, 2, Text: "cast"),
                    new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop),
                    new(OpCode.Null), new(OpCode.Return),
                ], TypeRef.Null, CaptureCount: 2, ParameterTypes: [TypeRef.Number, TypeRef.Scope]),
                new("write-captured-cell", 0, 3, [
                    new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.NativeCall, 2, Text: "process.spawn"),
                    new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return),
                ], TypeRef.Null, CaptureCount: 3),
            ], OptimizationLevel: 0),
            new([], NullCode, []),
            new([TextCode], ProcessCode, [new(TextCode, false), new(TextsCode, false)]),
            new([NumberCode, StateCode], NullCode, [new(TextCode, false), new(TextsCode, false)]),
            new([], NullCode, [new(OptionalProcess, true), new(TextCode, false), new(TextsCode, false)]));
    }

    internal static Module EffectTails()
    {
        const int state = 0, bias = 1, reader = 2, writer = 3, index = 4, score = 5, committer = 6;
        const int two = 0, three = 1, zero = 2, count = 3, half = 4, eighth = 5, one = 6, text = 7;
        List<Instruction> code =
        [
            new(OpCode.Constant, two), new(OpCode.InitializeLocal, state),
            new(OpCode.Constant, three), new(OpCode.InitializeLocal, bias),
            new(OpCode.LoadCell, state, NumberCode + 1), new(OpCode.LoadCell, bias, NumberCode + 1), new(OpCode.MakeClosure, 1, 2), new(OpCode.InitializeLocal, reader),
            new(OpCode.LoadCell, state, NumberCode + 1), new(OpCode.LoadCell, bias, NumberCode + 1), new(OpCode.MakeClosure, 2, 2), new(OpCode.InitializeLocal, writer),
            new(OpCode.LoadCell, state, NumberCode + 1), new(OpCode.LoadCell, bias, NumberCode + 1), new(OpCode.MakeClosure, 3, 2), new(OpCode.InitializeLocal, committer),
            new(OpCode.Constant, zero), new(OpCode.InitializeLocal, index), new(OpCode.Constant, zero), new(OpCode.InitializeLocal, score),
        ];
        int header = code.Count;
        code.AddRange([new(OpCode.LoadLocal, index), new(OpCode.Constant, count), new(OpCode.Less)]);
        int exit = code.Count;
        code.Add(new(OpCode.JumpIfFalse));
        code.AddRange([
            new(OpCode.LoadLocal, score), new(OpCode.LoadLocal, reader), new(OpCode.LoadLocal, index),
            new(OpCode.Constant, zero), new(OpCode.Add), new(OpCode.Call, 1, 2),
            new(OpCode.Add), new(OpCode.StoreLocal, score),
            new(OpCode.LoadLocal, score), new(OpCode.LoadLocal, committer), new(OpCode.LoadLocal, index),
            new(OpCode.Constant, zero), new(OpCode.Add), new(OpCode.Call, 1, 4),
            new(OpCode.Add), new(OpCode.StoreLocal, score),
            new(OpCode.LoadLocal, writer), new(OpCode.LoadLocal, index), new(OpCode.Constant, zero), new(OpCode.Add),
            new(OpCode.Call, 1, 3), new(OpCode.Pop),
            new(OpCode.LoadLocal, index), new(OpCode.Constant, one), new(OpCode.Add), new(OpCode.StoreLocal, index), new(OpCode.Jump, header),
        ]);
        code[exit] = code[exit] with { A = code.Count };
        code.Add(new(OpCode.LoadLocal, score));
        PrintNumber(code, text);
        code.Add(new(OpCode.LoadLocal, state));
        PrintNumber(code, text);
        End(code);
        Instruction[] arithmetic =
        [
            new(OpCode.LoadLocal, 2), new(OpCode.NativeCall, 1, Text: "math.abs"),
            new(OpCode.LoadLocal, 0), new(OpCode.Add), new(OpCode.Constant, half), new(OpCode.Multiply),
            new(OpCode.LoadLocal, 1), new(OpCode.Subtract),
            new(OpCode.LoadLocal, 2), new(OpCode.Constant, eighth), new(OpCode.Multiply), new(OpCode.Add),
        ];
        return Typed(new(
            [
                new NumberValue(2), new NumberValue(3), new NumberValue(0), new NumberValue(137),
                new NumberValue(.5), new NumberValue(.125), new NumberValue(1), new TypeValue(TypeRef.String),
            ],
            [
                new("tail-main", 0, 7, code.ToArray(), TypeRef.Null),
                new("read-captured-tail", 1, 3, [.. arithmetic, new(OpCode.Return)],
                    TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("write-captured-tail", 1, 3, [.. arithmetic, new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Return)],
                    TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
                new("commit-raw-tail", 1, 3, [.. arithmetic, new(OpCode.StoreLocal, 2), new(OpCode.LoadLocal, 2), new(OpCode.Return)],
                    TypeRef.Number, CaptureCount: 2, ParameterTypes: [TypeRef.Number]),
            ], OptimizationLevel: 0), new([], NullCode, []),
            new([NumberCode], NumberCode, [NumberCell, NumberCell]),
            new([NumberCode], NumberCode, [NumberCell, NumberCell]),
            new([NumberCode], NumberCode, [NumberCell, NumberCell]));
    }

    private static void InvokeAndPrint(List<Instruction> code, int closure, int argument, int function, int text)
    {
        code.AddRange([new(OpCode.LoadLocal, closure), new(OpCode.Constant, argument), new(OpCode.Call, 1, function + 1)]);
        PrintNumber(code, text);
    }

    private static void PrintNumber(List<Instruction> code, int text) =>
        code.AddRange([new(OpCode.Constant, text), new(OpCode.NativeCall, 2, Text: "cast"), new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop)]);

    private static void Echo(List<Instruction> code, int process, int message) =>
        code.AddRange([
            new(OpCode.LoadLocal, process), new(OpCode.Constant, message), new(OpCode.NativeCall, 2, Text: "process.write_stdin"), new(OpCode.Pop),
            new(OpCode.LoadLocal, process), new(OpCode.NativeCall, 1, Text: "process.close_stdin"), new(OpCode.Pop),
            new(OpCode.LoadLocal, process), new(OpCode.NativeCall, 1, Text: "process.wait"), new(OpCode.NativeCall, 1, Text: "process.stdout"),
            new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop),
        ]);

    private static void End(List<Instruction> code) => code.AddRange([new(OpCode.Null), new(OpCode.Return)]);

    private static Module Typed(Module image, params CodeSignature[] signatures) => image with
    {
        Functions = image.Functions.Select((body, index) => body with { CodeSignature = signatures[index] }).ToArray(),
        CodeTypes =
        [
            new(TypeKind.Number), new(TypeKind.Null), new(TypeKind.Function), new(TypeKind.String),
            new(TypeKind.Range, Element: TextCode), new(TypeKind.Process), new(TypeKind.Process | TypeKind.Null),
            new(TypeKind.Scope, ScopeLayouts: [StateLayout]), new(TypeKind.Scope, ScopeLayouts: [DetachedLayout]),
            new(TypeKind.Scope, ScopeLayouts: [RawResultLayout])
        ],
        ScopeLayouts =
        [
            new([new("child", OptionalProcess)]), new([new("before", NumberCode), new("after", NumberCode), new("nan", NumberCode)]),
            new([new("zero", NumberCode), new("nan", NumberCode), new("detached", DetachedCode),
                new("promoted", NumberCode), new("resumed", NumberCode)])
        ],
        FieldProjections =
        [
            new("child", [new(StateLayout, 0)]), new("before", [new(DetachedLayout, 0)]),
            new("after", [new(DetachedLayout, 1)]), new("nan", [new(DetachedLayout, 2)])
        ]
    };
}
