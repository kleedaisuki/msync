using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirSessionState
{
    internal long ItemsDisplayed { get; private set; }

    internal void BeginPresentation() => ItemsDisplayed = 0;

    internal void ObserveResultFrontier(ulong result, HpirProgram program, HpirHeap heap, HpirWork? work,
        HpirStack.Frame* frame, int pc, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts,
        CancellationToken token)
    {
        if (!active!.DisplaysFutureScope) return;
        using var roots = heap.DeferCollection();
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(frame->Function, pc), frame->Function, pc);
        using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.session.presentation.scope_fields");
        ScopeObservation observation = new(scopeLayouts)
        {
            Frontier = new(work, site, token, new(program, heap, frame, pc, diagnostics, counts))
        };
        try { presentedScope = ScopeValue((nint)result, observation); }
        catch (Exception error) { trace?.Fail(error); throw; }
        finally
        {
            trace?.SetMetric("scope.records", observation.Records);
            trace?.SetMetric("scope.fields", observation.Fields);
            trace?.SetMetric("scope.demands", observation.Demands);
        }
    }

    internal void DisplayItem(nint address, HpirSessionField field)
    {
        var writer = options?.Output ?? Console.Out;
        if (field.Union is null && field.Kind == TypeKind.String)
            HpirSessionPresentation.Write(writer, HpirTextStorage.Read(*(nint*)address));
        else
        {
            TypeKind kind = field.Union is null ? field.Kind : UnionKind(address);
            HpirSessionPresentation.Write(writer, ProjectField(address, field,
                kind == TypeKind.Scope ? new(scopeLayouts) : null));
        }
        ++ItemsDisplayed;
    }

    private HpirSessionValue ObserveScope(nint address, RuntimeTimelineScope? trace)
    {
        ScopeObservation observation = new(active!.ScopeLayouts);
        HpirSessionValue result = ScopeValue(address, observation);
        trace?.SetMetric("scope.records", observation.Records);
        trace?.SetMetric("scope.fields", observation.Fields);
        trace?.SetMetric("scope.cycles", observation.Cycles);
        return result;
    }

    private HpirSessionValue? RecoverScope(nint address)
    {
        if (address == 0 || HpirHeap.SessionSite(address) == 0) return null;
        return ScopeValue(address, new(scopeLayouts, recovery: true));
    }

    private sealed class ScopeObservation(IReadOnlyDictionary<int, HpirSessionField[]> layouts, bool recovery = false)
    {
        internal readonly IReadOnlyDictionary<int, HpirSessionField[]> Layouts = layouts;
        internal readonly bool Recovery = recovery;
        internal readonly HashSet<nint> Path = [];
        internal ScopeFrontier? Frontier;
        internal int Records, Fields, Cycles, Demands;
    }

    private readonly record struct ScopeFrontier(HpirWork? Work, HpirWorkSite Site, CancellationToken Token,
        HpirTextDemandContext Text);

    private static readonly HpirSessionScope ScopeCycle = new([], Cycle: true);

    private HpirSessionValue ScopeValue(nint address, ScopeObservation observation)
    {
        if (address == 0) return Scalar(TypeKind.Null, 0);
        if (!observation.Path.Add(address))
        {
            ++observation.Cycles;
            return new(TypeKind.Scope, 0, Scope: observation.Recovery ? ScopeCycle with { Identity = address } : ScopeCycle);
        }
        int layout = HpirHeap.LayoutOf(address);
        if (!observation.Layouts.TryGetValue(layout, out var fields))
            throw new MsyncException($"HPIR presentation has no field geometry for layout {layout}.");
        ++observation.Records;
        observation.Fields += fields.Length;
        List<HpirSessionMember> result = new(fields.Length);
        foreach (var field in fields)
        {
            TypeKind kind = field.Union is null ? field.Kind : UnionKind(address + field.Offset);
            if (observation.Recovery && kind is not (TypeKind.Null or TypeKind.Bool or TypeKind.Number or TypeKind.String
                or TypeKind.Type or TypeKind.Path or TypeKind.Connection or TypeKind.Scope or TypeKind.Function)) continue;
            result.Add(new(field.Name, ProjectField(address, field, observation)));
        }
        observation.Path.Remove(address);
        return new(TypeKind.Scope, 0, Scope: new(result)
        {
            Identity = observation.Recovery ? address : 0,
            Site = observation.Recovery ? HpirHeap.SessionSite(address) : 0
        });
    }

    private static TypeKind UnionKind(nint address) => (TypeKind)(1U << *(byte*)address);

    private HpirSessionValue ProjectField(nint instance, HpirSessionField field, ScopeObservation? observation)
    {
        if (field.Completion >= 0 && observation?.Frontier is { } frontier && *(nint*)(instance + field.Completion) is not 0 and var cell)
        {
            ++observation.Demands;
            return field.Kind == TypeKind.Number ? Scalar(field.Kind,
                BitConverter.DoubleToUInt64Bits(HpirFuture.Demand<double>(cell, frontier.Work, frontier.Site, frontier.Token, frontier.Text)))
                : Scalar(field.Kind, HpirFuture.Demand<byte>(cell, frontier.Work, frontier.Site, frontier.Token, frontier.Text));
        }
        nint address = instance + field.Offset;
        TypeKind kind = field.Kind;
        nint raw = address, pointers = address;
        if (field.Union is { } union)
        {
            kind = UnionKind(address);
            raw += union.RawOffset;
            pointers += union.PointerOffset;
        }
        return kind switch
        {
            TypeKind.Null => Scalar(kind, 0),
            TypeKind.Bool => Scalar(kind, *(byte*)raw),
            TypeKind.Number => Scalar(kind, *(ulong*)raw),
            TypeKind.String or TypeKind.Type or TypeKind.Connection => *(ulong*)pointers == 0 ? Scalar(TypeKind.Null, 0) : Scalar(kind, *(ulong*)pointers),
            TypeKind.Scope when *(nint*)pointers == 0 => Scalar(TypeKind.Null, 0),
            TypeKind.Scope => ScopeValue(*(nint*)pointers, observation!),
            TypeKind.Process when *(nint*)pointers == 0 => Scalar(TypeKind.Null, 0),
            TypeKind.Process => ObserveProcess(*(nint*)pointers),
            TypeKind.Function => ObserveCallable(pointers),
            TypeKind.Path => field.Union is null ? Aggregate(kind, address)
                : new(kind, 0, Path: UnionPath(raw, pointers).ToHost(contextual: true)),
            _ => throw new MsyncException($"HPIR presentation is not supported for field '{field.Name}' of type {kind}.")
        };
    }

    private static HpirPathValue UnionPath(nint raw, nint pointers) => new()
    {
        Endpoint = (ConnectionKind)(*(byte*)raw),
        Qualified = *(byte*)(raw + 1),
        Raw = *(nint*)pointers,
        Directory = *(nint*)(pointers + sizeof(nint)),
        Home = *(nint*)(pointers + 2 * sizeof(nint)),
        Spelling = *(nint*)(pointers + 3 * sizeof(nint)),
        EndpointAddress = *(nint*)(pointers + 4 * sizeof(nint))
    };
}
