using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed record Info(TypeRef Type, Template? Function = null, ScopeInfo? Scope = null, TypeRef? TypeValue = null, string? NativeName = null, IReadOnlyList<Info>? Alternatives = null, Info? Element = null, bool? KnownBool = null, ConnectionValue? KnownConnection = null, bool EditorUnknown = false, PathValue? KnownPath = null, string? KnownText = null, StorageConstraint? Storage = null, bool EagerRange = false);
internal sealed record Binding(int Slot, Info Info, Template? Self = null, EditorLocation? Origin = null,
    bool AdmitsNull = false, TypeRef? Constraint = null)
{
    public object Identity { get; init; } = new();
    public StorageConstraint Storage { get; init; } = new(Constraint, Info.Type.WithoutNull == TypeRef.Function ? Info with { Storage = null } : null, Info.Storage);
}
internal sealed class StorageConstraint(TypeRef? type, Info? callables = null, StorageConstraint? source = null)
{
    public TypeRef? Type { get; set; } = type;
    public Info? Callables { get; set; } = callables;
    public ImmutableDictionary<string, ConceptContract> ConceptContracts { get; set; } = ImmutableDictionary<string, ConceptContract>.Empty;
    public ImmutableDictionary<string, ConceptMemberContract> ConceptMemberContracts { get; set; } = ImmutableDictionary<string, ConceptMemberContract>.Empty;
    public int Revision { get; set; }
    public ImmutableHashSet<StorageConstraint> Sources { get; set; } = source is null ? [] : [source];
    public ImmutableDictionary<string, CallableContract> Contracts { get; set; } = ImmutableDictionary<string, CallableContract>.Empty;
    public ImmutableDictionary<string, CastTypeContract> CastContracts { get; set; } = ImmutableDictionary<string, CastTypeContract>.Empty;
    public ImmutableDictionary<string, DeferredTypeWrite> DeferredTypeWrites { get; set; } = ImmutableDictionary<string, DeferredTypeWrite>.Empty;
}
internal sealed record CallableContract(Info Result, string Source, string SourceName, SourceSpan Span);
internal sealed record CastTypeContract(TypeRef Input, TypeRef Target, string Source, string SourceName, SourceSpan Span);
internal sealed record DeferredTypeWrite(TypeRef? Target, string Source, string SourceName, SourceSpan Span);
internal sealed record ScopeInfo(IReadOnlyDictionary<string, Info> Members, bool Named = false,
    IReadOnlyDictionary<string, IReadOnlyList<EditorLocation>>? Origins = null,
    IReadOnlySet<string>? NullableMembers = null, IReadOnlyDictionary<string, TypeRef?>? StorageTypes = null)
{
    public object Identity { get; init; } = new();
    public IReadOnlyDictionary<string, StorageConstraint> Storage { get; init; } =
        Members.ToDictionary(pair => pair.Key, pair => new StorageConstraint(StorageTypes?.GetValueOrDefault(pair.Key)
            ?? (pair.Value.Type.WithoutNull.Kind == TypeKind.Never ? null : pair.Value.Type),
            pair.Value.Type.WithoutNull == TypeRef.Function ? pair.Value with { Storage = null } : null, pair.Value.Storage));
    public IReadOnlyDictionary<string, Binding> FieldBindings { get; init; } = new Dictionary<string, Binding>();
    public int AllocationSite { get; init; }
}
internal sealed class Template(int id, FunctionExpr syntax, IReadOnlyList<KeyValuePair<string, Binding>> captures, string source, string sourceName)
{
    public int Id { get; } = id;
    public FunctionExpr Syntax { get; } = syntax;
    public IReadOnlyList<KeyValuePair<string, Binding>> Captures { get; } = captures;
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public Dictionary<string, Specialization> Specializations { get; } = [];
    public ImmutableDictionary<string, ConceptDefinition> Concepts { get; init; } = ImmutableDictionary<string, ConceptDefinition>.Empty;
    public bool Instance { get; init; }
}
internal sealed class Specialization(int id, Info result, IReadOnlyList<TypeRef> parameterTypes)
{
    public int Id { get; } = id;
    public Info Result { get; set; } = result;
    public IReadOnlyList<TypeRef> ParameterTypes { get; } = parameterTypes;
    public IReadOnlyDictionary<object, Info> Effects { get; set; } = new Dictionary<object, Info>();
}
internal sealed class LoopContext(string label, bool isFor, int acquireDepth, int iteratorDepth)
{
    public string Label { get; } = label;
    public bool IsFor { get; } = isFor;
    public int AcquireDepth { get; } = acquireDepth;
    public int IteratorDepth { get; } = iteratorDepth;
    public List<Info> Values { get; } = [];
    public List<ImmutableDictionary<object, Info>> ExitFacts { get; } = [];
}
internal sealed class Frame(string source, string sourceName, ImmutableDictionary<string, Binding>? environment = null)
{
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public ImmutableDictionary<string, Binding> Environment { get; set; } = environment ?? ImmutableDictionary<string, Binding>.Empty;
    public ImmutableDictionary<string, Binding>? GlobalBindings { get; set; }
    public ImmutableDictionary<object, Info> Facts { get; set; } = ImmutableDictionary<object, Info>.Empty;
    public Dictionary<(int Start, string Name), Binding> Declarations { get; init; } = [];
    public Dictionary<ScopeExpr, object> ScopeSites { get; init; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<(Template Template, int Start), Info> Allocations { get; init; } = [];
    public Dictionary<object, HashSet<(object Scope, string Member)>> FieldOwners { get; init; } = [];
    public SourceSpan ContextOwner { get; set; } = new(0, source.Length);
    public bool ContextIncludesEnd { get; set; } = true;
    public bool SingleExecution { get; init; }
    public bool EditorRecovery { get; set; }
    public bool EditorProbe { get; set; }
    public bool EditorReportErrors { get; set; }
    public bool DeferredCaptures { get; set; }
    public EditorCallTrace? EditorCallTrace { get; set; }
    public List<Instruction> Code { get; } = [];
    public int Locals { get; set; }
    public List<Info> Returns { get; } = [];
    public ImmutableDictionary<string, ConceptDefinition> Concepts { get; set; } = ImmutableDictionary<string, ConceptDefinition>.Empty;
    public TypeSyntax? ExpectedConceptReturn { get; set; }
    public TypeRef? ExpectedReturn { get; set; }
    public SourceSpan? ExpectedReturnSpan { get; set; }
    public Dictionary<int, int[]> SelfCaptureSlots { get; } = [];
    public List<string> AcquireRegions { get; } = [];
    public List<LoopContext> Loops { get; } = [];
    public List<int> Iterators { get; } = [];
    public int Allocate() => Locals++;
    public void Bind(string name, Binding binding)
    {
        Environment = Environment.SetItem(name, binding);
        Facts = Facts.SetItem(binding.Identity, binding.Info);
    }
    public int Emit(OpCode op, int a = 0, int b = 0, string? text = null, SourceSpan? span = null)
    {
        Code.Add(new(op, a, b, text, span is null ? null : new Msync.Core.SourceSpan(SourceName, span.Value.Start, span.Value.Length)));
        return Code.Count - 1;
    }
    public void Patch(int at, int target) => Code[at] = Code[at] with { A = target };
}

