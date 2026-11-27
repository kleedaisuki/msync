using System.Globalization;
using System.Numerics;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

internal static class Word32Checks
{
    private const long Modulus = 1L << 32;
    private static readonly string[] Operations = ["bit_and", "bit_xor", "shift_right", "rotate_right"];
    private const string Source = """
        fn next_number() -> Number {
            let line? = $std::io::read_line();
            if line is null { 0 } else { line as Number }
        };
        let left = next_number();
        let right = next_number();
        let count = next_number();
        object {
            and_value: $std::bits::bit_and(left, right),
            xor_value: $std::bits::bit_xor(left, right),
            shifted: $std::bits::shift_right(left, count),
            rotated: $std::bits::rotate_right(left, count),
        }
        """;
    private const string SourceName = "word32";
    private static readonly string[] Fields = ["and_value", "xor_value", "shifted", "rotated"];

    internal static async Task NativeAsync(Func<string, double[], ValueTask<Value>> invoke, Action<bool, string> check)
    {
        foreach (var (left, right, count) in Vectors())
        {
            var expected = Oracle(left, right, count);
            for (int i = 0; i < Operations.Length; i++)
            {
                var result = await invoke(Operations[i], [left, i < 2 ? right : count]);
                check(Same(result, expected[i]), $"{Operations[i]} disagreed with the word oracle");
            }
        }
        check(Same(await invoke("bit_xor", [-0d, 0d]), 0), "Word32 materializes positive zero");
        double[] invalid = [-1d, 0.5, Modulus, double.NaN, double.NegativeInfinity, double.PositiveInfinity];
        foreach (string operation in Operations)
            foreach (double input in invalid)
                await Reject(() => invoke(operation, [input, 0]), "Word32 operand", check);
        foreach (string operation in Operations.Take(2))
            foreach (double input in invalid)
                await Reject(() => invoke(operation, [0, input]), "Word32 operand", check);
        foreach (string operation in Operations.Skip(2))
            foreach (double input in new[] { -1d, 0.5, 32d, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
                await Reject(() => invoke(operation, [1, input]), "Word32 shift count", check);
    }

    internal static async Task ArtifactsAsync(Action<bool, string> check)
    {
        var binding = new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module portable = RoundTrip(Frontend.Compile(Source, SourceName, level));
            foreach (Module image in new[] { portable, RoundTrip(Binding.Bind(portable, binding)) })
            {
                foreach (var (left, right, count) in Vectors().Take(4))
                {
                    var profile = new RuntimeProfile();
                    var result = (ScopeValue)await Run(image, left, right, count, profile);
                    var expected = Oracle(left, right, count);
                    for (int i = 0; i < Fields.Length; i++)
                        check(Same(result[Fields[i]], expected[i]), $"{image.Stage}/O{level} changed {Fields[i]}");
                    var counts = profile.Drain().Where(sample => sample.Name == "vm.native").ToArray();
                    foreach (string operation in Operations)
                        check(counts.Any(sample => sample.Attributes["native.name"] == "bits." + operation
                            && sample.Metrics["calls"] == 1 && sample.Metrics["fast_calls"] == 1),
                            $"{image.Stage}/O{level} did not keep {operation} on the raw stack");
                }
                await Reject(() => Run(image, -1, 0, 0), "Word32 operand", check, "bit_and");
                await Reject(() => Run(image, 1, 0, 32), "Word32 shift count", check, "shift_right");
            }
        }
    }

    private static ValueTask<Value> Run(Module image, double left, double right, int count, RuntimeProfile? profile = null)
    {
        var input = FormattableString.Invariant($"{left:R}\n{right:R}\n{count}\n");
        return Interpreter.ExecuteAsync(image, new(WorkingDirectory: Environment.CurrentDirectory)
        {
            Input = new StringReader(input),
            Profile = profile
        });
    }

    private static async Task Reject(Func<ValueTask<Value>> run, string message, Action<bool, string> check, string? call = null)
    {
        try { await run(); }
        catch (MsyncException error)
        {
            check(error.Message.StartsWith(message, StringComparison.Ordinal), $"Unexpected Word32 diagnostic: {error.Message}");
            if (call is not null)
                check(error.Span is { Source: SourceName } span
                    && Source.AsSpan(span.Start, span.Length).Contains(call.AsSpan(), StringComparison.Ordinal),
                    $"Word32 diagnostic lost the source call: {call}");
            return;
        }
        check(false, $"Expected {message} diagnostic");
    }

    private static IEnumerable<(long Left, long Right, int Count)> Vectors()
    {
        yield return (0, Modulus - 1, 0);
        yield return (Modulus - 1, Modulus - 1, 31);
        yield return (1, 0, 1);
        yield return (1L << 31, 1, 31);
        Random random = new(4491);
        for (int i = 0; i < 64; i++)
            yield return (random.NextInt64(Modulus), random.NextInt64(Modulus), random.Next(32));
    }

    private static double[] Oracle(long left, long right, int count)
    {
        BigInteger value = left;
        BigInteger divisor = BigInteger.Pow(2, count);
        BigInteger quotient = BigInteger.DivRem(value, divisor, out var remainder);
        return [(double)(value & right), (double)(value ^ right), (double)quotient,
            (double)(quotient + remainder * (Modulus / divisor))];
    }

    private static bool Same(Value value, double expected) => value is NumberValue number
        && BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected);

    private static Module RoundTrip(Module image)
    {
        using MemoryStream stream = new();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
