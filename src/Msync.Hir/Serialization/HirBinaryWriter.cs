using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

internal sealed class HirBinaryWriter(BinaryWriter writer)
{
    internal void String(string value) => writer.Write(value);

    internal void Module(HirModule module)
    {
        writer.Write(module.Entry.Value);
        writer.Write(module.IsSession);
        Array(module.Sources, Source);
        Array(module.Types, Type);
        Array(module.Locations, Location);
        Array(module.Functions, Function);
        Array(module.Templates, Template);
        Array(module.Nodes, Node);
        Array(module.Edges, value => writer.Write(value.Value));
        Array(module.Literals, Literal);
        Array(module.ScopeLayouts, layout => Array(layout.Fields, Field));
        Array(module.Signatures, Signature);
        Array(module.Calls, Call);
        Array(module.Closures, Closure);
        Array(module.Scopes, Scope);
        Array(module.Fors, value => { writer.Write(value.Loop.Value); writer.Write(value.Item.Value); });
        Array(module.Matches, value => Array(value.Arms, arm => { Pattern(arm.Pattern); writer.Write(arm.Value.Value); }));
        Array(module.Barriers, value => { writer.Write(value.Release); writer.Write(value.Acquire); });
        Array(module.Ranges, value => Array(value.Deferred, writer.Write));
        Array(module.Adaptations, value => Array(value.Targets, Target));
        Array(module.Exports, value => { String(value.Name); writer.Write(value.Location.Value); });
        Array(module.TemplateClosures, value => { writer.Write(value.Template.Value); Array(value.Captures, Capture); });
        Array(module.Concepts, Concept);
    }

    private void Array<T>(ImmutableArray<T> values, Action<T> write)
    {
        writer.Write(values.IsDefault ? 0 : values.Length);
        if (values.IsDefault) return;
        foreach (T value in values) write(value);
    }

    private void Optional(string? value)
    {
        writer.Write(value is not null);
        if (value is not null) String(value);
    }

    private void Source(HirSource source) { String(source.Name); Optional(source.Text); }
    private void Range(HirSourceRange source)
    { writer.Write(source.Source.Value); writer.Write(source.Start); writer.Write(source.Length); }

    private void RuntimeType(TypeRef type)
    {
        int count = 0;
        for (TypeRef? item = type; item is not null; item = item.Element) count++;
        writer.Write(count);
        for (TypeRef? item = type; item is not null; item = item.Element) writer.Write((ushort)item.Kind);
    }

    private void Type(HirType type)
    {
        RuntimeType(type.RuntimeType);
        writer.Write(type.Shape); writer.Write(type.Signature); writer.Write(type.Element); writer.Write(type.TypeParameter); writer.Write(type.Concept);
        writer.Write(type.Nullable); Array(type.Alternatives, value => writer.Write(value.Value));
    }

    private void Location(HirLocation location)
    {
        String(location.Name); writer.Write(location.StorageType.Value); Range(location.Source);
        writer.Write(location.Owner.Value); writer.Write((byte)location.Kind);
        writer.Write(location.Mutable); writer.Write(location.AdmitsNull);
        writer.Write(location.TemplateOwner?.Value ?? -1);
    }

    private void Capture(HirCapture capture)
    {
        writer.Write(capture.Target.Value); writer.Write((byte)capture.Mode);
        writer.Write(capture.SourceLocation.Value); writer.Write(capture.SourceValue.Value);
    }

    private void Function(HirFunction function)
    {
        String(function.Name); Array(function.Parameters, value => writer.Write(value.Value)); Array(function.Captures, Capture);
        writer.Write(function.Result.Value); writer.Write(function.Body.Value);
        writer.Write(function.Template?.Value ?? -1); writer.Write(function.IsOpen);
    }

    private void Template(HirTemplate template)
    {
        String(template.Name); Array(template.TypeParameters, String);
        Array(template.Parameters, value => writer.Write(value.Value)); writer.Write(template.Body.Value); Range(template.Source);
        Array(template.Captures, Capture); writer.Write(template.ResultAnnotation?.Value ?? -1);
        Array(template.TypeParameterTypes, value => writer.Write(value.Value));
    }

    private void Node(HirNode node)
    {
        writer.Write((byte)node.Kind); writer.Write(node.Type.Value); Range(node.Source);
        writer.Write(node.Children.Start); writer.Write(node.Children.Count); writer.Write(node.Data);
        Optional(node.Text); writer.Write((ushort)node.Effects);
    }

    private void Literal(HirLiteral literal)
    {
        writer.Write((byte)literal.Kind); Optional(literal.Text);
        writer.Write(BitConverter.DoubleToInt64Bits(literal.Number)); writer.Write(literal.Boolean);
        writer.Write(literal.Type is not null);
        if (literal.Type is { } type) RuntimeType(type);
        writer.Write((byte)literal.ConnectionKind);
    }

    private void Field(HirField field)
    { String(field.Name); writer.Write(field.StorageType.Value); writer.Write(field.AdmitsNull); }

    private void Signature(HirSignature signature)
    {
        Array(signature.Parameters, value => writer.Write(value.Value));
        writer.Write(signature.Result.Value); writer.Write(signature.Variadic);
    }

    private void Call(HirCall call)
    {
        writer.Write((byte)call.Kind); writer.Write(call.Function.Value); Optional(call.NativeName);
        Array(call.Targets, Target);
        writer.Write(call.Scheduling);
    }

    private void Target(HirDispatchTarget target)
    {
        writer.Write(target.Identity.Value); writer.Write(target.Identity.IsTemplate); writer.Write(target.Target.Value);
    }

    private void Closure(HirClosure closure)
    { writer.Write(closure.Function.Value); Array(closure.Captures, Capture); }

    private void Scope(HirScopeConstruction scope)
    {
        writer.Write(scope.Layout); Array(scope.Fields, value => writer.Write(value.Value));
        writer.Write(scope.Singleton); writer.Write(scope.Self?.Value ?? -1);
    }

    private void Pattern(HirPattern pattern)
    {
        writer.Write((byte)pattern.Kind); writer.Write(pattern.Data); Optional(pattern.Text);
        writer.Write(pattern.Binding?.Value ?? -1);
    }

    private void Concept(HirConcept concept)
    {
        String(concept.Name); Range(concept.Source); Array(concept.Bases, writer.Write);
        Array(concept.TypeParameters, value => writer.Write(value.Value));
        Array(concept.Members, member =>
        {
            String(member.Name); writer.Write(member.Type.Value); Range(member.Source);
            writer.Write(member.Signature); writer.Write(member.Nullable);
        });
    }
}
