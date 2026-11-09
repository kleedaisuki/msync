using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed class Frame(string source, string sourceName, ImmutableDictionary<string, Binding>? environment = null)
{
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public ImmutableDictionary<string, Binding> Environment { get; set; } = environment ?? ImmutableDictionary<string, Binding>.Empty;
    public ImmutableDictionary<string, Binding>? GlobalBindings { get; set; }
    public ImmutableDictionary<object, Info> Facts { get; set; } = ImmutableDictionary<object, Info>.Empty;
    public Dictionary<(int Start, string Name), Binding> Declarations { get; init; } = [];
    public Dictionary<ScopeExpr, object> ScopeSites { get; init; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<(Template Template, int Start, int Specialization), Info> Allocations { get; init; } = [];
    public Dictionary<object, HashSet<(object Scope, string Member)>> FieldOwners { get; init; } = [];
    public SourceSpan ContextOwner { get; set; } = new(0, source.Length);
    public bool ContextIncludesEnd { get; set; } = true;
    public bool SingleExecution { get; init; }
    public bool EditorRecovery { get; set; }
    public bool EditorProbe { get; set; }
    public bool EditorReportErrors { get; set; }
    public bool DeferredCaptures { get; set; }
    public bool DeferredReads { get; set; }
    public IReadOnlySet<int> UnwrittenLocations { get; init; } = ImmutableHashSet<int>.Empty;
    public EditorCallTrace? EditorCallTrace { get; set; }
    public List<Instruction> Code { get; } = [];
    public int Locals { get; set; }
    public List<Info> Returns { get; } = [];
    public ImmutableDictionary<string, ConceptDefinition> Concepts { get; set; } = ImmutableDictionary<string, ConceptDefinition>.Empty;
    public TypeSyntax? ExpectedConceptReturn { get; set; }
    public TypeEnvironment? ExpectedTypeEnvironment { get; set; }
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

    internal static Frame Fork(Frame frame, string? source = null, string? sourceName = null)
    {
        Frame clone = new(source ?? frame.Source, sourceName ?? frame.SourceName, frame.Environment)
        {
            Locals = frame.Locals,
            Facts = frame.Facts,
            GlobalBindings = frame.GlobalBindings,
            Declarations = frame.Declarations,
            ScopeSites = frame.ScopeSites,
            Allocations = frame.Allocations,
            FieldOwners = frame.FieldOwners,
            ExpectedReturn = frame.ExpectedReturn,
            Concepts = frame.Concepts,
            ExpectedConceptReturn = frame.ExpectedConceptReturn,
            ExpectedTypeEnvironment = frame.ExpectedTypeEnvironment,
            ContextOwner = frame.ContextOwner,
            ContextIncludesEnd = frame.ContextIncludesEnd,
            EditorRecovery = frame.EditorRecovery,
            EditorProbe = frame.EditorProbe,
            EditorReportErrors = frame.EditorReportErrors,
            DeferredCaptures = frame.DeferredCaptures,
            DeferredReads = frame.DeferredReads,
            UnwrittenLocations = frame.UnwrittenLocations,
            EditorCallTrace = frame.EditorCallTrace
        };
        clone.AcquireRegions.AddRange(frame.AcquireRegions);
        clone.Iterators.AddRange(frame.Iterators);
        foreach (var pair in frame.SelfCaptureSlots) clone.SelfCaptureSlots[pair.Key] = pair.Value;
        return clone;
    }
}
