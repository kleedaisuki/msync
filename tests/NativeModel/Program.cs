using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        assertions++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    public static async Task Main()
    {
        ClosedValues();
        StaticStorage();
        RepeatedRegions();
        TypedCopies();
        await PointerMetadata();
        OwnedRowRanges();
        UnionTransitions();
        ExactNumbers();
        AliasesAndCaptures();
        TrustedOffsets();
        EmptyAndPendingMetadata();
        DeferredScalarContracts();
        LogicalRangeMetadata();
        await DeferredLogicalRangeMetadata();
        await StreamsAndProcesses();
        GcReferences();
        Console.WriteLine($"PASS native model: {assertions} contracts");
    }
    private static NativeValue[] AllValues()
    {
        NativeAllocation allocation = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        allocation.Field(0).Store(NativeValue.FromNumber(3));
        NativeEnvironment environment = new(new([new NativeCaptureLayout(NativeType.Of(NativeKind.Number), true)]),
            [new NativeCapture(allocation.Field(0))]);
        return [NativeValue.Null, NativeValue.FromBool(true), NativeValue.FromNumber(-0.0),
            NativeValue.FromString("可莉𐐀"),
            NativeValue.FromPath(new("raw\\name", "/captured", new(ConnectionKind.Ssh, "configured-host"), "/home/klee", "local-projection")),
            NativeValue.FromConnection(new(ConnectionKind.Wsl, "debian")),
            NativeValue.FromFunction(new(17, environment, "io.println")),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(7)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "result", "diagnostic") { ProcessId = 4321 }),
            NativeValue.FromType(new(TypeKind.Path | TypeKind.Null)), NativeValue.FromAddress(allocation.Address)];
    }
    private static void TypedCopies()
    {
        NativeValue[] values = AllValues();
        NativeType sum = NativeType.Union(values.Select(value => NativeType.Of(value.Kind)).ToArray());
        NativeAllocation union = new(new NativeLayout(sum, sum));
        foreach (NativeValue value in values)
        {
            NativeType type = NativeType.Of(value.Kind);
            NativeAllocation source = new(new NativeLayout(type, type));
            NativeAllocation destination = new(new NativeLayout(type));
            source.Field(0).Store(value);
            NativeStorage.Copy(source.Field(0), source.Field(1));
            Check(source.Field(1).Load() == value, "Same-owner typed copy preserves " + value.Kind);
            NativeStorage.Copy(source.Field(0), destination.Field(0));
            Check(destination.Field(0).Load() == value, "Cross-owner typed copy preserves " + value.Kind);
            NativeStorage.Copy(destination.Field(0), destination.Field(0));
            Check(destination.Field(0).Load() == value, "Self-copy preserves " + value.Kind);
            NativeStorage.Copy(source.Field(0), union.Field(0));
            Check(union.Field(0).Load() == value, "Mono-to-sum copy preserves " + value.Kind);
            NativeStorage.Copy(union.Field(0), union.Field(1));
            Check(union.Field(1).Load() == value, "Sum-to-sum copy preserves " + value.Kind);
            NativeStorage.Copy(union.Field(1), destination.Field(0));
            Check(destination.Field(0).Load() == value, "Sum-to-mono copy preserves " + value.Kind);
            source.Field(0).Clear();
            Throws<InvalidOperationException>(() => NativeStorage.Copy(source.Field(0), source.Field(1)),
                "Typed copy rejects uninitialized " + value.Kind);
            Check(source.Field(1).Load() == value, "Failed copy leaves its destination intact");
        }
        NativeAllocation invalid = new(new NativeLayout(NativeType.Of(NativeKind.Number), NativeType.Of(NativeKind.String)));
        invalid.Field(0).Store(NativeValue.FromNumber(7));
        invalid.Field(1).Store(NativeValue.FromString("retained"));
        Throws<ArgumentException>(() => NativeStorage.Copy(invalid.Field(0), invalid.Field(1)),
            "Typed copy does not reinterpret Number bits as a String root");
        Check(invalid.Field(1).Load().AsString() == "retained", "Incompatible copy retains destination roots");
    }

    private static async Task PointerMetadata()
    {
        NativeValue[] values = AllValues();
        NativeLayout layout = new(values.Select(value => NativeType.Of(value.Kind)).ToArray());
        NativeAllocation source = new(layout);
        NativeAllocation pointers = new(new NativeLayout(Enumerable.Repeat(NativeType.Of(NativeKind.Address), values.Length).ToArray()));
        for (int index = 0; index < values.Length; index++)
        {
            NativeAddress location = source.Field(index);
            location.Store(values[index]);
            NativeValue pointer = NativeValue.FromAddress(location);
            Check(pointer.AsAddress().Type.Equals(location.Type), "Address values retain physical " + values[index].Kind + " storage type");
            pointers.Field(index).Store(pointer);
            NativeAddress restored = pointers.Field(index).Load().AsAddress();
            Check(restored.Type.Equals(location.Type) && restored.Load() == values[index],
                "Typed pointer storage dereferences the original physical builtin");
        }
        NativeAllocation copy = new(pointers.Layout);
        NativeStorage.CopyRegion(pointers.Address, copy.Address, pointers.Layout, 1);
        for (int index = 0; index < values.Length; index++)
            Check(copy.Field(index).Load().AsAddress().Load() == values[index], "Region copies preserve pointer ABI metadata");
        NativeValue typed = NativeValue.FromAddress(source.Field(0));
        NativeValue untyped = NativeValue.FromAddress(source.Address);
        Check(typed == untyped && typed.GetHashCode() == untyped.GetHashCode(), "Physical pointer metadata does not change location identity");
        Throws<InvalidOperationException>(() => _ = untyped.AsAddress().Type, "Namespace bases do not invent a pointee type");
        NativeEnvironment environment = new(new([new NativeCaptureLayout(NativeType.Of(NativeKind.Address))]), [new NativeCapture(typed)]);
        Check(environment[0].Read().AsAddress().Type.Equals(source.Field(0).Type), "By-value captures preserve typed pointer ABI");
        NativeRange snapshot = NativeRange.Snapshot(pointers.InitializedValues, TypeRef.Scope);
        int item = 0;
        await foreach (NativeValue pointer in snapshot.Items)
            Check(pointer.AsAddress().Load() == values[item++], "Range snapshots preserve physical pointer ABI");
        Check(item == values.Length, "Pointer Range membership is complete");
    }

    private static void RepeatedRegions()
    {
        NativeValue[] values = AllValues();
        NativeLayout layout = new(values.Select((value, index) => new NativeField($"f{index}", NativeType.Of(value.Kind))));
        NativeAllocation allocation = new(layout, 37, 5);
        Check(allocation.Layout == layout && allocation.LayoutId == 37 && allocation.ElementCount == 5,
            "Repeated allocations retain their static element identity.");
        for (int item = 0; item < 4; item++)
            for (int field = 0; field < values.Length; field++)
                allocation.Index(item, layout).Field(field).Store(values[field]);
        NativeStorage.CopyRegion(allocation.Address, allocation.Index(1, layout), layout, 4);
        for (int item = 1; item < 5; item++)
            for (int field = 0; field < values.Length; field++)
                Check(allocation.Index(item, layout).Field(field).Load().Equals(values[field]), "Overlapping copy retains every builtin and its roots.");
        NativeStorage.ClearRegion(allocation.Index(2, layout), layout, 1);
        Check(!allocation.Index(2, layout).Field(0).IsInitialized, "Cleared Null is uninitialized, not fake Null.");
        NativeStorage.CopyRegion(allocation.Index(2, layout), allocation.Address, layout, 1);
        Throws<InvalidOperationException>(() => allocation.Field(1).Load(), "Bulk copy preserves uninitialized state.");
        Throws<ArgumentOutOfRangeException>(() => allocation.Index(5, layout), "Indexed elements cannot exceed allocation extent.");
        Throws<ArgumentOutOfRangeException>(() => NativeStorage.ClearRegion(allocation.Index(4, layout), layout, 2), "Regions cannot exceed allocation extent.");
        Throws<ArgumentOutOfRangeException>(() => NativeStorage.CopyRegion(allocation.Address, allocation.Address,
            new NativeLayout(NativeType.Of(NativeKind.Number)), 1), "Bulk copy cannot reinterpret physical field schema.");
        NativeStorage.ClearRegion(allocation.Address.OffsetBy(layout.Extent * 5), layout, 0);

        NativeLayout sum = new(NativeType.Union(NativeType.Of(NativeKind.Number), NativeType.Of(NativeKind.String)));
        NativeAllocation sequence = new(sum, elementCount: 3);
        sequence.Index(0, sum).Field(0).Store(NativeValue.FromNumber(1));
        sequence.Index(1, sum).Field(0).Store(NativeValue.FromString(new string('x', 128)));
        sequence.Index(2, sum).Field(0).Store(NativeValue.FromNumber(3));
        NativeRange snapshot = NativeRange.FromMemory(sequence.Address, 3, sum, new(TypeKind.Number | TypeKind.String));
        sequence.Index(1, sum).Field(0).Store(NativeValue.FromNumber(99));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Check(snapshot.SnapshotValues![1].AsString() == new string('x', 128), "Memory Range freezes membership and keeps moving-GC roots.");
        NativeStorage.CopyRegion(sequence.Address, sequence.Index(1, sum), sum, 2);
        Check(sequence.Index(1, sum).Field(0).Load().AsNumber() == 1
            && sequence.Index(2, sum).Field(0).Load().AsNumber() == 99, "Overlapping union copy has memmove semantics.");
        NativeLayout addressLayout = new(NativeType.Of(NativeKind.Address));
        NativeAllocation aliases = new(addressLayout);
        aliases.Field(0).Store(NativeValue.FromAddress(sequence.Address));
        NativeRange aliasSnapshot = NativeRange.FromMemory(aliases.Address, 1, addressLayout, new(TypeKind.Scope));
        Check(aliasSnapshot.SnapshotValues![0].AsAddress() == sequence.Address, "Memory Range snapshots do not clone referenced allocations.");
        Check(new NativeEnvironment(new([])).Address.Allocation.Layout.Count == 0, "Empty environment exposes its ordinary base allocation.");
        Check(new NativeEnvironment(new([]), layoutId: 42).Address.Allocation.LayoutId == 42,
            "Explicit environment layout identity survives ordinary backing allocation.");
        NativeLayout emptyElement = new(Array.Empty<NativeType>());
        NativeAllocation emptyElements = new(emptyElement, elementCount: 2);
        Check(emptyElements.Index(0, emptyElement) != emptyElements.Index(1, emptyElement), "Empty repeated elements retain distinct logical identities.");
        Throws<ArgumentOutOfRangeException>(() => emptyElements.Index(2, emptyElement), "Empty element arrays obey ordinary bounds.");
        NativeAllocation empty = new(layout, elementCount: 0);
        NativeStorage.ClearRegion(empty.Address, layout, 0);
        Throws<ArgumentOutOfRangeException>(() => empty.Index(0, layout), "Empty allocations have no indexed elements.");
    }

    private static void OwnedRowRanges()
    {
        NativeLayout row = new(NativeType.Of(NativeKind.Bool), NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.Number));
        NativeAllocation source = new(row, elementCount: 2);
        source.Index(0, row).Field(1).Store(NativeValue.FromString("first"));
        source.Index(1, row).Field(1).Store(NativeValue.FromString("second"));
        NativeRange copied = NativeRange.FromMemory(source.Address, 2, row, TypeRef.String, 1);
        source.Index(0, row).Field(1).Store(NativeValue.FromString("replacement"));
        Check(copied.SnapshotValues![0].AsString() == "first", "AoS Range copy freezes only the selected field's membership.");
        NativeRange owned = NativeRange.FromOwnedMemory(source.Address, 2, row, TypeRef.String, 1);
        Check(owned.SnapshotValues![0].AsString() == "replacement" && owned.SnapshotValues[1].AsString() == "second",
            "Owned AoS Range uses its selected field and physical row stride.");
        Throws<ArgumentOutOfRangeException>(() => NativeRange.FromOwnedMemory(source.Address, 2, row, TypeRef.String, 3),
            "Owned Range rejects absent physical field ordinals.");
        Throws<ArgumentException>(() => NativeRange.FromOwnedMemory(source.Address, 2, row, TypeRef.Number, 1),
            "Owned Range cannot reinterpret selected physical field storage.");
        NativeAllocation large = new(row, elementCount: 16384);
        long before = GC.GetAllocatedBytesForCurrentThread();
        NativeRange largeOwned = NativeRange.FromOwnedMemory(large.Address, 16384, row, TypeRef.String, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(largeOwned.SnapshotValues!.Count == 16384 && allocated < 4096,
            "Ownership transfer must not allocate per-row value descriptors or copy the region.");
        Throws<InvalidOperationException>(() => _ = largeOwned.SnapshotValues[0], "Owned Range preserves uninitialized state instead of fake Null.");
        GC.KeepAlive(largeOwned);
    }

    private static void DeferredScalarContracts()
    {
        string[] views = ["process.stdout", "process.stderr", "process.success", "process.exit_code"];
        foreach (string name in views)
        {
            Msync.Core.NativeSignature signature = NativeSignatures.Get(name);
            Check(signature.Effect == NativeEffect.Read, "Process scalar observation retains its Read effect.");
            Check(signature.Behavior == (NativeBehavior.DeferredScalar | NativeBehavior.MayFail),
                "Process scalar views defer demand without invoking a process operation.");
        }
        Check(NativeSignatures.All.Where(signature => (signature.Behavior & NativeBehavior.DeferredScalar) != 0)
            .Select(signature => signature.Name).Order().SequenceEqual(views.Order()), "Deferred scalar admission is explicit, not a process-name wildcard.");
        Check((NativeSignatures.Get("process.wait").Behavior & NativeBehavior.DeferredScalar) == 0,
            "Explicit process.wait remains an eager selected observation.");
    }

    private static void LogicalRangeMetadata()
    {
        NativeType physical = NativeType.Of(NativeKind.Address);
        NativeAllocation payload = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        payload.Field(0).Store(NativeValue.FromNumber(1));
        TypeRef logical = new(TypeKind.Storage, TypeRef.Number);
        NativeRange original = NativeRange.Snapshot([NativeValue.FromAddress(payload.Address)], TypeRef.Scope);
        NativeRange described = original.WithItemType(logical);
        Check(described.ItemType == logical && described.Type == new TypeRef(TypeKind.Range, logical), "Range logical Type descriptor is independent of its carrier ABI.");
        Check(described.PhysicalItemType.Equals(physical) && ReferenceEquals(described.SnapshotValues, original.SnapshotValues),
            "Metadata refinement preserves static physical item ABI and backing storage.");
        NativeRange materialized = NativeRange.Snapshot(described.SnapshotValues!, described.ItemType, described.PhysicalItemType);
        Check(materialized.PhysicalItemType.Equals(physical) && materialized.ItemType == logical, "Materialization must not derive physical layout from logical descriptor metadata.");
        Check(materialized.SnapshotValues![0].AsAddress() == payload.Address, "Logical descriptor materialization cloned shared address identity.");
        payload.Field(0).Store(NativeValue.FromNumber(9));
        Check(materialized.SnapshotValues[0].AsAddress().Field(0).Load().AsNumber() == 9, "Descriptor materialization preserves aliases to payload storage.");
        NativeRange nested = NativeRange.Snapshot([NativeValue.FromRange(materialized)], materialized.Type);
        Check(nested.PhysicalItemType == NativeType.Of(NativeKind.Range) && nested.SnapshotValues![0].AsRange().ItemType == logical,
            "Nested Range metadata retains the nested physical descriptor rather than flattening its ABI.");
        NativeRange empty = NativeRange.Snapshot([], logical, physical);
        Check(empty.SnapshotValues!.Count == 0 && empty.PhysicalItemType == physical, "Empty metadata-described Range requires no fake payload.");
        Throws<ArgumentException>(() => NativeRange.Snapshot([NativeValue.FromNumber(1)], logical, physical),
            "Explicit metadata cannot override the statically declared physical builtin carrier.");
        Throws<ArgumentException>(() => NativeType.FromTypeRef(logical), "Historical Type metadata must not become a runtime Storage carrier.");
        Throws<ArgumentException>(() => NativeRange.Snapshot([NativeValue.FromNumber(1)], TypeRef.String), "Ordinary snapshot logical admission must remain strict.");
    }

    private static async Task DeferredLogicalRangeMetadata()
    {
        NativeType physical = NativeType.Of(NativeKind.Number);
        NativeRange resolved = NativeRange.Snapshot([NativeValue.FromNumber(3)], TypeRef.Number);
        NativeRange deferred = new(resolved.Items, TypeRef.Number, resolve: _ => ValueTask.FromResult(resolved), physicalItemType: physical);
        TypeRef logical = new(TypeKind.Range, TypeRef.Path);
        NativeRange described = deferred.WithItemType(logical);
        NativeRange demanded = await described.Resolve!(CancellationToken.None);
        Check(demanded.ItemType == logical && demanded.PhysicalItemType.Equals(physical),
            "Demand of a metadata-refined Range preserves its logical descriptor independently from resolved physical ABI.");
        Check(demanded.SnapshotValues![0].AsNumber() == 3, "Deferred logical metadata must not reinterpret or copy payload storage.");
    }

    private static void ClosedValues()
    {
        NativeValue[] values = AllValues();
        Check(values.Length == Enum.GetValues<NativeKind>().Length, "Every native kind has a fixture.");
        Check(values.Select(value => value.Kind).Distinct().Count() == values.Length, "Kind fixtures are exhaustive.");
        Check(values[3].AsString() == "可莉𐐀", "String carries Unicode without an object wrapper.");
        NativePath path = values[4].AsPath();
        Check(path.Raw == "raw\\name" && path.WorkingDirectory == "/captured" && path.HomeDirectory == "/home/klee", "Path retains captured provenance.");
        Check(path.NativeText == "raw\\name" && path.LocalSpelling == "local-projection", "Remote path grammar retains raw spelling.");
        Check(path.Connection == new NativeConnection(ConnectionKind.Ssh, "configured-host"), "Path carries the endpoint descriptor.");
        Check(values[5].AsConnection() == new NativeConnection(ConnectionKind.Wsl, "debian"), "Connection remains a native descriptor.");
        Check(values[6].AsFunction().Environment![0].Read().AsNumber() == 3, "Function has a typed captured location.");
        Check(values[8].AsProcess().ProcessId == 4321, "Process descriptor retains its control identity.");
        Check(values[9].AsType().Nullable, "Type metadata is preserved.");
        Throws<InvalidOperationException>(() => values[3].AsNumber(), "Wrong typed access cannot reinterpret roots.");
        Check(typeof(NativeValue).IsValueType && typeof(NativePath).IsValueType && typeof(NativeConnection).IsValueType, "Native descriptors are value carriers.");
    }
    private static void StaticStorage()
    {
        NativeValue[] values = AllValues();
        NativeLayout layout = new(values.Select((value, index) => new NativeField($"field{index}", NativeType.Of(value.Kind))));
        NativeAllocation allocation = new(layout);
        for (int index = 0; index < values.Length; index++)
        {
            allocation.Field(index).Store(values[index]);
            Check(allocation.Field(index).Load() == values[index], $"Monomorphic {values[index].Kind} round trip.");
        }
        Check(layout.RawSize < values.Length * 8 && layout.ReferenceCount == 14, "Layout computes builtin widths and typed roots, including physical pointer metadata.");
        NativeOffset extent = layout.Extent;
        Check(extent == new NativeLayout(layout.Fields.Select(field => new NativeField(field.Name, field.Type))).Extent,
            "Layout does not depend on payload values.");
        NativeAllocation emptyA = new(new NativeLayout(Array.Empty<NativeType>()));
        NativeAllocation emptyB = new(new NativeLayout(Array.Empty<NativeType>()));
        Check(emptyA.Address != emptyB.Address, "Empty constructions retain fresh logical identities.");
        Check(NativeValue.FromAddress(emptyA.Address).AsAddress() == emptyA.Address, "Empty allocation addresses round trip.");
        NativeAllocation uninitialized = new(new NativeLayout(NativeType.Of(NativeKind.String)));
        Throws<InvalidOperationException>(() => uninitialized.Field(0).Load(), "Uninitialized state is not language Null.");
        Throws<ArgumentException>(() => allocation.Field(0).Store(NativeValue.FromNumber(1)), "Fixed Null storage cannot widen.");
        Throws<ArgumentException>(() => NativeStorage.ReadNumber(allocation.Field(3)), "A String root cannot be misread as Number.");
    }
    private static void UnionTransitions()
    {
        NativeValue[] values = AllValues();
        NativeType sum = NativeType.Union(values.Select(value => NativeType.Of(value.Kind)).ToArray());
        NativeAllocation allocation = new(new NativeLayout(sum));
        for (int pass = 0; pass < 10; pass++)
            foreach (NativeValue value in values)
            {
                allocation.Field(0).Store(value);
                Check(allocation.Field(0).Load() == value, $"Closed sum restores {value.Kind}.");
            }
        NativeType nullableString = NativeType.Union(NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.Null));
        NativeAllocation text = new(new NativeLayout(nullableString));
        text.Field(0).Store(NativeValue.FromString("stored"));
        text.Field(0).Store(NativeValue.Null);
        Check(text.Field(0).Load().Kind == NativeKind.Null, "Null is a sum alternative, not an absent GC reference.");
        Throws<ArgumentException>(() => text.Field(0).Store(NativeValue.FromNumber(9)), "A closed union cannot accept an unrelated kind.");
    }
    private static void ExactNumbers()
    {
        NativeAllocation allocation = new(new NativeLayout(NativeType.Of(NativeKind.Number),
            NativeType.Union(NativeType.Of(NativeKind.Number), NativeType.Of(NativeKind.Null))));
        ulong[] edges = [0, 0x8000000000000000, 0x7ff0000000000000, 0xfff0000000000000,
            0x7ff8000000000042, 0xfff8000000000017, 1, 0x7fefffffffffffff];
        Random random = new(901);
        for (int index = 0; index < 4096; index++)
        {
            ulong bits = index < edges.Length ? edges[index] : unchecked((ulong)random.NextInt64() << 1 | (uint)random.Next(2));
            NativeValue value = NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(bits));
            foreach (int field in new[] { 0, 1 })
            {
                allocation.Field(field).Store(value);
                Check(BitConverter.DoubleToUInt64Bits(allocation.Field(field).Load().AsNumber()) == bits,
                    "Storage preserves every IEEE representation bit.");
            }
        }
    }
    private static void AliasesAndCaptures()
    {
        NativeAllocation source = new(new NativeLayout(NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.Address)));
        source.Field(0).Store(NativeValue.FromString("before"));
        source.Field(1).Store(NativeValue.FromAddress(source.Address));
        NativeAddress alias = NativeValue.FromAddress(source.Address).AsAddress();
        alias.Field(0).Store(NativeValue.FromString("after"));
        Check(source.Field(0).Load().AsString() == "after", "Namespace aliases share native field storage.");
        Check(source.Field(1).Load().AsAddress() == source.Address, "Recursive layouts close through references.");
        NativeEnvironment environment = new(new([new NativeCaptureLayout(NativeType.Of(NativeKind.String), true),
            new NativeCaptureLayout(NativeType.Of(NativeKind.String))]),
            [new NativeCapture(source.Field(0)), new NativeCapture(NativeValue.FromString("snapshot"))]);
        source.Field(0).Store(NativeValue.FromString("captured"));
        Check(environment[0].Read().AsString() == "captured" && environment[1].Read().AsString() == "snapshot",
            "Location captures and value captures have different binding semantics.");
        NativeAllocation destination = new(new NativeLayout(NativeType.Of(NativeKind.String)));
        NativeStorage.Copy(source.Field(0), destination.Field(0));
        Check(destination.Field(0).Load().AsString() == "captured" && source.Field(0).Load().AsString() == "captured", "Copy retains the source location.");
        NativeStorage.Move(source.Field(0), destination.Field(0));
        Throws<InvalidOperationException>(() => source.Field(0).Load(), "Move leaves source uninitialized, not Null.");
        Check(destination.Field(0).Load().AsString() == "captured", "Move preserves native payload.");
    }
    private static void TrustedOffsets()
    {
        NativeValue[] values = AllValues();
        NativeLayout layout = new(values.Select((value, index) => new NativeField($"field{index}", NativeType.Of(value.Kind))));
        NativeAllocation allocation = new(layout, 712);
        Check(allocation.LayoutId == 712, "Executable layout identity is explicit metadata.");
        for (int index = 0; index < values.Length; index++) allocation.Field(index).Store(values[index]);
        NativeAddress start = allocation.Address;
        Check(NativeStorage.LoadBool(start.OffsetBy(layout[1].Offset)), "Bool trusted load uses direct offsets.");
        Check(BitConverter.DoubleToUInt64Bits(NativeStorage.LoadNumber(start.OffsetBy(layout[2].Offset))) == 0x8000000000000000,
            "Number trusted load preserves signed zero.");
        Check(NativeStorage.LoadString(start.OffsetBy(layout[3].Offset)) == values[3].AsString(), "String trusted load uses typed roots.");
        Check(NativeStorage.LoadPath(start.OffsetBy(layout[4].Offset)) == values[4].AsPath(), "Path trusted load is complete.");
        Check(NativeStorage.LoadConnection(start.OffsetBy(layout[5].Offset)) == values[5].AsConnection(), "Connection trusted load is complete.");
        Check(NativeStorage.LoadFunction(start.OffsetBy(layout[6].Offset)) == values[6].AsFunction(), "Function trusted load preserves capture environment.");
        Check(ReferenceEquals(NativeStorage.LoadRange(start.OffsetBy(layout[7].Offset)), values[7].AsRange()), "Range trusted load preserves producer identity.");
        Check(ReferenceEquals(NativeStorage.LoadProcess(start.OffsetBy(layout[8].Offset)), values[8].AsProcess()), "Process trusted load preserves work identity.");
        Check(NativeStorage.LoadType(start.OffsetBy(layout[9].Offset)) == values[9].AsType(), "Type trusted load preserves metadata.");
        Check(NativeStorage.LoadAddress(start.OffsetBy(layout[10].Offset)) == values[10].AsAddress(), "Address trusted load preserves both coordinate halves.");
        NativeAddress typedString = start.OffsetBy(layout[3].Offset, layout[3].Type);
        NativeValue pointerValue = NativeValue.FromAddress(typedString);
        NativeCapture capture = new(pointerValue.AsAddress(), NativeType.Of(NativeKind.String));
        Check(capture.Read().AsString() == values[3].AsString(), "Capture signature supplies a location's static type, not a runtime object.");
        NativeAddress rangePointer = start.OffsetBy(layout[7].Offset, layout[7].Type);
        NativeValue addressValue = NativeValue.FromAddress(rangePointer);
        Check(addressValue.AsAddress().Offset == rangePointer.Offset && rangePointer.Offset.References > 0,
            "Address sum values preserve nonzero GC-reference coordinates.");
        NativeAllocation pointerSlot = new(new NativeLayout(NativeType.Of(NativeKind.Address)));
        NativeStorage.StoreAddress(pointerSlot.Field(0), rangePointer);
        Check(NativeStorage.LoadAddress(pointerSlot.Field(0)) == rangePointer,
            "Monomorphic pointer storage preserves raw and reference offsets.");
        Check(layout.Fields.Select(field => field.Offset.Bytes).Distinct().Count() == layout.Count,
            "Zero-payload and reference-only fields have distinct addressable locations.");
        NativeLayout repeated = NativeLayout.Repeat(NativeType.Of(NativeKind.Number), 16384);
        Check(repeated.RawSize == 16384 * sizeof(double) && repeated.ReferenceCount == 0,
            "Repeated monomorphic values have no universal union slots or per-element roots.");
        NativeAllocation sequence = new(repeated);
        for (int index = 0; index < repeated.Count; index++)
            NativeStorage.StoreNumber(sequence.Address.OffsetBy(repeated[index].Offset), index + 0.5);
        for (int index = 0; index < repeated.Count; index++)
            Check(NativeStorage.LoadNumber(sequence.Address.OffsetBy(repeated[index].Offset)) == index + 0.5,
                "Typed array offsets form an ordinary stride.");
    }
    private static void EmptyAndPendingMetadata()
    {
        Check(!NativeType.Of(NativeKind.Null).Accepts((NativeKind)64), "Unknown native tags cannot alias a valid closed alternative.");
        Check(NativeType.Never.IsNever && !NativeType.Never.IsUnion && !NativeType.Never.Accepts(NativeValue.Null),
            "Never is uninhabited metadata, not a Null value.");
        NativeAllocation impossible = new(new NativeLayout(NativeType.Never));
        Throws<InvalidOperationException>(() => impossible.Field(0).Load(), "Never storage starts uninitialized.");
        Throws<ArgumentException>(() => impossible.Field(0).Store(NativeValue.Null), "No value can initialize Never storage.");
        NativeEnvironmentLayout layout = new([new NativeCaptureLayout(NativeType.Of(NativeKind.String))]);
        NativeEnvironment pending = new(layout);
        Check(!pending.TryGetCapture(0, out _) && !pending.CaptureAddress(0).IsInitialized,
            "A pending capture is typed uninitialized storage, not a fabricated value.");
        pending.Initialize(0, new NativeCapture(NativeValue.FromString("ready")));
        Check(pending.TryGetCapture(0, out NativeCapture capture) && capture.Read().AsString() == "ready",
            "Actual capture completion publishes into its original typed location.");
        NativeAllocation recursiveLocation = new(new NativeLayout(NativeType.Of(NativeKind.Function)));
        NativeCapture recursive = new(recursiveLocation.Field(0));
        Check(!recursive.TryRead(out _), "Uninitialized recursive captures can be retained without being read.");
    }
    private static async Task StreamsAndProcesses()
    {
        NativeValue[] values = AllValues();
        NativeRange range = NativeRange.Snapshot(values, TypeRef.Any);
        int index = 0;
        await foreach (NativeValue value in range.Items) Check(value == values[index++], "Range publishes all native kinds.");
        Check(index == values.Length && range.RetainedValues.Count == values.Length, "Snapshot membership and roots are explicit.");
        NativeRange numbers = NativeRange.Snapshot(Enumerable.Range(0, 1024).Select(value => NativeValue.FromNumber(value)), TypeRef.Number);
        index = 0;
        await foreach (NativeValue number in numbers.Items) Check(number.AsNumber() == index++, "Monomorphic Range snapshot uses typed native storage.");
        NativeRange refined = range.WithItemType(TypeRef.Any);
        Check(ReferenceEquals(refined, range), "An unchanged item type does not allocate another Range state.");
        NativeRange empty = NativeRange.Snapshot([], new(TypeKind.Never));
        Check(empty.SnapshotValues!.Count == 0, "Empty Never membership does not require a fabricated element type.");
        NativeRange narrowedEmpty = empty.WithItemType(TypeRef.Path);
        Check(ReferenceEquals(narrowedEmpty.Items, empty.Items) && ReferenceEquals(narrowedEmpty.SnapshotValues, empty.SnapshotValues)
            && ReferenceEquals(narrowedEmpty.RetainedValues, empty.RetainedValues) && narrowedEmpty.ItemType == TypeRef.Path,
            "Declared item refinement preserves snapshot storage without enumeration.");
        Throws<ArgumentException>(() => NativeRange.Snapshot([NativeValue.FromString("wrong")], TypeRef.Number),
            "Snapshot cannot widen its declared native element type.");
        TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeProcess process = new(-1, "", "") { Completion = completion.Task };
        ValueTask<NativeProcess> wait = process.WaitAsync();
        Check(!wait.IsCompleted, "An incomplete Process does not pretend to be ready.");
        completion.SetResult(new(23, "done", "selected failure"));
        NativeProcess result = await wait;
        Check(result.ExitCode == 23 && result.StandardOutput == "done", "Process resumes its actual completion.");
    }
    private static void GcReferences()
    {
        (NativeAllocation allocation, WeakReference<string> retained) = RetainedString();
        Collect();
        CheckRetained(allocation, retained);
        WeakReference<string> released = ReplacedString(allocation);
        Collect();
        Check(!released.TryGetTarget(out _), "Union replacement releases obsolete payload roots.");
        Check(allocation.Field(0).Load().Kind == NativeKind.Null, "Cleared roots do not change the selected Null alternative.");
        GC.KeepAlive(allocation);
        Check(Marshal.SizeOf<NativeOffset>() == 8, "Address offset coordinates have fixed integer representation.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckRetained(NativeAllocation allocation, WeakReference<string> retained)
    {
        Check(retained.TryGetTarget(out _), "Typed reference union remains visible to a moving GC.");
        Check(allocation.Field(0).Load().AsString().Length == 2048, "A rooted String survives collection and typed load.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (NativeAllocation, WeakReference<string>) RetainedString()
    {
        string value = new('k', 2048);
        NativeAllocation allocation = new(new NativeLayout(NativeType.Union(NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.Null))));
        allocation.Field(0).Store(NativeValue.FromString(value));
        return (allocation, new(value));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> ReplacedString(NativeAllocation allocation)
    {
        WeakReference<string> reference = new(allocation.Field(0).Load().AsString());
        allocation.Field(0).Store(NativeValue.Null);
        return reference;
    }
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
