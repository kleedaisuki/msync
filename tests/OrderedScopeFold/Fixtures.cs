using Msync.Core;

internal static class Fixtures
{
    private const int Accumulator = 0, Iterator = 1, Row = 2, Invariant = 3, Alias = 4;
    private const int SampleLayout = 0, AffineLayout = 1, PermutedLayout = 2, ResultLayout = 3;
    private const int Number = 0, RowDomain = 1, ResultScope = 2, OptionalRow = 3, FirstExactRow = 4;
    private const int Sample = 0, Delta = 1;
    private const int AcrossChunks = 773;
    private static readonly double Large = Math.ScaleB(1, 54);
    private static readonly double QuietNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000012345UL));

    internal enum Operation { Sum, ReverseDifference, Division, NegatedProduct, Affine, ReusedField }
    internal enum Admission { Hit, GuardMiss, Unmatched }
    internal enum Escape { None, Alias, Row }

    internal sealed record Case(string Name, Operation Operation, double Seed, RowValue[] Values, double Expected,
        Admission Admission = Admission.Hit, Escape Escape = Escape.None, double? Observed = null, string[]? Printed = null,
        bool Called = false);

    internal readonly record struct RowValue(long FirstBits, long SecondBits = 0, string? Effect = null, bool Permuted = false)
    {
        internal static RowValue Of(double value, double second = 0, string? effect = null, bool permuted = false) =>
            new(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(second), effect, permuted);
        internal double First => BitConverter.Int64BitsToDouble(FirstBits);
        internal double Second => BitConverter.Int64BitsToDouble(SecondBits);
    }

    internal static IEnumerable<Case> Cases()
    {
        var rounded = Repeat(0, AcrossChunks);
        rounded[0] = RowValue.Of(Large);
        rounded[255] = RowValue.Of(1);
        rounded[256] = RowValue.Of(-Large);
        var reordered = (RowValue[])rounded.Clone();
        (reordered[255], reordered[256]) = (reordered[256], reordered[255]);
        yield return new("order-rounded", Operation.Sum, 0, rounded, 0);
        yield return new("order-retained", Operation.Sum, 0, reordered, 1);
        yield return new("reverse-difference", Operation.ReverseDifference, 3, Repeat(2, AcrossChunks), -1);
        yield return new("negative-zero", Operation.Sum, -0d, Repeat(-0d, AcrossChunks), -0d);
        yield return new("negated-zero", Operation.NegatedProduct, -0d, Repeat(1, AcrossChunks), 0);
        var divide = Repeat(1, AcrossChunks);
        divide[511] = RowValue.Of(-0d);
        yield return new("negative-infinity", Operation.Division, 1, divide, double.NegativeInfinity);
        var unordered = Repeat(1, AcrossChunks);
        unordered[513] = RowValue.Of(QuietNaN);
        yield return new("unordered", Operation.Sum, 0, unordered, double.NaN);

        var affine = Repeat(1, AcrossChunks);
        affine[0] = RowValue.Of(1 - Math.ScaleB(1, -27), -1);
        // Each multiplication rounds before the following addition. An FMA
        // would retain -2^-54 and make this result negative rather than +0.
        yield return new("no-fma", Operation.Affine, 1 + Math.ScaleB(1, -27), affine, 0);

        var reused = Enumerable.Range(0, AcrossChunks)
            .Select(i => RowValue.Of((i * 11 % 23 - 11) * 0.25)).ToArray();
        double expected = 3;
        foreach (var row in reused)
        {
            double product = 2 * row.First;
            double added = expected + product;
            double divided = added / 2;
            expected = divided + row.First;
        }
        yield return new("reused-field", Operation.ReusedField, 3, reused, expected);

        yield return new("empty", Operation.Sum, -0d, [], -0d);
        yield return new("single", Operation.Sum, 3, [RowValue.Of(4)], 7);
        yield return new("called-private-frame", Operation.Sum, 1, Repeat(2, AcrossChunks), 1547, Called: true);
        yield return new("mixed-effects", Operation.Sum, 1,
            [RowValue.Of(2, effect: "first"), RowValue.Of(4), RowValue.Of(8, effect: "last")], 15,
            Admission.GuardMiss, Printed: ["first", "last"], Called: true);
        var permuted = Enumerable.Range(0, AcrossChunks)
            .Select(index => index % 2 == 0 ? RowValue.Of(2, permuted: true) : RowValue.Of(4)).ToArray();
        yield return new("permuted-layout", Operation.Sum, 1, permuted, 1 + 2 * ((AcrossChunks + 1) / 2) + 4 * (AcrossChunks / 2));
        yield return new("aliased-cell", Operation.Sum, 1, Repeat(2, AcrossChunks), 1547,
            Admission.GuardMiss, Escape.Alias, Observed: 1547, Called: true);
        yield return new("escaped-row", Operation.Sum, 1, [RowValue.Of(2), RowValue.Of(4)], 7,
            Admission.Unmatched, Escape.Row, Observed: 4);
    }

    private static RowValue[] Repeat(double value, int count) => Enumerable.Repeat(RowValue.Of(value), count).ToArray();

    internal static Module ModuleFor(Case test)
    {
        bool affine = test.Operation == Operation.Affine;
        int[] layouts = test.Values.Length == 0 ? [SampleLayout] : test.Values
            .Select(row => Layout(row, affine)).Distinct().Order().ToArray();
        List<Value> constants = [new NumberValue(test.Seed), new NumberValue(2), new TypeValue(TypeRef.Scope)];
        List<FunctionCode> functions = [];
        Dictionary<RowValue, int> factories = [];
        List<Instruction> main = [new(OpCode.Constant, 0), new(OpCode.InitializeLocal,
            test.Escape == Escape.Alias ? Alias : Accumulator)];
        if (test.Escape == Escape.Alias)
            main.AddRange([new(OpCode.LoadCell, Alias, Number + 1), new(OpCode.InitializeLocal, Accumulator)]);
        if (test.Escape == Escape.Row)
            main.AddRange([new(OpCode.Null), new(OpCode.InitializeLocal, Row)]);
        if (test.Operation == Operation.ReusedField)
            main.AddRange([new(OpCode.Constant, 1), new(OpCode.InitializeLocal, Invariant)]);

        foreach (var row in test.Values)
        {
            if (!factories.TryGetValue(row, out int factory))
            {
                factory = functions.Count + 1;
                factories.Add(row, factory);
                functions.Add(Factory(row, Layout(row, affine), constants));
            }
            main.Add(new(OpCode.MakeClosure, factory));
        }
        main.AddRange([
            new(OpCode.MakeLazyRange, test.Values.Length, 3),
            new(OpCode.GetIterator), new(OpCode.InitializeLocal, Iterator),
        ]);
        int header = main.Count;
        main.AddRange([new(OpCode.IteratorNext, Iterator), new(OpCode.AdmitScope, RowDomain),
            new(OpCode.InitializeLocal, Row)]);
        EmitBody(test.Operation, main);
        main.AddRange([new(OpCode.StoreLocal, Accumulator), new(OpCode.Jump, header)]);
        main[header] = main[header] with { B = main.Count };
        main.Add(new(OpCode.LoadLocal, Accumulator));
        if (test.Escape != Escape.None)
        {
            main.Add(new(OpCode.LoadLocal, test.Escape == Escape.Alias ? Alias : Row));
            main.Add(new(OpCode.NewScope, ResultLayout));
        }
        main.Add(new(OpCode.Return));
        TypeRef result = test.Escape == Escape.None ? TypeRef.Number : TypeRef.Scope;
        int resultType = test.Escape == Escape.None ? Number : ResultScope;
        if (!test.Called)
        {
            functions.Insert(0, new FunctionCode("entry", 0, 5, [.. main], result)
            { CodeSignature = new([], resultType, []) });
            return Image(0);
        }
        for (int pc = 1; pc < main.Count; pc++)
        {
            var instruction = main[pc];
            if (instruction.Op is OpCode.LoadLocal or OpCode.InitializeLocal or OpCode.StoreLocal or OpCode.LoadCell
                or OpCode.IteratorNext)
                main[pc] = instruction with { A = instruction.A + 1 };
        }
        main[0] = new(OpCode.LoadLocal, 0);
        functions.Insert(0, new FunctionCode("entry", 1, 6, [.. main], result, ParameterTypes: [TypeRef.Number])
        { CodeSignature = new([Number], resultType, []) });
        int caller = functions.Count;
        functions.Add(new FunctionCode("caller", 0, 0,
            [new(OpCode.MakeClosure, 0), new(OpCode.Constant, 0), new(OpCode.Call, 1, 1), new(OpCode.Return)], result)
        { CodeSignature = new([], resultType, []) });
        return Image(caller);

        Module Image(int entry) => new(constants, functions, EntryPoint: entry, OptimizationLevel: 0)
        {
            CodeTypes = [
                new(TypeKind.Number), new(TypeKind.Scope, ScopeLayouts: layouts),
                new(TypeKind.Scope, ScopeLayouts: [ResultLayout]),
                new(TypeKind.Scope | TypeKind.Null, ScopeLayouts: layouts),
                new(TypeKind.Scope, ScopeLayouts: [SampleLayout]),
                new(TypeKind.Scope, ScopeLayouts: [AffineLayout]),
                new(TypeKind.Scope, ScopeLayouts: [PermutedLayout])
            ],
            ScopeLayouts = [
                new([new("sample", Number)]),
                new([new("sample", Number), new("delta", Number)]),
                new([new("unused", Number), new("sample", Number)]),
                new([new("result", Number), new("observed", test.Escape == Escape.Row ? OptionalRow : Number)])
            ],
            FieldProjections = [
                new("sample", layouts.Select(layout => new CodeFieldCase(layout, layout == PermutedLayout ? 1 : 0)).ToArray()),
                new("delta", [new(AffineLayout, 1)])
            ]
        };
    }

    private static int Layout(RowValue row, bool affine) =>
        affine ? AffineLayout : row.Permuted ? PermutedLayout : SampleLayout;

    private static FunctionCode Factory(RowValue row, int layout, List<Value> constants)
    {
        List<Instruction> code = [];
        if (row.Effect is { } text)
        {
            code.Add(new(OpCode.Constant, constants.Count));
            constants.Add(new StringValue(text));
            code.AddRange([new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop)]);
        }
        if (layout == PermutedLayout)
        {
            code.Add(new(OpCode.Constant, constants.Count));
            constants.Add(new NumberValue(999));
        }
        code.Add(new(OpCode.Constant, constants.Count));
        constants.Add(new NumberValue(row.First));
        if (layout == AffineLayout)
        {
            code.Add(new(OpCode.Constant, constants.Count));
            constants.Add(new NumberValue(row.Second));
        }
        code.AddRange([new(OpCode.NewScope, layout), new(OpCode.Return)]);
        return new(row.Effect is null ? "literal" : "effect", 0, 0, [.. code], TypeRef.Scope)
        { CodeSignature = new([], FirstExactRow + layout, []) };
    }

    private static void EmitBody(Operation operation, List<Instruction> code)
    {
        void Project(int place = Sample) =>
            code.AddRange([new(OpCode.LoadLocal, Row), new(OpCode.LoadField, place)]);

        if (operation == Operation.ReverseDifference)
        {
            Project();
            code.AddRange([new(OpCode.LoadLocal, Accumulator), new(OpCode.Subtract)]);
            return;
        }

        code.Add(new(OpCode.LoadLocal, Accumulator));
        if (operation == Operation.ReusedField) code.Add(new(OpCode.LoadLocal, Invariant));
        Project();
        switch (operation)
        {
            case Operation.Sum: code.Add(new(OpCode.Add)); break;
            case Operation.Division: code.Add(new(OpCode.Divide)); break;
            case Operation.NegatedProduct: code.AddRange([new(OpCode.Multiply), new(OpCode.Negate)]); break;
            case Operation.Affine:
                code.Add(new(OpCode.Multiply));
                Project(Delta);
                code.Add(new(OpCode.Add));
                break;
            case Operation.ReusedField:
                code.AddRange([new(OpCode.Multiply), new(OpCode.Add), new(OpCode.Constant, 1), new(OpCode.Divide)]);
                Project();
                code.Add(new(OpCode.Add));
                break;
        }
    }
}
