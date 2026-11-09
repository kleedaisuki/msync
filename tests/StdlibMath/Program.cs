using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

RuntimeContext context = new(new(WorkingDirectory: Environment.CurrentDirectory));
ValueTask<Value> Native(string name, params double[] numbers) => NativeRegistry.InvokeAsync("math." + name,
    numbers.Select(number => (Value)new NumberValue(number)).ToArray(), context,
    (_, _, _) => throw new InvalidOperationException("Scalar math must not invoke callbacks"));
async Task<double> Number(string name, params double[] arguments) => ((NumberValue)await Native(name, arguments)).Data;
async Task Exact(string name, double expected, params double[] arguments)
{
    double actual = await Number(name, arguments);
    Check(BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected),
        $"math.{name}: expected {expected:R} ({BitConverter.DoubleToInt64Bits(expected):x16}), got {actual:R} ({BitConverter.DoubleToInt64Bits(actual):x16})");
}
async Task Near(string name, double expected, params double[] arguments)
{
    double actual = await Number(name, arguments);
    Check(Math.Abs(actual - expected) <= Math.Abs(expected) * 1e-14 + 1e-15,
        $"math.{name}: expected approximately {expected:R}, got {actual:R}");
}

await Exact("abs", 0, -0.0);
await Exact("abs", 4, -4);
await Exact("floor", -3, -2.25);
await Exact("ceil", -2, -2.25);
await Exact("trunc", -2, -2.25);
await Exact("round", 3, 2.5);
await Exact("round", -3, -2.5);
await Exact("round_even", 2, 2.5);
await Exact("round_even", -2, -2.5);
await Exact("round_even", 4, 3.5);
await Exact("sqrt", 3, 9);
await Near("cbrt", -3, -27);
await Exact("divide", double.PositiveInfinity, 1, 0);
await Exact("divide", double.NegativeInfinity, 1, -0.0);
Check(double.IsNaN(await Number("divide", 0, 0)), "zero divided by zero is not NaN");
Check(double.IsNaN(await Number("sqrt", -1)), "sqrt domain must return NaN, not throw");
await Exact("log", double.NegativeInfinity, 0);
await Exact("exp", 1, 0);
await Exact("exp2", 8, 3);
await Exact("log2", 3, 8);
await Exact("log10", 2, 100);
await Near("expm1", 1e-20, 1e-20);
await Near("log1p", 1e-20, 1e-20);
await Exact("sin", 0, 0);
await Exact("cos", 1, 0);
await Exact("tan", 0, 0);
await Exact("asin", 0, 0);
await Exact("acos", 0, 1);
await Exact("atan", 0, 0);
await Exact("sinh", 0, 0);
await Exact("cosh", 1, 0);
await Exact("tanh", 0, 0);
await Exact("asinh", 0, 0);
await Exact("acosh", 0, 1);
await Exact("atanh", 0, 0);
await Exact("fmin", 5, double.NaN, 5);
await Exact("fmax", 5, 5, double.NaN);
await Exact("fmin", -0.0, 0, -0.0);
await Exact("fmin", -0.0, -0.0, 0);
await Exact("fmax", 0, -0.0, 0);
await Exact("fmax", 0, 0, -0.0);
Check(double.IsNaN(await Number("fmin", double.NaN, double.NaN)), "fmin of two NaNs must be NaN");
await Exact("pow", 8, 2, 3);
await Exact("atan2", -0.0, -0.0, 1);
await Exact("hypot", 5, 3, 4);
Check(double.IsFinite(await Number("hypot", 1e308, 1e308)), "hypot overflowed despite a finite mathematical result");
await Exact("fmod", -1, -7, 3);
await Exact("remainder", -1, 7, 2);
await Exact("copysign", -0.0, 0, -1);
await Exact("nextafter", -0.0, 0, -0.0);
await Exact("nextafter", double.Epsilon, -0.0, 1);
await Exact("nextafter", -double.Epsilon, 0, -1);
await Exact("nextafter", double.MaxValue, double.PositiveInfinity, 0);
await Exact("nextafter", -double.MaxValue, double.NegativeInfinity, 0);
await Exact("fma", -Math.Pow(2, -54), 1 + Math.Pow(2, -27), 1 - Math.Pow(2, -27), -1);
await Exact("clamp", 3, 4, 1, 3);
try { await Native("clamp", 2, 3, 1); Check(false, "Reversed clamp bounds were accepted"); }
catch (MsyncException error) { Check(error.Message.Contains("lower", StringComparison.Ordinal), "Clamp bounds diagnostic is not useful"); }
foreach (var (name, input, expected) in new[]
{
    ("is_finite", 1.0, true), ("is_finite", double.PositiveInfinity, false),
    ("is_nan", double.NaN, true), ("is_infinite", double.NegativeInfinity, true),
    ("signbit", -0.0, true), ("signbit", 0.0, false)
}) Check(await Native(name, input) is BoolValue { Data: var actual } && actual == expected, name + " classification failed");

Random random = new(2718);
for (int index = 0; index < 40; index++)
{
    double first = random.NextDouble() * 1000 - 500, second = random.NextDouble() * 1000 - 500;
    await Exact("copysign", Math.CopySign(first, second), first, second);
    double toward = await Number("nextafter", first, second);
    Check(first < second ? toward > first && toward <= second : toward < first && toward >= second,
        "nextafter did not move one representable step toward its target");
    await Near("hypot", Math.Sqrt(first * first + second * second), first, second);
}

if (args.Contains("--native-only", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS: {checks} Number native and IEEE contracts");
    return;
}

foreach (var (source, expected) in new[]
{
    ("$std::math::square(7)", 49.0),
    ("$std::math::cube(-3)", -27.0),
    ("$std::math::sqrt(81)", 9.0),
    ("$std::math::round((\"2.5\" as Number))", 3.0),
    ("$std::math::round_even((\"2.5\" as Number))", 2.0),
    ("$std::math::clamp(8, 1, 4)", 4.0),
    ("$std::math::sign(- 0)", -0.0),
    ("$std::math::positive_difference(3, 5)", 0.0),
    ("$std::math::lerp(2, 6, (\"0.25\" as Number))", 3.0),
    ("$std::math::lerp((\"-1e308\" as Number), (\"1e308\" as Number), (\"0.5\" as Number))", 0.0),
    ("$std::ranges::fold($std::ranges::transform([3, 4], $std::math::square), 0, fn(a: Number, b: Number) { a + b })", 25.0)
})
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        var portable = Frontend.Compile(source, "stdlib-math", level);
        foreach (bool bound in new[] { false, true })
        {
            var module = bound ? Binding.Bind(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) : portable;
            using MemoryStream bytes = new();
            Artifact.Write(module, bytes); bytes.Position = 0;
            var result = await Interpreter.ExecuteAsync(Artifact.Read(bytes));
            Check(result is NumberValue number && BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected),
                $"Source math at O{level}/bound={bound}: expected {expected:R}, got {result}");
        }
    }
}
Console.WriteLine($"PASS: {checks} Number native, IEEE, source composition and binary-stage contracts");
