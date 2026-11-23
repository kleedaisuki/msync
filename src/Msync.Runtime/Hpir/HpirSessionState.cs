using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirSessionState : IDisposable
{
    internal readonly HpirDirectoryAuthority Directory;

    private readonly ExecutionOptions? options;

    internal HpirSessionState(ExecutionOptions? options)
    {
        this.options = options;
        Directory = new(options, sessionAnchor: DirectoryAnchor);
    }
    private readonly Dictionary<int, PersistentGlobal> cells = [], statics = [];
    private HpirSessionSubmission? active;
    private HpirHeap? heap;
    private HpirWork? work;
    private RuntimeTimeline? workTimeline;
    private object? compilerOwner;
    private nint* globals, literalRoots;
    private HpirPathAnchors literalAnchors;
    private nint resultRecord;
    private HpirSessionValue? presentedScope;
    private IReadOnlyList<TypeRef> types = [];
    private IReadOnlySet<int>? storedTextSlots;
    private IReadOnlyDictionary<int, HpirSessionField[]> scopeLayouts = new Dictionary<int, HpirSessionField[]>();
    private IReadOnlyDictionary<int, HpirSessionSnapshot> snapshotLayouts = new Dictionary<int, HpirSessionSnapshot>();
    private int textCount, textLayout, generation;

    private readonly record struct PersistentGlobal(nint Address, nint Flag, TypeKind Kind, bool Aliased, HpirSessionUnion? Union);

    internal int GlobalCount => cells.Count + statics.Count + (pathRecords?.Count ?? 0);

    internal void Begin(HpirSessionSubmission submission, nint result)
    {
        object owner = submission.Owner ?? throw new ArgumentException("Expected a compiler-owned HPIR submission.", nameof(submission));
        if (compilerOwner is not null && !ReferenceEquals(compilerOwner, owner))
            throw new InvalidOperationException("The HPIR submission belongs to a different compilation session.");
        if (submission.Generation <= generation)
            throw new InvalidOperationException("An HPIR session submission can execute only once and in generation order.");
        compilerOwner = owner;
        generation = submission.Generation;
        types = submission.Witnesses;
        storedTextSlots = submission.TextSlots;
        scopeLayouts = submission.ScopeLayouts;
        snapshotLayouts = submission.SnapshotLayouts;
        textLayout = submission.Program.TextLayout;
        resultRecord = result;
        active = submission;
    }

    internal void BindResult(HpirFunction entry, byte* frame)
    {
        if (resultRecord != 0) *(nint*)(frame + entry.Parameters[0].Offset) = resultRecord;
    }

    internal HpirWork? BeginWork(RuntimeTimeline? timeline, CancellationToken token)
    {
        if (work is null && !active!.StartsProcesses) return null;
        work ??= new(default, sessionProcesses: true);
        workTimeline = timeline;
        work.BeginSubmission(token);
        return work;
    }

    internal HpirSessionValue ObserveResult(TypeKind kind, ulong bits, RuntimeTimelineScope? trace) => presentedScope ??
        (active!.ResultUnion is { } union ? ProjectField(resultRecord, new("<result>", 0, kind, union), new(active.ScopeLayouts))
            : kind == TypeKind.Scope ? ObserveScope((nint)bits, trace)
            : kind == TypeKind.Process ? ObserveProcess((nint)bits)
            : resultRecord == 0 ? Scalar(kind, bits) : Aggregate(kind, resultRecord));

    private HpirSessionValue ObserveProcess(nint handle) =>
        new(TypeKind.Process, 0, Process: work!.ObserveSessionProcess(((HpirProcessHandle*)handle)->Job));

    internal HpirHeap LinkHeap(HpirProgram program, RuntimeTimeline? timeline)
    {
        heap ??= new(program.Layouts);
        heap.AdoptSessionLayouts(program.Layouts, this);
        if (heap.WantsCollection)
        {
            using RuntimeTimelineScope? trace = timeline?.Begin("hpir.session.collect_between_submissions");
            int marked = heap.MarkGlobals();
            HpirHeap.Sweep sweep = heap.Collect();
            trace?.SetMetric("roots.globals", marked);
            trace?.SetMetric("heap.live_bytes", sweep.LiveBytes);
            trace?.SetMetric("heap.reclaimed_bytes", sweep.ReclaimedBytes);
        }
        return heap;
    }

    internal nint* LinkGlobals(HpirProgram program, HpirHeap owner, RuntimeTimeline? timeline)
    {
        if (program.Globals.Count == 0 && active!.PreparedPathOrigins is not { Count: > 0 }) return null;
        using RuntimeTimelineScope? trace = timeline?.Begin("hpir.session.link.globals");
        try
        {
            trace?.SetMetric("session.generation", generation);
            CapturePreparedPaths(program.Context, trace);
            if (program.Globals.Count == 0) return null;
            globals = (nint*)NativeMemory.Alloc(checked((nuint)program.Globals.Count * 2), (nuint)sizeof(nint));
            int linked = 0, paths = 0, linkedPaths = 0;
            for (int i = 0; i < program.Globals.Count; ++i)
            {
                HpirGlobal global = program.Globals[i];
                if (active!.PathRelocations?.TryGetValue(i, out var path) == true)
                {
                    globals[i * 2] = LinkPath(path, program, global.Layout, owner, ref linkedPaths);
                    globals[i * 2 + 1] = 0;
                    ++paths;
                    continue;
                }
                bool cell = active!.CellRelocations.TryGetValue(i, out var info);
                Dictionary<int, PersistentGlobal> addresses = cell ? cells : statics;
                int key = cell ? info.Slot : global.Declaration;
                if (!addresses.TryGetValue(key, out var place))
                {
                    var (allocation, flag) = owner.AllocateGlobal(global.Layout);
                    place = new(allocation.Address, flag, info.Kind, info.Aliased, info.Union);
                    addresses.Add(key, place);
                    ++linked;
                }
                globals[i * 2] = place.Address;
                globals[i * 2 + 1] = place.Flag;
            }
            trace?.SetMetric("globals.linked", linked);
            trace?.SetMetric("globals.reused", program.Globals.Count - paths - linked);
            trace?.SetMetric("paths.linked", linkedPaths);
            trace?.SetMetric("paths.reused", paths - linkedPaths);
            return globals;
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw;
        }
    }

    internal HpirLiteralBank LinkLiterals(HpirProgram program, HpirHeap owner, RuntimeTimeline? timeline)
    {
        BindingContext? context = program.PathAnchors ? program.Context : null;
        int typeCells = program.TypeCells.Count;
        int count = checked(program.TextLiterals.Count + typeCells + (context is null ? 0 : 2));
        if (count == 0) return default;
        using RuntimeTimelineScope? trace = timeline?.Begin("hpir.session.link.literals");
        nuint bytes = checked((nuint)count * (nuint)sizeof(nint) + (nuint)typeCells * (nuint)sizeof(ulong));
        nint* addresses = (nint*)NativeMemory.Alloc(bytes);
        try
        {
            int i = 0;
            foreach (string text in program.TextLiterals) addresses[i++] = Text(text, program.TextLayout, owner);
            HpirLiteralBank.WriteTypeCells(program.TypeCells, addresses + i, (byte*)(addresses + count));
            i += typeCells;
            HpirPathAnchors anchors = default;
            if (context is not null)
            {
                nint directory = addresses[i++] = PathAnchor(context.WorkingDirectory, program.TextLayout, owner);
                nint home = addresses[i] = PathAnchor(context.HomeDirectory, program.TextLayout, owner);
                anchors = new(directory, home);
            }
            literalRoots = addresses;
            literalAnchors = anchors;
            textCount = program.TextLiterals.Count;
            trace?.SetMetric("literals.entries", textCount);
            trace?.SetMetric("literals.types", typeCells);
            trace?.SetMetric("literals.path_anchors", context is null ? 0 : 2);
            return new(addresses, anchors);
        }
        catch (Exception error)
        {
            NativeMemory.Free(addresses);
            trace?.Fail(error);
            throw;
        }
    }

    private static nint Text(string text, int layout, HpirHeap heap)
    {
        nint address = heap.Allocate(layout, HpirTextStorage.Elements(text.Length), clear: false).Address;
        HpirTextStorage.Write(address, text);
        return address;
    }

    internal int MarkRoots(HpirHeap owner)
    {
        int marked = MarkPaths(owner);
        foreach (var value in cells.Values) if (owner.Mark(value.Address)) ++marked;
        foreach (var value in statics.Values) if (owner.Mark(value.Address)) ++marked;
        for (int i = 0; i < textCount; ++i)
            if (owner.Mark(literalRoots[i])) ++marked;
        if (owner.Mark(literalAnchors.Directory)) ++marked;
        if (owner.Mark(literalAnchors.Home)) ++marked;
        return marked;
    }

    internal bool IsInitialized(int slot) => cells.TryGetValue(slot, out var cell) && *(byte*)cell.Flag != 0;

    internal HpirSessionValue? ObserveBinding(int slot, bool recoverScopes = false)
    {
        if (!cells.TryGetValue(slot, out var cell) || *(byte*)cell.Flag == 0) return null;
        nint address = cell.Aliased ? *(nint*)cell.Address : cell.Address;
        if (cell.Union is { } union)
        {
            TypeKind kind = UnionKind(address);
            if (kind == TypeKind.Scope) return recoverScopes ? RecoverScope(*(nint*)(address + union.PointerOffset)) : null;
            return kind is TypeKind.Null or TypeKind.Bool or TypeKind.Number or TypeKind.String
                or TypeKind.Type or TypeKind.Path or TypeKind.Function or TypeKind.Connection
                ? ProjectField(address, new("", 0, cell.Kind, union), null) : null;
        }
        return cell.Kind switch
        {
            TypeKind.Null => Scalar(cell.Kind, 0),
            TypeKind.Bool => Scalar(cell.Kind, *(byte*)address),
            TypeKind.Number or TypeKind.Type or TypeKind.Connection => Scalar(cell.Kind, *(ulong*)address),
            TypeKind.String => ObserveBindingText(slot, *(nint*)address),
            TypeKind.Function or TypeKind.Path => Aggregate(cell.Kind, address),
            TypeKind.Scope when recoverScopes => RecoverScope(*(nint*)address),
            TypeKind.Range => ObserveRange(*(nint*)address),
            _ => null
        };
    }

    private HpirSessionValue ObserveRange(nint address) => new(TypeKind.Range, 0)
    {
        Snapshot = address != 0 && snapshotLayouts.TryGetValue(HpirHeap.LayoutOf(address), out var shape) ? shape : null
    };

    private HpirSessionValue ObserveBindingText(int slot, nint value)
    {
        if (storedTextSlots?.Contains(slot) == true && !HpirHeap.HasLayout(value, textLayout))
        {
            var completion = (HpirTextCompletion*)value;
            if (completion->Kind != HpirTextCompletionKind.Ready)
                return new(TypeKind.String, 0, RequiresDemand: true);
            value = completion->Text;
        }
        return Scalar(TypeKind.String, (ulong)value);
    }

    private static HpirSessionValue Aggregate(TypeKind kind, nint address) => kind switch
    {
        TypeKind.Function => ObserveCallable(address),
        TypeKind.Path => new(kind, 0, Path: (*(HpirPathValue*)address).ToHost(contextual: true)),
        _ => throw new InvalidOperationException("Expected an entitled physical result record.")
    };

    private static HpirSessionValue ObserveCallable(nint address)
    {
        ulong identity = *(ulong*)(address + HpirCallableAbi.Code);
        // Concrete callable record: code, environment, native name.
        // Only the dispatch identity crosses this semantic boundary.
        nint nativeName = *(nint*)(address + HpirCallableAbi.NativeName);
        return new(TypeKind.Function, identity,
            nativeName == 0 ? null : new string(HpirTextStorage.Read(nativeName)));
    }

    internal HpirSessionValue Scalar(TypeKind kind, ulong bits) => kind switch
    {
        TypeKind.String => new(kind, 0, new string(HpirTextStorage.Read((nint)bits))),
        TypeKind.Type => new(kind, 0, Type: types[checked((int)bits) - 1]),
        TypeKind.Connection => new(kind, 0, Connection: new HpirConnectionValue((nint)bits).ToHost()),
        _ => new(kind, bits)
    };

    internal void End()
    {
        active = null;
        presentedScope = null;
        resultRecord = 0;
        literalRoots = null;
        literalAnchors = default;
        textCount = 0;
        NativeMemory.Free(globals);
        globals = null;
        heap?.TrackWork(work);
    }

    public void Dispose()
    {
        try
        {
            if (work?.Settle(abort: true, new(workTimeline, null)) is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            work = null;
            End();
            heap?.Dispose();
            heap = null;
            types = [];
            cells.Clear();
            statics.Clear();
            ClearPaths();
            Directory.Dispose();
        }
    }
}
