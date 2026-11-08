using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed record Info(TypeRef Type, Template? Function = null, ScopeInfo? Scope = null, TypeRef? TypeValue = null, string? NativeName = null, IReadOnlyList<Info>? Alternatives = null, Info? Element = null, bool? KnownBool = null, ConnectionValue? KnownConnection = null, bool EditorUnknown = false, PathValue? KnownPath = null, string? KnownText = null);
internal sealed record Binding(int Slot, Info Info, Template? Self = null, EditorLocation? Origin = null);
internal sealed record ScopeInfo(IReadOnlyDictionary<string, Info> Members, bool Named = false,
    IReadOnlyDictionary<string, IReadOnlyList<EditorLocation>>? Origins = null);
internal sealed class Template(int id, FunctionExpr syntax, IReadOnlyList<KeyValuePair<string, Binding>> captures, string source, string sourceName)
{
    public int Id { get; } = id;
    public FunctionExpr Syntax { get; } = syntax;
    public IReadOnlyList<KeyValuePair<string, Binding>> Captures { get; } = captures;
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public Dictionary<string, Specialization> Specializations { get; } = [];
}
internal sealed class Specialization(int id, Info result, IReadOnlyList<TypeRef> parameterTypes)
{
    public int Id { get; } = id;
    public Info Result { get; set; } = result;
    public IReadOnlyList<TypeRef> ParameterTypes { get; } = parameterTypes;
}
internal sealed class Frame(string source, string sourceName, ImmutableDictionary<string, Binding>? environment = null)
{
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public ImmutableDictionary<string, Binding> Environment { get; set; } = environment ?? ImmutableDictionary<string, Binding>.Empty;
    public SourceSpan ContextOwner { get; set; } = new(0, source.Length);
    public bool ContextIncludesEnd { get; set; } = true;
    public bool EditorRecovery { get; set; }
    public bool EditorProbe { get; set; }
    public bool EditorReportErrors { get; set; }
    public EditorCallTrace? EditorCallTrace { get; set; }
    public List<Instruction> Code { get; } = [];
    public int Locals { get; set; }
    public List<Info> Returns { get; } = [];
    public TypeRef? ExpectedReturn { get; set; }
    public SourceSpan? ExpectedReturnSpan { get; set; }
    public Dictionary<int, int[]> SelfCaptureSlots { get; } = [];
    public List<List<int>> Breaks { get; } = [];
    public List<List<Info>> BreakValues { get; } = [];
    public int Allocate() => Locals++;
    public void Bind(string name, Binding binding) => Environment = Environment.SetItem(name, binding);
    public int Emit(OpCode op, int a = 0, int b = 0, string? text = null, SourceSpan? span = null)
    {
        Code.Add(new(op, a, b, text, span is null ? null : new Msync.Core.SourceSpan(SourceName, span.Value.Start, span.Value.Length)));
        return Code.Count - 1;
    }
    public void Patch(int at, int target) => Code[at] = Code[at] with { A = target };
}

