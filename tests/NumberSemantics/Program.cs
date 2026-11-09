using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "number-semantics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0;
try
{
    foreach ((string source, double expected) in new (string, double)[]
    {
        ("1.5+2.5", 4), (".5 * 2", 1), ("1e3+2", 1002), ("1_000+0x10+0b10+0o10", 1026),
        ("1/2", .5), ("8/2*2", 8), ("7%4", 3), ("-7%4", -3), ("7%-4", 3),
        ("2**3**2", 512), ("-2**2", -4), ("(-2)**2", 4), ("2**-2", .25),
        ("+3", 3), ("-0", -0d), ("-0%3", -0d), ("1/0", double.PositiveInfinity),
        ("0/0", double.NaN), ("\"0x10\" as Number", 16), ("\"-0b10\" as Number", -2),
        ("\"1_000.5\" as Number", 1000.5), ("\"-0\" as Number", -0d)
    })
        await AcrossArtifacts(source, value => value is NumberValue number
            && (double.IsNaN(expected) ? double.IsNaN(number.Data)
                : BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected)));

    foreach ((string source, bool expected) in new (string, bool)[]
    {
        ("let n = 0/0; n == n", false), ("let n = 0/0; n != n", true),
        ("let n = 0/0; n < 0", false), ("let n = 0/0; n <= 0", false),
        ("let n = 0/0; n > 0", false), ("let n = 0/0; n >= 0", false),
        ("-0 == 0", true), ("(0/0) as Bool", false), ("(-0) as Bool", false),
        ("(1/0) as Bool", true), ("$std::math::signbit(-0)", true)
    })
        await AcrossArtifacts(source, value => value is BoolValue boolean && boolean.Data == expected);

    foreach (string source in new[]
    {
        "$std::math::is_nan($std::math::nan)", "$std::math::is_infinite($std::math::infinity)",
        "$std::math::is_integer(9007199254740992)", "not $std::math::is_safe_integer(9007199254740992)",
        "$std::math::is_safe_integer($std::math::max_safe_integer)", "not $std::math::is_integer(1.5)",
        "not $std::math::is_integer($std::math::nan)", "not $std::math::is_safe_integer($std::math::infinity)",
        "$std::math::min_positive > 0", "$std::math::max_value < $std::math::infinity"
    })
        await AcrossArtifacts(source, value => value is BoolValue { Data: true });

    foreach (string source in new[]
    {
        "$std::ranges::empty($std::ranges::take([], 2147483648))",
        "$std::ranges::empty($std::ranges::drop([1], 2147483648))",
        "$std::ranges::first($std::ranges::stride([true, false], 2147483648))",
        "$std::ranges::first($std::ranges::take([true], 9223372036854774784))"
    })
        await AcrossArtifacts(source, value => value is BoolValue { Data: true });

    foreach (string operation in new[] { "take", "drop", "stride", "repeat", "chunk", "slide", "at" })
    {
        await ExpectFailure(() => NativeRegistry.InvokeAsync("ranges." + operation,
            [new RangeValue([Value.True], TypeRef.Bool), new StringValue("1")], new RuntimeContext(), NoCallback).AsTask(),
            "ranges." + operation + " implicitly parsed a String count");
        foreach (double invalid in new[] { -1d, .5, double.NaN, double.PositiveInfinity, 9223372036854775808d })
        {
            IReadOnlyList<Value> arguments = [new RangeValue([Value.True], TypeRef.Bool), new NumberValue(invalid)];
            await ExpectFailure(() => NativeRegistry.InvokeAsync("ranges." + operation, arguments, new RuntimeContext(), NoCallback).AsTask(),
                "ranges." + operation + " accepted invalid count " + invalid);
        }
    }
    foreach (string operation in new[] { "stride", "chunk", "slide" })
        await ExpectFailure(() => NativeRegistry.InvokeAsync("ranges." + operation,
            [new RangeValue([Value.True], TypeRef.Bool), new NumberValue(0)], new RuntimeContext(), NoCallback).AsTask(),
            "ranges." + operation + " accepted zero stride");

    Require(ExplicitConversions.Possible(new(TypeKind.Number | TypeKind.Bool), TypeRef.Number), "Supported scalar cast union rejected");
    Require(!ExplicitConversions.Possible(new(TypeKind.String | TypeKind.Scope), TypeRef.Number), "Unsupported Scope variant admitted by scalar cast");
    Require(!ExplicitConversions.Possible(new(TypeKind.String | TypeKind.Null), TypeRef.Number), "Null discarded by scalar cast");
    Require(ExplicitConversions.Possible(new(TypeKind.String | TypeKind.Null), new(TypeKind.Number | TypeKind.Null)), "Nullable scalar cast rejected");

    Random random = new(932017);
    foreach (string operation in new[] { "take", "drop", "stride" })
    {
        for (int sample = 0; sample < 40; sample++)
        {
            int length = random.Next(0, 40), count = random.Next(operation == "stride" ? 1 : 0, 50);
            Value[] input = Enumerable.Range(0, length).Select(value => (Value)new NumberValue(value)).ToArray();
            Value adapted = await NativeRegistry.InvokeAsync("ranges." + operation,
                [new RangeValue(input, TypeRef.Number), new NumberValue(count)], new RuntimeContext(), NoCallback);
            List<int> actual = [];
            await foreach (Value value in ((RangeValue)adapted).Items) actual.Add((int)((NumberValue)value).Data);
            IEnumerable<int> expected = operation switch
            {
                "take" => Enumerable.Range(0, length).Take(count),
                "drop" => Enumerable.Range(0, length).Skip(count),
                _ => Enumerable.Range(0, length).Where(index => index % count == 0)
            };
            Require(actual.SequenceEqual(expected), operation + " sequence mismatch");
        }
    }
    Console.WriteLine($"Number semantics: {assertions} contracts passed across source, mso, mexe and lazy native ranges.");
}
finally { Directory.Delete(root, recursive: true); }

async Task AcrossArtifacts(string source, Func<Value, bool> check)
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module module = Compiler.Compile(source, "number-semantics", level);
        foreach (Module variant in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(root, root))) })
        {
            Value value = await Interpreter.ExecuteAsync(variant, new(WorkingDirectory: root));
            Require(check(value), $"O{level}: {source} produced {value}");
        }
    }
}
Module RoundTrip(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream); stream.Position = 0; return Artifact.Read(stream);
}
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
async Task ExpectFailure(Func<Task<Value>> action, string message)
{
    try { await action(); }
    catch (MsyncException) { assertions++; return; }
    throw new InvalidOperationException(message);
}
ValueTask<Value> NoCallback(Value value, IReadOnlyList<Value> arguments, CancellationToken token) =>
    throw new InvalidOperationException("Count operations must not invoke a callback.");
