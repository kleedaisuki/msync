using System.Runtime.CompilerServices;
using System.Reflection;
using Msync.Core;

internal static class SpatialLayoutChecks
{
    internal static void Run(bool packed = false)
    {
        int checks = 0;
        Func<ScopeLayout, Value[], ScopeValue> create = packed ? PackedFactory()
            : static (shape, values) => new(shape, values);
        ScopeLayout layout = new(["x", "y", "z", "weight", "payload"]);
        ScopeValue item = Create(create, layout, 1, "first");
        ScopeValue alias = item;
        HashSet<ScopeValue> identities = [item];
        int identityHash = item.GetHashCode();
        if (packed) Check(IsPacked(item), "The explicit numeric-row factory did not select packed storage.");

        foreach (long representation in new long[]
        {
            0, long.MinValue, 1, 0x0010_0000_0000_0000,
            0x7ff0_0000_0000_0000, unchecked((long)0xfff0_0000_0000_0000),
            0x7ff8_0000_0000_0001, 0x7ff8_0000_0000_cafe
        })
        {
            item.SetField(0, new NumberValue(BitConverter.Int64BitsToDouble(representation)));
            Check(Bits(alias.GetField(0)) == representation, "Indexed numeric read changed IEEE representation.");
            Check(Bits(alias["x"]) == representation, "Named numeric read changed IEEE representation.");
            Check(Bits(alias.Single(field => field.Key == "x").Value) == representation, "Enumeration changed IEEE representation.");
        }

        item.SetField(0, new StringValue("reference"));
        Check(alias["x"] is StringValue { Data: "reference" }, "A reference store into a numeric physical slot was lost.");
        item.SetMember("x", Value.Null);
        Check(alias.GetField(0) is NullValue, "A Null store into a numeric physical slot was lost.");
        item.SetField(0, new NumberValue(19));
        Check(Bits(alias["x"]) == BitConverter.DoubleToInt64Bits(19), "Numeric storage did not recover after a reference/Null store.");

        CellValue cell = new(new NumberValue(23));
        item.SetField(0, cell);
        Check(alias["x"] is NumberValue { Data: 23 }, "Installing a field Cell did not expose its value.");
        cell.Data = new NumberValue(-0.0);
        Check(Bits(alias.GetField(0)) == long.MinValue, "Captured-cell writes are not visible through the physical slot.");
        alias.SetMember("x", new NumberValue(31));
        Check(cell.Data is NumberValue { Data: 31 }, "Field store replaced rather than updated the captured Cell.");
        cell.Data = Value.Null;
        Check(item["x"] is NullValue, "Captured nullable state was not retained.");
        item.SetField(0, new StringValue("captured reference"));
        Check(cell.Data is StringValue { Data: "captured reference" }, "Captured reference store did not share its location.");

        Check(item.GetHashCode() == identityHash && identities.Contains(item), "Physical field transitions changed instance identity.");
        Check(item.Count == 5 && new HashSet<string>(item.Keys, StringComparer.Ordinal)
            .SetEquals(["x", "y", "z", "weight", "payload"]), "Physical layout changed the named fields.");
        ScopeValue independent = item.MapFields(value => value);
        independent.SetField(0, new NumberValue(37));
        Check(cell.Data is StringValue { Data: "captured reference" }, "Visible-value cloning retained the old shared Cell.");
        Check(!ReferenceEquals(item, independent) && !item.Equals(independent), "Layout sharing merged independent Scope instances.");
        Check(ReferenceEquals(item.Layout, independent.Layout), "Visible-value cloning did not retain the logical shape.");

        ScopeLayout numeric = new(["a", "b", "c", "d"]);
        ScopeValue emptyFirst = new(ScopeLayout.Parse(""), []);
        ScopeValue emptySecond = new(ScopeLayout.Parse(""), []);
        Check(!emptyFirst.Equals(emptySecond), "Empty storage merged object identities.");
        if (packed)
        {
            ScopeLayout wideLayout = new(Enumerable.Range(0, 130).Select(index => "field" + index));
            Value[] wideValues = Enumerable.Repeat<Value>(new StringValue("reference"), wideLayout.Count).ToArray();
            foreach (int field in new[] { 0, 64, 129 }) wideValues[field] = new NumberValue(field + 1);
            ScopeValue wide = create(wideLayout, wideValues);
            Check(IsPacked(wide), "Packed shape imposed a field-mask-width restriction.");
            foreach (int field in new[] { 0, 64, 129 })
                Check(wide.GetField(field) is NumberValue number && number.Data == field + 1, "Sparse wide numeric offsets changed values.");
            Check(wide.GetField(128) is StringValue { Data: "reference" }, "Wide reference offset changed.");
            ScopeValue[] dense = Enumerable.Range(0, 32).Select(index => create(numeric,
                [new NumberValue(index), new NumberValue(index + 1),
                 new NumberValue(index + 2), new NumberValue(index + 3)])).ToArray();
            Check(dense.All(IsPacked), "A numeric-only row did not use packed storage.");
            var neighbors = dense.GroupBy(NumericPage, ReferenceEqualityComparer.Instance).First(group => group.Count() >= 2)
                .Take(2).ToArray();
            Check(ReferenceEquals(FieldStorage(neighbors[0]), FieldStorage(neighbors[1])),
                "Rows in one reference-free page still have separate storage descriptors.");
            Check(neighbors[0].GetOperandField(2, out double raw) is null
                && raw == ((NumberValue)neighbors[0]["c"]).Data, "Dense read did not return a raw numeric operand.");
            foreach (int offset in new[] { -1, numeric.Count })
            {
                try
                {
                    _ = neighbors[0].GetOperandField(offset, out _);
                    throw new InvalidOperationException("Dense row read crossed its logical boundary.");
                }
                catch (IndexOutOfRangeException) { checks++; }
            }
            long neighborBits = Bits(neighbors[1]["a"]);
            neighbors[0].SetNumber(0, BitConverter.Int64BitsToDouble(long.MinValue));
            Check(Bits(neighbors[0]["a"]) == long.MinValue, "Dense row store changed the numeric bits.");
            var denseCell = new CellValue(new NumberValue(17));
            neighbors[0].SetField(0, denseCell);
            neighbors[0].SetNumber(0, 19);
            Check(!IsPacked(neighbors[0]) && IsPacked(neighbors[1]) && Bits(neighbors[1]["a"]) == neighborBits,
                "Dense promotion changed a neighboring row.");
            Check(neighbors[0].TryGetFieldCell(0, out var fieldCell) && ReferenceEquals(fieldCell, denseCell)
                && fieldCell.Data is NumberValue { Data: 19 }, "Dense promotion lost the captured location.");
            ScopeValue[] clustered = Enumerable.Range(0, 32).Select(index => Create(create, layout, index, "clustered")).ToArray();
            HashSet<double[]> pages = new(ReferenceEqualityComparer.Instance);
            foreach (ScopeValue row in clustered)
            {
                Check(IsPacked(row), "A mixed numeric row did not use packed storage.");
                pages.Add(NumericPage(row));
            }
            Check(pages.Count < clustered.Length, "Each numeric row still owns a separate numeric array.");
            var read = typeof(ScopeValue).GetMethod("TryGetNumber")!.CreateDelegate<NumberReader>();
            var write = typeof(ScopeValue).GetMethod("SetNumber")!.CreateDelegate<NumberWriter>();
            for (int index = 0; index < 32; index++)
            {
                write(clustered[0], 0, index);
                _ = read(clustered[0], 0, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            double final = 0;
            for (int index = 0; index < 1024; index++)
            {
                write(clustered[0], 0, index);
                if (!read(clustered[0], 0, out final)) throw new InvalidOperationException("Packed numeric read became a reference read.");
            }
            long rawBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(final == 1023 && rawBytes == 0, "Raw numeric field access allocated per-operation wrappers.");
            foreach (Value replacement in new Value[] { new StringValue("changed"), Value.Null, new CellValue(new NumberValue(47)) })
            {
                ScopeValue promoted = Create(create, layout, 2, "promoted");
                ScopeValue neighbor = Create(create, layout, 9, "neighbor");
                Check(IsPacked(promoted) && IsPacked(neighbor), "Independent numeric rows were not packed.");
                promoted.SetField(0, replacement);
                Check(!IsPacked(promoted) && IsPacked(neighbor), "Promotion changed storage of an unrelated row.");
                if (replacement is CellValue shared)
                {
                    shared.Data = new NumberValue(53);
                    Check(promoted.GetField(0) is NumberValue { Data: 53 }, "Packed-to-Cell promotion lost the shared location.");
                    promoted.SetField(0, new NumberValue(59));
                    Check(shared.Data is NumberValue { Data: 59 }, "A promoted field stopped updating its captured location.");
                }
                Check(neighbor.GetField(0) is NumberValue { Data: 9 }, "Promotion overwrote another row's numeric payload.");
            }
        }
        var retained = MakeRows(create, layout, numeric);
        Collect();
        Check(retained.DeadRows.All(weak => !weak.TryGetTarget(out _)), "An allocator retained unrelated Scope handles.");
        Check(retained.DeadPayloads.All(weak => !weak.TryGetTarget(out _)), "A live numeric row retained unrelated managed payloads.");
        Check(retained.Live["payload"] is StringValue { Data: "live payload" }, "The retained row lost its own reference payload.");
        Check(retained.Live.GetField(3) is NumberValue { Data: 43 }, "Collection invalidated retained numeric storage.");
        Check(retained.LiveNumeric.GetField(2) is NumberValue { Data: 9 }, "A retained numeric-only row lost its payload.");
        GC.KeepAlive(retained.Live);
        GC.KeepAlive(retained.LiveNumeric);

        Console.WriteLine($"PASS {checks} spatial layout value/identity/capture/retention contracts");

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static ScopeValue Create(Func<ScopeLayout, Value[], ScopeValue> create,
        ScopeLayout layout, double value, string payload) => create(layout,
        [new NumberValue(value), new NumberValue(value + 1), new NumberValue(value + 2),
         new NumberValue(value + 3), new StringValue(payload)]);

    private static long Bits(Value value) => value is NumberValue number
        ? BitConverter.DoubleToInt64Bits(number.Data)
        : throw new InvalidOperationException($"Expected Number, found {value.Type}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ScopeValue Live, ScopeValue LiveNumeric,
        WeakReference<ScopeValue>[] DeadRows, WeakReference<StringValue>[] DeadPayloads)
        MakeRows(Func<ScopeLayout, Value[], ScopeValue> create, ScopeLayout mixed, ScopeLayout numeric)
    {
        ScopeValue live = Create(create, mixed, 40, "live payload");
        ScopeValue liveNumeric = create(numeric,
            [new NumberValue(7), new NumberValue(8), new NumberValue(9), new NumberValue(10)]);
        List<WeakReference<ScopeValue>> rows = [];
        List<WeakReference<StringValue>> payloads = [];
        for (int index = 0; index < 257; index++)
        {
            StringValue payload = new(new string((char)('a' + index % 26), 8192));
            ScopeValue doomed = create(mixed,
                [new NumberValue(index), new NumberValue(index + 1), new NumberValue(index + 2),
                 new NumberValue(index + 3), payload]);
            rows.Add(new(doomed));
            payloads.Add(new(payload));
            ScopeValue numericOnly = create(numeric,
                [new NumberValue(index), new NumberValue(index + 1),
                 new NumberValue(index + 2), new NumberValue(index + 3)]);
            rows.Add(new(numericOnly));
        }
        return (live, liveNumeric, rows.ToArray(), payloads.ToArray());
    }

    private static Func<ScopeLayout, Value[], ScopeValue> PackedFactory()
    {
        Type allocatorType = typeof(ScopeValue).Assembly.GetType("Msync.Core.ScopeAllocator")
            ?? throw new InvalidOperationException("This image has no packed Scope allocator.");
        object allocator = Activator.CreateInstance(allocatorType)!;
        MethodInfo method = allocatorType.GetMethod("Create", [typeof(ScopeLayout), typeof(Value[])])
            ?? throw new InvalidOperationException("This image has no array-based packed Scope construction API.");
        return method.CreateDelegate<Func<ScopeLayout, Value[], ScopeValue>>(allocator);
    }

    private static bool IsPacked(ScopeValue value) =>
        typeof(ScopeValue).GetProperty("IsPacked", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            is true;

    private static double[] NumericPage(ScopeValue value)
    {
        object storage = FieldStorage(value);
        FieldInfo page = storage.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(double[]));
        return (double[])page.GetValue(storage)!;
    }

    private static object FieldStorage(ScopeValue value) =>
        typeof(ScopeValue).GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private delegate bool NumberReader(ScopeValue value, int field, out double number);
    private delegate void NumberWriter(ScopeValue value, int field, double number);

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    internal static void CheckEscapedRowIsolation()
    {
        var state = MakeEscapedRow();
        Collect();
        if (state.AbandonedPage.TryGetTarget(out _))
            throw new InvalidOperationException("An escaped row retains its allocator's unrelated current page.");
        if (state.PreviousBoxes.Any(weak => weak.TryGetTarget(out _)))
            throw new InvalidOperationException("An escaped row retains unrelated input Number boxes through allocator state.");
        if (state.Live.GetField(0) is not NumberValue { Data: 1 })
            throw new InvalidOperationException("Escaped row lost its actual numeric page.");
        GC.KeepAlive(state.Live);
        Console.WriteLine("PASS escaped numeric row does not retain allocator state or unrelated numeric pages");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ScopeValue Live, WeakReference<double[]> AbandonedPage,
        WeakReference<NumberValue>[] PreviousBoxes) MakeEscapedRow()
    {
        var create = PackedFactory();
        ScopeLayout layout = new(["a", "b", "c", "d"]);
        ScopeValue live = create(layout,
            [new NumberValue(1), new NumberValue(2), new NumberValue(3), new NumberValue(4)]);
        ScopeValue? final = null;
        NumberValue[] boxes = [];
        for (int row = 0; row < 64; row++)
        {
            boxes = [new(row + 10), new(row + 11), new(row + 12), new(row + 13)];
            final = create(layout, boxes.Cast<Value>().ToArray());
        }
        double[] page = NumericPage(final!);
        if (ReferenceEquals(page, NumericPage(live)))
            throw new InvalidOperationException("The retention discriminator did not create a distinct later page.");
        return (live, new(page), boxes.Select(value => new WeakReference<NumberValue>(value)).ToArray());
    }
}
