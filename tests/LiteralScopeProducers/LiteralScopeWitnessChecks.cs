using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class LiteralScopeWitnessChecks
{
    private static readonly TypeRef Rows = new(TypeKind.Range, TypeRef.Scope);
    private const long NaNBits = unchecked((long)0xfff8000000010456UL);

    internal static async Task Run(string root)
    {
        CheckTemplate();
        await CheckTokens(root);
        await CheckIdentity(root);
        await CheckPartial(root);
        Console.WriteLine("PASS readonly literal row producer: exact field bits, fresh identities, selected tokens, lazy partial factory");
    }

    private static void CheckTemplate()
    {
        var template = ScopeLiteralTemplate.TryCreate(new ScopeLayout(["x", "y", "label"]),
            [new NumberValue(-0d), new NumberValue(BitConverter.Int64BitsToDouble(NaNBits)), new StringValue("literal")], 0)
            ?? throw new InvalidOperationException("Expected a literal template.");
        Require(template.TryGetNumber(0, "x", out double x) && BitConverter.DoubleToInt64Bits(x) == long.MinValue
            && template.TryGetNumber(1, "y", out double y) && BitConverter.DoubleToInt64Bits(y) == NaNBits,
            "The projection must preserve exact Number bits.");
        Require(!template.TryGetNumber(0, "y", out _) && !template.TryGetNumber(1, "x", out _)
            && !template.TryGetNumber(2, "label", out _) && !template.TryGetNumber(3, "x", out _),
            "A field name, layout offset, and ready Number are independent obligations.");
        var first = template.Create();
        first.SetMember("x", new NumberValue(29));
        Require(template.TryGetNumber(0, "x", out x) && BitConverter.DoubleToInt64Bits(x) == long.MinValue
            && !ReferenceEquals(first, template.Create()), "An escaped Object write must not mutate the producer witness.");
    }

    private static async Task CheckTokens(string root)
    {
        using var producer = new CancellationTokenSource();
        using var consumer = new CancellationTokenSource();
        RangeValue rows = await Produce(root, [new(OpCode.MakeClosure, 1), new(OpCode.MakeLazyRange, 1), new(OpCode.Return)],
            producer.Token);
        consumer.Cancel();
        await ExpectCancelled(rows, consumer.Token, "selected consumer token");
        producer.Cancel();
        await ExpectCancelled(rows, CancellationToken.None, "original producer token");

        using var emptyToken = new CancellationTokenSource();
        RangeValue empty = await Produce(root, [new(OpCode.MakeLazyRange), new(OpCode.Return)], emptyToken.Token);
        emptyToken.Cancel();
        await using var enumeration = empty.Items.GetAsyncEnumerator();
        Require(!await enumeration.MoveNextAsync(), "An empty producer must not poll an unselected item token.");
    }

    private static async Task ExpectCancelled(RangeValue range, CancellationToken token, string boundary)
    {
        await using var enumerator = range.Items.GetAsyncEnumerator(token);
        try
        {
            _ = await enumerator.MoveNextAsync();
            throw new InvalidOperationException($"The first demand did not use the {boundary}.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task CheckIdentity(string root)
    {
        var range = await Produce(root, [new(OpCode.MakeClosure, 1), new(OpCode.MakeLazyRange, 1, Text: "1"), new(OpCode.Return)]);
        var first = (ScopeValue)await Front(range);
        first.SetMember("x", new NumberValue(29));
        var second = (ScopeValue)await Front(range);
        Require(!ReferenceEquals(first, second) && second["x"] is NumberValue { Data: 1 },
            "An ordinary demand from a witnessed producer must keep fresh identity.");
        var replaced = range with { Items = Replacement() };
        Require(await Front(replaced) is NumberValue { Data: 42 } && await Front(range) is ScopeValue,
            "The actual Items enumerable must determine the demand.");

        static async IAsyncEnumerable<Value> Replacement([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new NumberValue(42);
            await Task.CompletedTask;
        }
    }

    private static async Task CheckPartial(string root)
    {
        using StringWriter output = new();
        var range = await Produce(root, [new(OpCode.MakeClosure, 1), new(OpCode.MakeClosure, 2),
            new(OpCode.MakeLazyRange, 2, Text: "11"), new(OpCode.Return)], output: output);
        Require(output.ToString().Length == 0, "Constructing a partially proved Range must not invoke its remaining factory.");
        Require(await Front(range) is ScopeValue && output.ToString().Length == 0,
            "Demand for a proved prefix must not prefetch an effectful suffix.");

        for (int replay = 1; replay <= 2; replay++)
        {
            await using var iterator = range.Items.GetAsyncEnumerator();
            Require(await iterator.MoveNextAsync() && iterator.Current is ScopeValue first && first["x"] is NumberValue { Data: 1 }
                && await iterator.MoveNextAsync() && iterator.Current is ScopeValue last && last["x"] is NumberValue { Data: 2 }
                && !await iterator.MoveNextAsync(), "The selected mixed producer must retain exact item order.");
            Require(output.ToString() == string.Concat(Enumerable.Repeat("effect" + Environment.NewLine, replay)),
                "The effectful producer must run exactly once per actual demand.");
        }
    }

    private static async Task<Value> Front(RangeValue range)
    {
        await using var iterator = range.Items.GetAsyncEnumerator();
        if (await iterator.MoveNextAsync()) return iterator.Current;
        throw new InvalidOperationException("Expected a first item.");
    }

    private static async Task<RangeValue> Produce(string root, Instruction[] code, CancellationToken token = default,
        TextWriter? output = null)
    {
        const int Number = 0, Row = 1, RowRange = 2;
        Module portable = new([new NumberValue(1), new NumberValue(2), new StringValue("effect")],
            [
                new("entry", 0, 0, code, Rows) { CodeSignature = new([], RowRange, []) },
                new("literal", 0, 0, [new(OpCode.Constant, 0), new(OpCode.NewScope, 0), new(OpCode.Return)], TypeRef.Scope)
                    { CodeSignature = new([], Row, []) },
                new("effectful", 0, 0, [new(OpCode.Constant, 2), new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop),
                    new(OpCode.Constant, 1), new(OpCode.NewScope, 0), new(OpCode.Return)], TypeRef.Scope)
                    { CodeSignature = new([], Row, []) },
            ], OptimizationLevel: 0)
        {
            CodeTypes = [new(TypeKind.Number), new(TypeKind.Scope, ScopeLayouts: [0]), new(TypeKind.Range, Element: Row)],
            ScopeLayouts = [new([new("x", Number)])]
        };
        using MemoryStream artifact = new();
        Artifact.Write(Binding.Relocate(portable, new(root, root)), artifact);
        artifact.Position = 0;
        var executable = Artifact.Read(artifact, formBlocks: false);
        return (RangeValue)await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: root, Output: output), token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
