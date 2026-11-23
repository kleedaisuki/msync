namespace Msync.Core.Hpir;

public enum HpirFutureOperation : byte
{
    Ready, HostNumber, HostBool, ProcessExit, ProcessSuccess,
    Add, Subtract, Multiply, Divide, Remainder, Power, Negate,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    Not, BoolEqual, BoolNotEqual,
    TextIsNull, TextParseNumber,
    MathNumber, MathBool, MathTriple, GuestNumber, ScalarIsNull
}

public enum HpirFutureInput : byte { Ready, Deferred, Either }
public enum HpirProcessArgument : byte { Invalid, Text, Path, Number, Bool, PendingText, EitherText }

public static class HpirFutureImports
{
    private const int OperationBits = 5;
    private const int LayoutShift = OperationBits + 4;

    public static HpirLayout Completion(HpirWidth scalar) => Scalar(scalar)
        ? new(32, 8, []) : throw new ArgumentOutOfRangeException(nameof(scalar));

    public static HpirLayout Lifted(HpirFutureOperation operation) => TryShape(operation, out _, out HpirWidth operand, out _)
        ? new(operand == HpirWidth.U64 ? 48 : 40, 8, [0, 8])
        : throw new ArgumentOutOfRangeException(nameof(operation));

    public const int ProcessTextTag = sizeof(ulong);

    public static HpirLayout ProcessHandle() => new(sizeof(int), sizeof(int), []);
    public static HpirLayout ProcessScalarProjection() => new(32, 8, [0]);

    // Each Either operand is a nullable exact-root pointer followed by the
    // independently addressed ready payload. A mixed result writes its ready
    // alternative into an immediate, nonescaping out-parameter and returns null.
    public static HpirImport Lift(HpirFutureOperation operation, HpirFutureInput left, HpirFutureInput right, int layout)
    {
        if ((uint)layout > int.MaxValue >> LayoutShift || !TryShape(operation, out int arity, out var width, out _)
            || !ValidInputs(arity, left, right))
            throw new ArgumentOutOfRangeException(nameof(operation));
        int binding = (layout << LayoutShift) | ((int)right << (OperationBits + 2))
            | ((int)left << OperationBits) | (int)operation;
        return new(HpirService.FutureLift, binding, HpirWidth.U64, LiftArguments(arity, width, left, right));
    }

    public static HpirImport Demand(HpirWidth scalar, HpirFutureInput mode = HpirFutureInput.Deferred)
    {
        if (!Scalar(scalar) || mode is not (HpirFutureInput.Deferred or HpirFutureInput.Either))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return new(HpirService.FutureDemand, (int)mode, scalar,
            mode == HpirFutureInput.Either ? [HpirWidth.U64, scalar] : [HpirWidth.U64]);
    }

    public static HpirImport StartProcess(int layout, bool release, HpirProcessArgument[] argv)
    {
        if ((uint)layout > int.MaxValue >> 1) throw new ArgumentOutOfRangeException(nameof(layout));
        var arguments = new HpirWidth[checked(argv.Length + 1)];
        arguments[0] = HpirWidth.U64;
        for (int i = 0; i < argv.Length; ++i) arguments[i + 1] = ProcessWidth(argv[i]);
        return new(HpirService.ProcessStart, (layout << 1) | (release ? 1 : 0), HpirWidth.U64, arguments)
        { ProcessArguments = argv };
    }

    private static HpirWidth ProcessWidth(HpirProcessArgument argument) => argument switch
    {
        HpirProcessArgument.Text or HpirProcessArgument.Path or HpirProcessArgument.Number
            or HpirProcessArgument.PendingText or HpirProcessArgument.EitherText => HpirWidth.U64,
        HpirProcessArgument.Bool => HpirWidth.U8,
        _ => HpirWidth.Void
    };

