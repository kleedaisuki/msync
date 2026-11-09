using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private const string StandardCachePrefix = "$std::";

    private static readonly Lazy<string[]> standardNames = new(() => libraries.Value.Keys.Where(name => !name.StartsWith('_'))
        .Concat(libraries.Value.TryGetValue("_core", out var prelude) ? prelude.Document.Statements.OfType<NativeDeclaration>().Select(item => item.Name) : [])
        .Order(StringComparer.Ordinal).ToArray());
    private static IEnumerable<string> StandardNames() => standardNames.Value;

    private static ImmutableDictionary<string, Binding> StandardEnvironment(Frame frame) => frame.Environment
        .Where(pair => pair.Key == "$std" || pair.Key.StartsWith(StandardCachePrefix, StringComparison.Ordinal))
        .ToImmutableDictionary(StringComparer.Ordinal);

    private Binding StandardBinding(Frame frame, string name, SourceSpan span)
    {
        string key = StandardCachePrefix + name;
        if (frame.Environment.TryGetValue(key, out var saved)) return saved;
        int slot = frame.Allocate();
        Binding binding = new(slot, new(TypeRef.Scope, Scope: new(new Dictionary<string, Info>())), Constraint: TypeRef.Scope);
        frame.Bind(key, binding);
        frame.Emit(OpCode.Null);
        frame.Emit(OpCode.InitializeLocal, slot);
        Info info;
        EditorLocation? origin;
        if (libraries.Value.TryGetValue("_core", out var prelude)
            && prelude.Document.Statements.OfType<NativeDeclaration>().FirstOrDefault(item => item.Name == name) is { } export)
        {
            Frame context = new(prelude.Source, "msync-stdlib:///0.8.0/_core.msync");
            info = Native(context, export, null);
            frame.Code.AddRange(context.Code);
            origin = new(context.SourceName, context.Source, export.NameSpan ?? export.Span);
            binding = binding with { Constraint = TypeRef.Function, Storage = new(TypeRef.Function) };
        }
        else
        {
            info = StandardModule(frame, name, span);
            origin = libraries.Value.TryGetValue(name, out var library)
                ? new($"msync-stdlib:///0.8.0/{name}.msync", library.Source, new(0, 0)) : null;
        }
        frame.Emit(OpCode.StoreLocal, slot);
        binding = binding with { Origin = origin };
        Update(frame, binding, info);
        frame.Bind(key, binding with { Info = info });
        return frame.Environment[key];
    }

    private Info StandardRootInfo(Frame frame)
    {
        Dictionary<string, Info> members = [];
        Dictionary<string, IReadOnlyList<EditorLocation>>? origins = editorContexts is null ? null : new(StringComparer.Ordinal);
        foreach (string name in StandardNames())
        {
            if (frame.Environment.TryGetValue(StandardCachePrefix + name, out var binding))
            {
                members[name] = Current(frame, binding);
                if (origins is not null && binding.Origin is { } location) origins[name] = [location];
            }
            else if (libraries.Value.ContainsKey(name))
            {
                members[name] = new(TypeRef.Scope);
                if (origins is not null) origins[name] = [new($"msync-stdlib:///0.8.0/{name}.msync", libraries.Value[name].Source, new(0, 0))];
            }
            else members[name] = new(TypeRef.Function, NativeName: name);
        }
        return new(TypeRef.Scope, Scope: new(members, Origins: origins));
    }

    private bool TryStandardSelection(Frame frame, BinaryExpr binary, out Info info)
    {
        info = UnknownInfo;
        if (binary.Left is not UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } }
            || frame.Environment.ContainsKey("std") || frame.Environment.ContainsKey("$std")) return false;
        string? name = binary.Right switch { NameExpr value => value.Name, PathExpr value => value.Path, StringExpr value => value.Value, _ => null };
        if (name is null)
        {
            if (frame.EditorRecovery && binary.Right is MissingExpr)
            {
                int first = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
                CaptureReceiver(frame, new(first, binary.Right.Span.End - first), StandardRootInfo(frame));
                return true;
            }
            return false;
        }
        if (!StandardNames().Contains(name, StringComparer.Ordinal)) return false;
        var binding = StandardBinding(frame, name, binary.Span);
        int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
        CaptureReceiver(frame, new(start, binary.Right.Span.End - start), StandardRootInfo(frame));
        frame.Emit(OpCode.LoadLocal, binding.Slot, span: binary.Span);
        info = Current(frame, binding) with { Storage = binding.Storage };
        Observe(frame, binary.Right.Span, info, name);
        if (binding.Origin is { } origin) CaptureDefinition(frame, binary.Right.Span, [origin]);
        return true;
    }

    private void CaptureStandardDependencies(Frame frame, Node body, ISet<string> usedNames)
    {
        if (!HasStandardReference(body)) return;
        if (frame.Environment.ContainsKey("std")) { usedNames.Add("std"); return; }
        HashSet<string> selected = new(StringComparer.Ordinal);
        bool root = false;
        void Collect(Node node)
        {
            if (node is BinaryExpr { Operator: "::", Left: UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } } } member)
            {
                string? name = member.Right switch { NameExpr value => value.Name, PathExpr value => value.Path, StringExpr value => value.Value, _ => null };
                if (name is null || !StandardNames().Contains(name, StringComparer.Ordinal)) root = true;
                else selected.Add(name);
                return;
            }
            if (node is UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } }) { root = true; return; }
            foreach (Node child in Children(node)) Collect(child);
        }
        Collect(body);
        if (root || frame.Environment.ContainsKey("$std"))
        {
            Standard(frame, body.Span); frame.Emit(OpCode.Pop);
            usedNames.Add("$std");
            return;
        }
        foreach (string name in selected.Order(StringComparer.Ordinal))
        {
            StandardBinding(frame, name, body.Span);
            usedNames.Add(StandardCachePrefix + name);
        }
    }
}
