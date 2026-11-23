namespace Msync.Core.Hpir;

public readonly record struct HpirFutureMathBinding(HpirMathOp Operation, HpirFutureInput First,
    HpirFutureInput Second, HpirFutureInput Third, int Layout)
{
    public HpirFutureInput Mode(int index) => index == 0 ? First : index == 1 ? Second : Third;
    public bool Mixed => First != HpirFutureInput.Deferred && Second != HpirFutureInput.Deferred
        && Third != HpirFutureInput.Deferred;
}

public static class HpirFutureMathImports
{
    private const int OperationBits = 6;
    private const int LayoutShift = OperationBits + 6;

    public static (int Arity, HpirWidth Payload) Shape(HpirMathOp operation) => HpirScalarImports.MathShape(operation);

    public static HpirLayout Layout(int arity) => arity == 3 ? new(72, 8, [0, 8, 32]) : new(56, 8, [0, 8]);

    public static HpirFutureMathBinding Decode(int binding) => new(
        (HpirMathOp)(binding & ((1 << OperationBits) - 1)),
        (HpirFutureInput)((binding >> OperationBits) & 3),
        (HpirFutureInput)((binding >> (OperationBits + 2)) & 3),
        (HpirFutureInput)((binding >> (OperationBits + 4)) & 3),
        binding >> LayoutShift);

    public static HpirImport Lift(HpirFutureMathBinding binding)
    {
        var (arity, _) = Shape(binding.Operation);
        if ((uint)binding.Layout > int.MaxValue >> LayoutShift || !ValidModes(binding, arity))
            throw new ArgumentOutOfRangeException(nameof(binding));
        int encoded = (binding.Layout << LayoutShift) | ((int)binding.Third << (OperationBits + 4))
            | ((int)binding.Second << (OperationBits + 2)) | ((int)binding.First << OperationBits) | (int)binding.Operation;
        return new(HpirService.FutureMath, encoded, HpirWidth.U64, Arguments(binding, arity));
    }

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        var binding = Decode(import.Binding);
        var (arity, _) = Shape(binding.Operation);
        if (import.Result != HpirWidth.U64 || (uint)binding.Layout >= (uint)program.Layouts.Count || !ValidModes(binding, arity))
            return false;
        var found = program.Layouts[binding.Layout];
        var expected = Layout(arity);
        return found.Size == expected.Size && found.Alignment == expected.Alignment
            && found.PointerOffsets.SequenceEqual(expected.PointerOffsets) && found.BorrowOffsets.Count == 0
            && import.Arguments.AsSpan().SequenceEqual(Arguments(binding, arity));
    }

    private static HpirWidth[] Arguments(HpirFutureMathBinding binding, int arity)
    {
        int count = binding.Mixed ? 1 : 0;
        for (int index = 0; index < arity; ++index)
            count += binding.Mode(index) == HpirFutureInput.Either ? 2 : 1;
        var widths = new HpirWidth[count];
        Array.Fill(widths, HpirWidth.U64);
        return widths;
    }

    private static bool ValidModes(HpirFutureMathBinding binding, int arity)
    {
        if (arity == 0) return false;
        bool hasFuture = false;
        for (int index = 0; index < 3; ++index)
        {
            var mode = binding.Mode(index);
            if ((uint)mode > (uint)HpirFutureInput.Either || index >= arity && mode != HpirFutureInput.Ready) return false;
            hasFuture |= mode != HpirFutureInput.Ready;
        }
        return hasFuture;
    }
}
