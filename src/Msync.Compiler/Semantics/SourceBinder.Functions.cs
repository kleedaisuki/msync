using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirNodeId FunctionDeclaration(FunctionDeclaration declaration)
    {
        var location = Declare(declaration.Name, declaration.Function.NameSpan ?? declaration.Span,
            DeclarationKind.Function, hir.Type(TypeRef.Function));
        var value = Function(declaration.Function, location);
        return Node(HirKind.Declare, hir.Type(TypeRef.Null), declaration.Span, [value], location.Value);
    }

    private HirNodeId Function(FunctionExpr function, HirLocationId? declaration = null)
    {
        var outer = scope;
        var outerAnnotations = annotationScope;
        var savedLoops = loops.Count == 0 ? null : loops.ToArray();
        loops.Clear();
        HirTemplateId id = new(hir.Templates.Count);
        string name = function.Name ?? "<lambda>";
        var definition = Range(function.Span);
        hir.Templates.Add(new(name, [], [], new(-1), definition));
        program.Templates.Add(new(id, name, [], [], null, new(-1), definition));
        if (declaration is { } bound) program.FunctionLocations.Add(bound, id);
        scope = new(outer, definition, id, outer.Values);
        annotationScope = outer;
        List<HirLocationId> parameters = [];
        List<HirTypeId> typeParameters = [];
        try
        {
            foreach (var parameter in function.Parameters)
            {
                HirTypeId? annotation = parameter.Type is null ? null : Type(parameter.Type);
                HirTypeId type = annotation ?? OpenType(parameter.Name);
                if (annotation is null) typeParameters.Add(type);
                var location = Declare(parameter.Name, new(parameter.Span.Start, parameter.Name.Length),
                    DeclarationKind.Parameter, type, parameter.Nullable, annotation);
                parameters.Add(location);
            }
            HirTypeId? result = function.ReturnType is null ? null : Type(function.ReturnType);
            annotationScope = null;
            var body = Block(function.Body);
            var captures = scope.Captures.OrderBy(pair => pair.Key.Value)
                .Select(pair => new HirCapture(pair.Value, HirCaptureMode.Location, pair.Key, new(-1)))
                .ToImmutableArray();
            hir.Templates[id.Value] = new(name,
                typeParameters.Select(type => program.TypeParameterNames[hir.Types[type.Value].TypeParameter]).ToImmutableArray(),
                parameters.ToImmutableArray(), body, definition, captures, result, typeParameters.ToImmutableArray());
            program.Templates[id.Value] = new(id, name, parameters.ToImmutableArray(),
                captures.Select(capture => capture.Target).ToImmutableArray(), result, body, definition);
            int closure = hir.TemplateClosures.Count;
            hir.TemplateClosures.Add(new(id, captures));
            return Node(HirKind.TemplateClosure, hir.Type(TypeRef.Function), function.Span, data: closure);
        }
        finally
        {
            scope = outer;
            annotationScope = outerAnnotations;
            if (savedLoops is not null)
                for (int index = savedLoops.Length - 1; index >= 0; index--) loops.Push(savedLoops[index]);
        }
    }

    private HirNodeId Native(NativeDeclaration declaration)
    {
        string identity = nativeModule is null ? declaration.Name : nativeModule + "." + declaration.Name;
        if (!NativeSignatures.TryGet(identity, out var signature))
        {
            var candidates = NativeSignatures.All.Where(item => item.Name.EndsWith("." + identity, StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 1) throw Error($"Native function '{identity}' has no unique registered implementation.", declaration.Span);
            signature = candidates[0];
            identity = signature.Name;
        }
        var parameterTypes = declaration.Parameters.Select(parameter => parameter.Type is null
            ? OpenType(parameter.Name) : Type(parameter.Type)).ToImmutableArray();
        var result = Type(declaration.ReturnType);
        if ((!signature.Variadic && declaration.Parameters.Count != signature.Parameters.Count)
            || signature.Variadic && (declaration.Parameters.Count < signature.Parameters.Count
                || declaration.Parameters.Count > signature.Parameters.Count + 1))
            throw Error($"Native declaration '{identity}' has the wrong parameter count.", declaration.Span);
        for (int index = 0; index < Math.Min(declaration.Parameters.Count, signature.Parameters.Count); index++)
        {
            var parameter = declaration.Parameters[index];
            var required = signature.Parameters[index];
            if (parameter.Type is null)
            {
                if (signature.ResultRule != NativeResultRule.Fixed || required.WithoutNull.Kind == (TypeKind.Any & ~TypeKind.Null))
                    continue;
                throw Error("This native parameter requires an explicit type.", parameter.Span);
            }
            var actual = hir.Types[parameterTypes[index].Value].RuntimeType;
            if (parameter.Nullable) actual = actual.WithNull;
            if (!required.Accepts(actual))
                throw Error($"Native '{identity}' parameter '{parameter.Name}' must accept {required}, not {actual}.", parameter.Span);
        }
        if (signature.ResultRule == NativeResultRule.Fixed && !hir.Types[result.Value].RuntimeType.Accepts(signature.Result))
            throw Error($"Native '{identity}' returns {signature.Result}, not {hir.Types[result.Value].RuntimeType}.", declaration.ReturnType.Span);
        int signatureId = hir.Signatures.Count;
        hir.Signatures.Add(new(parameterTypes, result, signature.Variadic));
        var type = hir.Type(new HirType(TypeRef.Function, Signature: signatureId));
        var location = Declare(declaration.Name, declaration.NameSpan ?? declaration.Span,
            DeclarationKind.Native, type);
        program.NativeLocations.Add(location, identity);
        if (observations) program.NativeSignatures.TryAdd(identity, new(identity, Range(declaration.Span),
            declaration.Parameters.Select(parameter => parameter.Name).ToImmutableArray(), parameterTypes, result,
            declaration.Parameters.Select(parameter => parameter.Nullable).ToImmutableArray()));
        var value = Node(HirKind.NativeFunction, type, declaration.Span, text: identity);
        return Node(HirKind.Declare, hir.Type(TypeRef.Null), declaration.Span, [value], location.Value);
    }

    private HirNodeId Globals(GlobalStatement statement)
    {
        if (scope.Template is null || scope.Globals is null)
            throw Error("global imports are only meaningful inside an fn.", statement.Span, "E0214");
        if (observations) program.GlobalImports.Add(new(Range(statement.Span), scope.Globals));
        foreach (var name in statement.Names)
        {
            if (!scope.Globals.TryGetValue(name.Name, out var original))
                throw Error($"global '{name.Name}' was not declared before this function definition.", name.Span, "E0214");
            if (scope.Values.TryGetValue(name.Name, out var existing)
                && Original(existing) != Original(original))
                throw Error($"global '{name.Name}' conflicts with a parameter or local binding.", name.Span, "E0214");
            var location = Capture(original, name.Name);
            scope.Values = scope.Values.SetItem(name.Name, location);
            Observe(name.Span, location);
        }
        return Null(statement.Span);
    }

    private HirLocationId Original(HirLocationId location)
    {
        while (program.Declaration(location).CapturedFrom is { } original) location = original;
        return location;
    }

    private HirNodeId ScopeDeclaration(ScopeDeclaration declaration)
    {
        var location = Declare(declaration.Name, declaration.NameSpan ?? declaration.Span,
            DeclarationKind.Scope, hir.Type(TypeRef.Scope));
        var value = Scope(declaration.Scope, location, true);
        return Node(HirKind.Declare, hir.Type(TypeRef.Null), declaration.Span, [value], location.Value);
    }

    private HirNodeId Scope(ScopeExpr expression, HirLocationId? self = null, bool named = false)
    {
        var outer = scope;
        scope = new(outer, Range(expression.Span), outer.Template, includesEnd: expression.EditorIncomplete);
        try
        {
            if (self is null && expression.Name is { } name)
                self = Declare(name, expression.NameSpan ?? expression.Span, DeclarationKind.Scope, hir.Type(TypeRef.Scope));
            List<HirLocationId> fields = [];
            List<HirField> shape = [];
            List<HirNodeId> initializers = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            int startContext = Context(expression.Span.Start);
            foreach (var member in expression.Members)
            {
                if (!names.Add(member.Name)) throw Error($"Scope field '{member.Name}' is declared more than once.",
                    member.NameSpan ?? member.Span, "E0212");
                var value = Expression(member.Value);
                var type = program.Node(value).Type;
                if (member.Nullable && hir.Types[type.Value].RuntimeType.WithoutNull.Kind == TypeKind.Never)
                    type = OpenType("nullable field " + member.Name);
                var location = Declare(member.Name, member.NameSpan ?? member.Span, DeclarationKind.Field,
                    type, member.Nullable);
                fields.Add(location);
                shape.Add(new(member.Name, type, member.Nullable));
                var declared = Node(HirKind.Declare, hir.Type(TypeRef.Null), member.Span, [value], location.Value);
                initializers.Add(declared);
                Context(member.Span.End, declared, after: true);
            }
            if (expression.Value is not null) initializers.Add(Expression(expression.Value));
            int layout = hir.ScopeLayouts.Count;
            hir.ScopeLayouts.Add(new(shape.ToImmutableArray()));
            int construction = hir.Scopes.Count;
            hir.AddScope(new(layout, fields.ToImmutableArray(), named || expression.Name is not null ? program.NextSingleton++ : -1, self));
            var result = Node(HirKind.Scope, hir.Type(new HirType(TypeRef.Scope, Shape: layout)), expression.Span,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(initializers), construction);
            if (startContext >= 0) program.Visibility[startContext] = program.Visibility[startContext] with { Anchor = result };
            return result;
        }
        finally { scope = outer; }
    }
}
