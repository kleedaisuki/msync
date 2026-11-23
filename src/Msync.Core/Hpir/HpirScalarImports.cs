namespace Msync.Core.Hpir;

public enum HpirMathOp : byte
{
    Invalid,
    Abs, Floor, Ceil, Trunc, Round, RoundEven, Sqrt, Cbrt, Exp, Exp2, ExpM1, Log, Log2, Log10, LogP1,
    Sin, Cos, Tan, Asin, Acos, Atan, Sinh, Cosh, Tanh, Asinh, Acosh, Atanh,
    IsFinite, IsNaN, IsInfinite, IsInteger, IsSafeInteger, SignBit,
    Divide, MinNumber, MaxNumber, Pow, Atan2, Hypot, FMod, Remainder, CopySign, NextAfter,
    FusedMultiplyAdd, Clamp
}

public enum HpirTextOutput : byte { Print, PrintLine, Error, ErrorLine, Flush, FlushError }

public static class HpirScalarImports
{
    public const int TextElementBytes = sizeof(char);
    public const int TextHeaderBytes = sizeof(int);

    private static readonly string[] mathNames =
    [
        "", "abs", "floor", "ceil", "trunc", "round", "round_even", "sqrt", "cbrt", "exp", "exp2", "expm1", "log", "log2",
        "log10", "log1p", "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
        "is_finite", "is_nan", "is_infinite", "is_integer", "is_safe_integer", "signbit",
        "divide", "fmin", "fmax", "pow", "atan2", "hypot", "fmod", "remainder", "copysign", "nextafter", "fma", "clamp"
    ];

    public static bool TryMath(ReadOnlySpan<char> name, out HpirMathOp operation) =>
        MathBindings.Operations.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out operation);

    public static string MathName(HpirMathOp operation) => mathNames[(int)operation];

    public static HpirImport MathCall(HpirMathOp operation)
    {
        (int count, HpirWidth result) = MathShape(operation);
        if (count == 0) throw new ArgumentOutOfRangeException(nameof(operation));
        var arguments = new HpirWidth[count];
        Array.Fill(arguments, HpirWidth.U64);
        return new(HpirService.MathF64, (int)operation, result, arguments);
    }

    public static HpirImport NumberTextCall(int layout) => new(HpirService.FormatF64, layout, HpirWidth.U64, [HpirWidth.U64]);

    public static HpirImport ParseCall(HpirService service) => service switch
    {
        HpirService.ParseF64 or HpirService.ParsePathF64 => new(service, 0, HpirWidth.U64, [HpirWidth.U64]),
        HpirService.ParseBool or HpirService.ParsePathBool => new(service, 0, HpirWidth.U8, [HpirWidth.U64]),
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    public static HpirImport OutputCall(HpirTextOutput output) => new(HpirService.WriteText, (int)output,
        HpirWidth.Void, FlushOnly(output) ? [] : [HpirWidth.U64]);

    public static bool FlushOnly(HpirTextOutput output) => output is HpirTextOutput.Flush or HpirTextOutput.FlushError;

    public static bool TryTextOutput(string native, out HpirTextOutput output)
    {
        switch (native)
        {
            case "io.print": output = HpirTextOutput.Print; return true;
            case "io.println": output = HpirTextOutput.PrintLine; return true;
            case "io.eprint": output = HpirTextOutput.Error; return true;
            case "io.eprintln": output = HpirTextOutput.ErrorLine; return true;
            case "io.flush": output = HpirTextOutput.Flush; return true;
            case "io.flush_error": output = HpirTextOutput.FlushError; return true;
            default: output = default; return false;
        }
    }

    public static string TextOutputName(HpirTextOutput output) => output switch
    {
        HpirTextOutput.Print => "io.print",
        HpirTextOutput.PrintLine => "io.println",
        HpirTextOutput.Error => "io.eprint",
        HpirTextOutput.ErrorLine => "io.eprintln",
        HpirTextOutput.Flush => "io.flush",
        HpirTextOutput.FlushError => "io.flush_error",
        _ => throw new ArgumentOutOfRangeException(nameof(output))
    };

    internal static bool ValidMath(HpirImport import)
    {
        if ((uint)import.Binding >= (uint)mathNames.Length) return false;
        (int count, HpirWidth result) = MathShape((HpirMathOp)import.Binding);
        return count != 0 && import.Result == result && import.Arguments.Length == count
            && import.Arguments.All(static width => width == HpirWidth.U64);
    }

    internal static bool IsTextLayout(HpirLayout layout) =>
        layout.Size == TextElementBytes && layout.Alignment == TextElementBytes
        && layout.PointerOffsets.Count == 0 && layout.BorrowOffsets.Count == 0;

    internal static (int Count, HpirWidth Result) MathShape(HpirMathOp operation) => operation switch
    {
        >= HpirMathOp.Abs and <= HpirMathOp.Atanh => (1, HpirWidth.U64),
        >= HpirMathOp.IsFinite and <= HpirMathOp.SignBit => (1, HpirWidth.U8),
        >= HpirMathOp.Divide and <= HpirMathOp.NextAfter => (2, HpirWidth.U64),
        HpirMathOp.FusedMultiplyAdd or HpirMathOp.Clamp => (3, HpirWidth.U64),
        _ => (0, HpirWidth.Void)
    };

    private static class MathBindings
    {
        internal static readonly Dictionary<string, HpirMathOp> Operations = Create();

        private static Dictionary<string, HpirMathOp> Create()
        {
            var result = new Dictionary<string, HpirMathOp>(mathNames.Length - 1, StringComparer.Ordinal);
            for (int i = 1; i < mathNames.Length; i++) result.Add(mathNames[i], (HpirMathOp)i);
            return result;
        }
    }
}