    public static HpirImport ProcessExit(int layout) => new(HpirService.ProcessExit, layout, HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport ProcessSuccess(int layout) => new(HpirService.ProcessSuccess, layout, HpirWidth.U64, [HpirWidth.U64]);

    public static (HpirFutureOperation Operation, HpirFutureInput Left, HpirFutureInput Right, int Layout) DecodeLift(int binding) =>
        ((HpirFutureOperation)(binding & ((1 << OperationBits) - 1)), (HpirFutureInput)((binding >> OperationBits) & 3),
            (HpirFutureInput)((binding >> (OperationBits + 2)) & 3), binding >> LayoutShift);

    public static bool IsMixedResult(HpirFutureInput left, HpirFutureInput right) =>
        left != HpirFutureInput.Deferred && right != HpirFutureInput.Deferred;

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        switch (import.Service)
        {
            case HpirService.FutureLift:
                var (operation, left, right, layout) = DecodeLift(import.Binding);
                return TryShape(operation, out int arity, out var width, out _) && ValidInputs(arity, left, right)
                    && import.Result == HpirWidth.U64 && IsLayout(program, layout, Lifted(operation))
                    && import.Arguments.AsSpan().SequenceEqual(LiftArguments(arity, width, left, right));
            case HpirService.FutureDemand:
                if (!Scalar(import.Result)) return false;
                return import.Binding switch
                {
                    (int)HpirFutureInput.Deferred => import.Arguments is [HpirWidth.U64],
                    (int)HpirFutureInput.Either => import.Arguments is [HpirWidth.U64, var value] && value == import.Result,
                    _ => false
                };
            case HpirService.ProcessStart:
                if (import.Result != HpirWidth.U64 || import.Arguments.Length - 1 != import.ProcessArguments.Length
                    || import.Arguments[0] != HpirWidth.U64 || !ValidProcessHandle(program, import.Binding >> 1))
                    return false;
                for (int i = 0; i < import.ProcessArguments.Length; ++i)
                {
                    var argumentWidth = ProcessWidth(import.ProcessArguments[i]);
                    if (argumentWidth == HpirWidth.Void || argumentWidth != import.Arguments[i + 1]) return false;
                }
                return true;
            case HpirService.ProcessExit or HpirService.ProcessSuccess:
                return import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64]
                    && IsLayout(program, import.Binding, ProcessScalarProjection());
            default:
                return false;
        }
    }

    internal static bool ValidProcessHandle(HpirProgram program, int layout) => IsLayout(program, layout, ProcessHandle());

    public static bool TryShape(HpirFutureOperation operation, out int arity, out HpirWidth input, out HpirWidth payload)
    {
        (arity, input, payload) = operation switch
        {
            >= HpirFutureOperation.Add and <= HpirFutureOperation.Power => (2, HpirWidth.U64, HpirWidth.U64),
            HpirFutureOperation.Negate => (1, HpirWidth.U64, HpirWidth.U64),
            >= HpirFutureOperation.Equal and <= HpirFutureOperation.GreaterEqual => (2, HpirWidth.U64, HpirWidth.U8),
            HpirFutureOperation.Not or HpirFutureOperation.ScalarIsNull => (1, HpirWidth.U8, HpirWidth.U8),
            HpirFutureOperation.BoolEqual or HpirFutureOperation.BoolNotEqual => (2, HpirWidth.U8, HpirWidth.U8),
            _ => (0, HpirWidth.Void, HpirWidth.Void)
        };
        return arity != 0;
    }

    private static bool Scalar(HpirWidth width) => width is HpirWidth.U64 or HpirWidth.U8;

    private static bool ValidInputs(int arity, HpirFutureInput left, HpirFutureInput right) =>
        (uint)left <= (uint)HpirFutureInput.Either && (uint)right <= (uint)HpirFutureInput.Either
        && (arity == 2 || right == HpirFutureInput.Ready)
        && (left != HpirFutureInput.Ready || right != HpirFutureInput.Ready);

    private static HpirWidth[] LiftArguments(int arity, HpirWidth scalar, HpirFutureInput left, HpirFutureInput right)
    {
        var arguments = new List<HpirWidth>(5);
        if (IsMixedResult(left, right)) arguments.Add(HpirWidth.U64);
        Add(left);
        if (arity == 2) Add(right);
        return arguments.ToArray();

        void Add(HpirFutureInput mode)
        {
            if (mode != HpirFutureInput.Ready) arguments.Add(HpirWidth.U64);
            if (mode != HpirFutureInput.Deferred) arguments.Add(scalar);
        }
    }

    private static bool IsLayout(HpirProgram program, int index, HpirLayout expected)
    {
        if ((uint)index >= (uint)program.Layouts.Count) return false;
        HpirLayout found = program.Layouts[index];
        return found.Size == expected.Size && found.Alignment == expected.Alignment
            && found.PointerOffsets.SequenceEqual(expected.PointerOffsets) && found.BorrowOffsets.Count == 0;
    }
}
