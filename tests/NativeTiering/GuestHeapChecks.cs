using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class GuestHeapChecks
{
    internal static unsafe void Run()
    {
        using NativeGuestHeap heap = new();
        nint live = heap.Allocate(24).Address;
        nint firstDead = heap.Allocate(24).Address;
        nint secondDead = heap.Allocate(24).Address;
        new Span<byte>((void*)live, 48).Fill(0x5a);
        new Span<byte>((void*)firstDead, 48).Fill(0xff);
        new Span<byte>((void*)secondDead, 48).Fill(0xff);
        NativeGuestHeap.Sweep swept = heap.Collect(arena => { arena.Mark(live); });
        if (swept.Objects != 2 || swept.Reclaimed != 96 || swept.LiveBytes != 64)
            throw new InvalidOperationException("Unexpected live and reclaimed block extents.");
        NativeGuestHeap.Allocation merged = heap.Allocate(56);
        if (!merged.Reused || merged.Address != firstDead || merged.Reserved != 0)
            throw new InvalidOperationException("The two adjacent free blocks did not coalesce.");
        if (new ReadOnlySpan<byte>((void*)merged.Address, 112).ContainsAnyExcept((byte)0)
            || new ReadOnlySpan<byte>((void*)live, 48).ContainsAnyExcept((byte)0x5a))
            throw new InvalidOperationException("Reuse must clear the new allocation and preserve the retained one.");

        Action<NativeGuestHeap> trace = arena => { arena.Mark(live); };
        heap.Collect(trace);
        _ = heap.Allocate(24);
        heap.Collect(trace);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1024; iteration++)
        {
            NativeGuestHeap.Allocation reused = heap.Allocate(24);
            if (!reused.Reused || reused.Reserved != 0)
                throw new InvalidOperationException("The raw allocator grew despite completed reclamation.");
            heap.Collect(trace);
        }
        if (GC.GetAllocatedBytesForCurrentThread() != before)
            throw new InvalidOperationException("Raw allocation and sweep allocated CLR objects.");
        RawEdgesAndViews();
    }

    private static void RawEdgesAndViews()
    {
        using NativeGuestHeap heap = new(rawEdges: true);
        NativeLayout layout = new(NativeType.Of(NativeKind.Number), NativeType.Of(NativeKind.Address));
        NativeAllocation owner = heap.NewObject(layout, 1, 1, 0, default);
        NativeAllocation target = heap.NewObject(layout, 1, 1, 0, default);
        owner.Field(1).Store(NativeValue.FromAddress(target.Address));
        if (!owner.HeapRoots.IsEmpty || owner.HeapEdges[0] != target.Storage.RawAddress
            || owner.Field(1).Load().AsAddress() != target.Address)
            throw new InvalidOperationException("A guest-to-guest edge was mirrored to a CLR reference.");
        NativeAllocation host = new(layout);
        NativeStorage.Copy(owner.Field(1), host.Field(1));
        owner.Field(1).Clear();
        if (owner.HeapEdges[0] != 0 || host.Field(1).Load().AsAddress() != target.Address)
            throw new InvalidOperationException("Copying from the native bank changed guest identity.");
        NativeStorage.Copy(host.Field(1), owner.Field(1));
        NativeAddress interior = target.Field(0);
        host.Field(1).Store(NativeValue.FromAddress(interior));
        NativeStorage.Copy(host.Field(1), owner.Field(1));
        if (!NativeStorageTypeWord.IsType(owner.HeapEdges[1]))
            throw new InvalidOperationException("A source-typed address must encode its contract in the native lane.");
        host.Field(1).Clear();
        NativeStorage.Copy(owner.Field(1), host.Field(1));
        NativeAddress restored = host.Field(1).Load().AsAddress();
        if (restored != interior || restored.StorageType != interior.StorageType)
            throw new InvalidOperationException("Moving an interior address between banks lost its storage type.");
        NativeLayout emptyLayout = new(Array.Empty<NativeField>());
        NativeAllocation empty = heap.NewObject(emptyLayout, 2, 1, 0, default);
        NativeAllocation otherEmpty = heap.NewObject(emptyLayout, 2, 1, 0, default);
        NativeStorage.StoreAddressCompiled(target.Field(1), empty.Address);
        if (empty.Address == otherEmpty.Address || empty.Storage.RawAddress == otherEmpty.Storage.RawAddress
            || target.HeapEdges[0] != empty.Storage.RawAddress || NativeStorage.LoadAddressDirect(target.Field(1)) != empty.Address)
            throw new InvalidOperationException("Empty objects must also have distinct raw addresses.");
        Action<NativeGuestHeap> trace = arena =>
        {
            arena.TraceObject(owner.Storage.RawAddress);
            while (arena.TakeObject(out nint current))
                foreach (nint edge in arena.References!.Resolve(current).HeapEdges)
                    if (NativeGuestObjectTable.IsObjectEdge(edge)) arena.TraceObject(edge);
        };
        heap.Collect(trace);
        if (owner.Field(1).Load().AsAddress() != interior || NativeStorage.LoadAddressDirect(target.Field(1)) != empty.Address)
            throw new InvalidOperationException("Sweeping changed the identity of a retained edge.");

        NativeLayout callables = new(NativeType.Of(NativeKind.Function));
        NativeAllocation holder = heap.NewObject(callables, 4, 1, 0, default);
        NativeEnvironmentLayout schema = new([new(NativeType.Of(NativeKind.Number))]);
        NativeEnvironment original = NativeEnvironment.View(schema, target.Address);
        NativeEnvironment another = NativeEnvironment.View(schema, target.Address);
        holder.Field(0).Store(NativeValue.FromFunction(new(1, original)));
        if (!holder.HeapRoots.IsEmpty || holder.HeapEdges[0] == 0
            || !ReferenceEquals(holder.Field(0).Load().AsFunction().Environment, original))
            throw new InvalidOperationException("The closure was not stored as a raw edge.");
        holder.Field(0).Store(NativeValue.FromFunction(new(1, another)));
        if (!NativeHostReference.IsHost(holder.HeapEdges[0]) || !ReferenceEquals(holder.Field(0).Load().AsFunction().Environment, another))
            throw new InvalidOperationException("Two independent environment views lost their identity.");
        heap.Collect(trace);

        NativeAllocation spare = heap.NewObject(layout, 2, 1, 1, default);
        spare.Field(1).Store(NativeValue.FromAddress(target.Address));
        heap.Collect(trace);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        {
            NativeAllocation next = heap.NewObject(layout, 3, 1, i, default);
            if (!ReferenceEquals(next, spare) || next.HeapEdges[0] != 0 || next.AllocationSite != i)
                throw new InvalidOperationException("The recycled view retained state from the previous guest object.");
            next.Field(1).Store(NativeValue.FromAddress(owner.Address));
            heap.Collect(trace);
        }
        if (GC.GetAllocatedBytesForCurrentThread() != before)
            throw new InvalidOperationException("Reusing the raw object, edges and descriptor allocated CLR objects.");

        NativeAllocation closure = heap.NewObject(schema.Storage, 1, 1, 0, default);
        NativeValue code = NativeValue.FromMachineInteger(new LlvmAPInt(64, 2), NativeMachineType.Integer(64, 8));
        Func<NativeAddress, NativeEnvironmentLayout> resolveSchema = _ => schema;
        NativeValue initial = NativeFunctionParts.ComposeGuest(code, NativeValue.FromAddress(closure.Address),
            NativeValue.Null, resolveSchema, out bool? alreadyOpen);
        NativeValue distinct = NativeFunctionParts.ComposeGuest(code, NativeValue.FromAddress(closure.Address),
            NativeValue.Null, resolveSchema, out bool? duplicated);
        NativeEnvironment saved = initial.AsFunction().Environment!;
        if (alreadyOpen != false || duplicated is not null || initial.Equals(distinct))
            throw new InvalidOperationException("Composing two closures over a single live payload merged their identities.");
        heap.Collect(trace);
        long beforeClosures = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        {
            NativeAllocation next = heap.NewObject(schema.Storage, 1, 1, 0, default);
            NativeValue function = NativeFunctionParts.ComposeGuest(code, NativeValue.FromAddress(next.Address),
                NativeValue.Null, resolveSchema, out bool? reused);
            NativeEnvironment environment = function.AsFunction().Environment!;
            if (reused != true || !ReferenceEquals(environment, saved) || environment.Address != next.Address)
                throw new InvalidOperationException("A dead closure's view was not reused on its next lifetime.");
            heap.Collect(trace);
        }
        if (GC.GetAllocatedBytesForCurrentThread() != beforeClosures)
            throw new InvalidOperationException("Reusing a guest closure allocated CLR objects.");

        NativeLayout texts = new(NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.String), NativeType.Of(NativeKind.String));
        NativeAllocation messages = heap.NewObject(texts, 4, 1, 0, default);
        NativeRootBank bank = messages.Storage.roots;
        messages.Field(0).Store(NativeValue.FromString("first"));
        messages.Field(1).Store(NativeValue.FromString("second"));
        bank.CopyTo(0, bank, 1, 2);
        if (!messages.HeapRoots.IsEmpty || bank.ReadHost(1).Text != "first" || bank.ReadHost(2).Text != "second")
            throw new InvalidOperationException("The native lane lost an overlapping host-slot copy.");
        nint boundaryKey = bank.GuestAddress + 2 * IntPtr.Size;
        heap.Collect(trace);
        if (heap.References!.HostReferences.Read(boundaryKey).Value is not null)
            throw new InvalidOperationException("Sweeping a native block retained a host boundary entry.");

        heap.References.AllocateText = text => heap.NewText(text, default);
        NativeAllocation nativeText = heap.NewObject(texts, 4, 1, 0, default);
        nativeText.Field(0).Store(NativeValue.FromString("β🙂"));
        NativeValue span = nativeText.Field(0).Load();
        nativeText.Field(1).Store(span);
        nint textEdge = nativeText.HeapEdges[0];
        if (!NativeGuestObjectTable.IsTextEdge(textEdge) || nativeText.HeapEdges[1] != textEdge
            || !span.AsStringSpan().SequenceEqual("β🙂"))
            throw new InvalidOperationException("Guest text transfer did not share one native UTF-16 block.");
        heap.Collect(arena =>
        {
            arena.Mark(nativeText.Storage.RawAddress);
            arena.Mark(textEdge & ~7);
        });
        if (!nativeText.Field(1).Load().AsStringSpan().SequenceEqual("β🙂"))
            throw new InvalidOperationException("A live native text edge was lost on sweep.");
    }

    internal static async Task HostLifetimeAsync()
    {
        NativeValue value = await ExportAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        NativeAddress scope = value.AsAddress();
        NativeAddress left = scope.Field(0).Load().AsAddress();
        NativeAddress right = scope.Field(1).Load().AsAddress();
        if (left != right || left.Field(0).Load().AsNumber() != 7)
            throw new InvalidOperationException("The embedded allocator lost the exported object or its alias.");
        left.Field(0).Store(NativeValue.FromNumber(19));
        if (right.Field(0).Load().AsNumber() != 19)
            throw new InvalidOperationException("The exported aliases no longer share one cell.");
    }

    private static async Task<NativeValue> ExportAsync()
    {
        NativeProgram program = Compiler.Compile("""
            let node = object { count: 7 };
            object { left: node, right: node }
            """, "embedded-guest-heap.msync");
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(program, new() { Profile = profile });
        NativeValue result = await interpreter.ExecuteAsync();
        if (!profile.Drain().Any(sample => sample.Name == "vm.function" && sample.Metrics["native_heap_objects"] > 0))
            throw new InvalidOperationException("Embedded execution bypassed the guest heap.");
        return result;
    }
}
