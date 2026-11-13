using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using static RuntimeFixture;

internal static class InlineNativeChecks
{
    private static readonly SourceSpan Origin = new("inline-native.msync", 11, 4);
    internal static async Task RunAsync()
    {
        // The old stack inline/vector-owner fast path is retired; exercise typed fields and real native boundaries.
        await Number("let row = scope { value: 0 }; row::value = $std::math::floor(41.75); $std::ranges::at([$std::ranges::front([row::value])], 0) + 1", 42);
        foreach (double index in new[] { -1d, double.PositiveInfinity, 1d, .5d })
        {
            NativeProgram program = At(index);
            try { await Run(Reload(program)); throw new InvalidOperationException("Invalid ranges.at index accepted: " + index); }
            catch (MsyncException error)
            {
                Check(error.Span == Origin, "Native failure lost original source span: " + error);
                Check(error.ToString().Contains("index", StringComparison.OrdinalIgnoreCase)
                    || error.ToString().Contains("count", StringComparison.OrdinalIgnoreCase)
                    || error.ToString().Contains("finite", StringComparison.OrdinalIgnoreCase), "Native index failure lost actionable detail: " + error);
            }
        }
        int yielded = 0;
        async IAsyncEnumerable<NativeValue> Lazy()
        {
            yielded++; yield return NativeValue.FromNumber(17); await Task.CompletedTask;
        }
        NativeRuntimeContext context = new();
        NativeRange range = new(Lazy(), TypeRef.Number);
        NativeValue selected = await NativeBuiltins.InvokeAsync("ranges.front", [NativeValue.FromRange(range)], context, (_, _, _) => throw new InvalidOperationException("Unexpected source callback"));
        Check(selected.AsNumber() == 17 && yielded == 1, "Unowned range did not select producer exactly once");
        Console.WriteLine("PASS typed native scalar/range operations and diagnostic boundaries");
    }
    private static NativeProgram At(double index) => new([new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: index)],
        [new("range-index", [new(0), new(1), new(0), new(0)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeRange, 1, Type: 1, Inputs: [0]), new(NativeOp.Constant, 2, 1),
             new(NativeOp.CallNative, 3, Symbol: "ranges.at", Inputs: [1, 2], Source: Origin), new(NativeOp.Return, A: 3)], new([], 0, []))],
        [new(TypeKind.Number), new(TypeKind.Range, Element: 0)], []);
}
