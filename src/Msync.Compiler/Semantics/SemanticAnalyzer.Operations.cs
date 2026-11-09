using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Operation(HirNodeId id, HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        switch (node.Kind)
        {
            case HirKind.Error:
                return new(hir.Type(TypeRef.Any), Add(node, hir.Type(TypeRef.Any), [], context));
            case HirKind.TypeValue:
                return new(hir.Type(TypeRef.Type), Add(node, hir.Type(TypeRef.Type), [], context),
                    new(HirLiteralKind.Type, Type: hir.Types[node.Data].RuntimeType), DenotedType: new(node.Data));
            case HirKind.TypeOf:
            {
                var value = Analyze(children[0], context.QueryFork());
                var result = node with { Kind = HirKind.TypeValue, Data = value.Type.Value };
                return new(hir.Type(TypeRef.Type), Add(result, hir.Type(TypeRef.Type), [], context),
                    new(HirLiteralKind.Type, Type: Runtime(value)), DenotedType: value.Type);
            }
            case HirKind.Builtin:
                return Builtin(node, context);
            case HirKind.Binary:
                return Binary(node, children, context);
            case HirKind.MemberRead:
                return MemberRead(id, node, Analyze(children[0], context), context);
            case HirKind.MemberAssign:
            {
                var receiver = Analyze(children[0], context);
                var value = Analyze(children[1], context);
                return MemberAssign(node, receiver, value, context, program.Node(children[1]).Source);
            }
            case HirKind.IsType:
            {
                var value = Analyze(children[0], context);
                var type = Analyze(children[1], context);
                Require(type, hir.Type(TypeRef.Type), program.Node(children[1]).Source, context: context);
                bool? known = null;
                if (type.DenotedType is { } target
                    && (value.DenotedType is not null || (Runtime(value).Kind & TypeKind.Type) == 0))
                {
                    TypeRef actual = value.DenotedType is { } denoted ? hir.Types[denoted.Value].RuntimeType : Runtime(value);
                    TypeRef required = hir.Types[target.Value].RuntimeType;
                    known = required.Accepts(actual) ? true : (required.Kind & actual.Kind) == 0 ? false : null;
                }
                return new(hir.Type(TypeRef.Bool), Add(node with { Data = type.DenotedType?.Value ?? -1 }, hir.Type(TypeRef.Bool), [value.Node, type.Node], context),
                    known is { } result ? new(HirLiteralKind.Bool, Boolean: result) : null);
            }
            case HirKind.Convert:
            case HirKind.Coerce:
                return Cast(node, Analyze(children[0], context), context);
        }
        var operand = children.Length == 0 ? null : Analyze(children[0], context);
        if (node.Kind == HirKind.IsNull)
        {
            bool? known = Runtime(operand!) == TypeRef.Null ? true : !Runtime(operand!).Nullable ? false : null;
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [operand!.Node], context),
                known is { } result ? new(HirLiteralKind.Bool, Boolean: result) : null);
        }
        if (node.Kind == HirKind.Unary)
        {
            var operation = (HirUnaryOperator)node.Data;
            Require(operand!, hir.Type(operation == HirUnaryOperator.Not ? TypeRef.Bool : TypeRef.Number), program.Node(children[0]).Source);
            HirLiteral? literal = operation == HirUnaryOperator.Not && operand!.KnownBool is { } known
                ? new(HirLiteralKind.Bool, Boolean: !known)
                : operation == HirUnaryOperator.Negate && operand!.Literal is { Kind: HirLiteralKind.Number } number
                    ? new(HirLiteralKind.Number, Number: -number.Number) : null;
            return new(operand!.Type, Add(node, operand.Type, [operand.Node], context), literal);
        }
        if (node.Kind == HirKind.RegexMatch)
        {
            Require(operand!, hir.Type(new TypeRef(TypeKind.Path | TypeKind.String)), program.Node(children[0]).Source);
            ValidatePattern(node.Text!, node.Source);
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [operand!.Node], context));
        }
        if (node.Kind == HirKind.SelectPath)
        {
            Require(operand!, hir.Type(new TypeRef(TypeKind.Path | TypeKind.String)), program.Node(children[0]).Source);
            ValidatePattern(node.Text!, node.Source);
            var element = ValueForType(hir.Type(TypeRef.Path));
            var type = RangeType(element.Type);
            return new(type, Add(node, type, [operand!.Node], context), Element: element);
        }
        if (node.Kind is HirKind.PathJoin or HirKind.Qualify or HirKind.Transfer)
        {
            var other = Analyze(children[1], context);
            return PairOperation(node, operand!, other, context, program.Node(children[0]).Source, program.Node(children[1]).Source);
        }
        throw Error(node.Source, $"Unknown semantic operation '{node.Kind}'.");
    }

    private SemanticValue Builtin(HirNode node, SemanticContext context)
    {
        if (node.Text == "std") return Standard(node, context);
        var kind = node.Text switch
        {
            "wsl" => ConnectionKind.Wsl,
            "env" => ConnectionKind.Environment,
            "path" => ConnectionKind.SearchPath,
            _ => ConnectionKind.Local
        };
        return new(hir.Type(TypeRef.Connection), Add(node, hir.Type(TypeRef.Connection), [], context),
            new(HirLiteralKind.Connection, Text: node.Text is "pwd" or "file" ? node.Text : "", ConnectionKind: kind));
    }

    private SemanticValue Binary(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        var left = Analyze(children[0], context);
        var leftSource = program.Node(children[0]).Source;
        var rightSource = program.Node(children[1]).Source;
        var operation = (HirBinaryOperator)node.Data;
        if (operation is HirBinaryOperator.And or HirBinaryOperator.Or)
        {
            Require(left, hir.Type(TypeRef.Bool), leftSource);
            var branch = context.Fork();
            bool rightEdge = operation == HirBinaryOperator.And;
            RefineCondition(children[0], rightEdge, branch);
            var right = Analyze(children[1], branch);
            Require(right, hir.Type(TypeRef.Bool), rightSource);
            if (left.KnownBool is { } selected)
            {
                if (selected == rightEdge) { MergeContexts(context, branch); AppendExits(context, branch); }
            }
            else { MergeContexts(context, context.Fork(), branch); AppendExits(context, branch); }
            bool? known = left.KnownBool is { } a && right.KnownBool is { } b
                ? operation == HirBinaryOperator.And ? a && b : a || b : null;
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [left.Node, right.Node], context),
                known is { } boolean ? new(HirLiteralKind.Bool, Boolean: boolean) : null);
        }
        var second = Analyze(children[1], context);
        if (operation == HirBinaryOperator.Divide && (Runtime(left) != TypeRef.Number || Runtime(second) != TypeRef.Number))
            return PairOperation(node with { Kind = HirKind.PathJoin }, left, second, context, leftSource, rightSource);
        if (operation >= HirBinaryOperator.Equal)
        {
            if (Runtime(left).Nullable || Runtime(second).Nullable)
                throw Error(Runtime(left).Nullable ? leftSource : rightSource,
                    "Null has no boolean comparison; use 'is null' or 'is not null'.", "E0201");
            TypeRef comparable = new(TypeKind.Bool | TypeKind.Number | TypeKind.String | TypeKind.Path | TypeKind.Type | TypeKind.Connection);
            Require(left, hir.Type(comparable), leftSource);
            Require(second, hir.Type(comparable), rightSource);
            TypeRef text = new(TypeKind.Path | TypeKind.String);
            if (Runtime(left).Kind != TypeKind.Never && Runtime(second).Kind != TypeKind.Never
                && Runtime(left) != Runtime(second) && !(text.Accepts(Runtime(left)) && text.Accepts(Runtime(second))))
                throw Error(node.Source, $"Cannot compare {Runtime(left)} and {Runtime(second)}.", "E0203");
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [left.Node, second.Node], context));
        }
        Require(left, hir.Type(TypeRef.Number), leftSource);
        Require(second, hir.Type(TypeRef.Number), rightSource);
        return new(hir.Type(TypeRef.Number), Add(node, hir.Type(TypeRef.Number), [left.Node, second.Node], context));
    }

    private SemanticValue PairOperation(HirNode node, SemanticValue left, SemanticValue right, SemanticContext context,
        HirSourceRange leftSource, HirSourceRange rightSource)
    {
        var path = hir.Type(new TypeRef(TypeKind.Path | TypeKind.String));
        if (node.Kind == HirKind.Transfer)
        {
            Require(left, hir.Type(new TypeRef(TypeKind.Path | TypeKind.String | TypeKind.Range)), leftSource);
            if (left.Element is { } item) Require(item, path, leftSource);
            Require(right, path, rightSource);
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [left.Node, right.Node], context));
        }
        Require(left, path, leftSource);
        Require(right, node.Kind == HirKind.Qualify ? hir.Type(TypeRef.Connection) : path, rightSource);
        return new(hir.Type(TypeRef.Path), Add(node, hir.Type(TypeRef.Path), [left.Node, right.Node], context));
    }

    private SemanticValue Cast(HirNode node, SemanticValue value, SemanticContext context)
    {
        HirTypeId target = ResolveHirType(new(node.Data), context);
        TypeRef expected = hir.Types[target.Value].RuntimeType;
        if (!ExplicitConversions.Possible(Runtime(value), expected))
            throw Error(node.Source, $"Cannot cast {Runtime(value)} to {expected}.", "E0220");
        if (hir.Types[target.Value].Concept >= 0) Require(value, target, node.Source, context: context);
        if (expected.Accepts(Runtime(value))) return value;
        return new(target, Add(node with { Data = target.Value }, target, [value.Node], context),
            Element: expected.WithoutNull.Kind == TypeKind.Range && value.Element is { } item && expected.Element is { } element
                ? item with { Type = hir.Type(element) } : null);
    }

    private SemanticValue MemberRead(HirNodeId id, HirNode node, SemanticValue receiver, SemanticContext context)
    {
        if (node.Text is null)
        {
            if (observations is not null)
                return new(hir.Type(TypeRef.Any), Add(node with { Kind = HirKind.Error }, hir.Type(TypeRef.Any), [receiver.Node], context));
            throw Error(node.Source, "Select a member by name after '::'.", "E0205");
        }
        if (Runtime(receiver).Nullable) throw Error(node.Source, "Handle Null before selecting a member.", "E0201");
        if (Runtime(receiver) == TypeRef.Process && ProcessCapabilities.Members.TryGetValue(node.Text!, out var processType))
            return new(hir.Type(processType), Add(node with { Data = -1 }, hir.Type(processType), [receiver.Node], context));
        if (Runtime(receiver) != TypeRef.Scope)
            throw Error(node.Source, $"Cannot select '{node.Text}' from {Runtime(receiver)}; '::' requires Scope or Process.", "E0206");
        var scope = receiver.Scope;
        if (scope is not null && context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        if (scope is null || !scope.Fields.TryGetValue(node.Text!, out var value))
            throw SemanticDiagnosticFactory.MissingMember(hir.Sources[node.Source.Source.Value],
                new(node.Source.Start, node.Source.Length), node.Text!, scope?.Fields.Keys ?? []);
        int offset = FieldOffset(scope.Layout, node.Text!);
        HirLocationId? location = scope.Locations.TryGetValue(node.Text!, out var member) ? member : null;
        if (location is { } fieldLocation && context.Values.TryGetValue(fieldLocation, out var actual)) value = actual;
        if (location is { } flowLocation) value = FlowRead(id, flowLocation, value, context);
        value = RefreshIndexed(value, context);
        return value with { Node = Add(node with { Data = offset }, value.Type, [receiver.Node], context), Location = location };
    }

    private SemanticValue MemberAssign(HirNode node, SemanticValue receiver, SemanticValue value, SemanticContext context,
        HirSourceRange valueSource)
    {
        if (Runtime(receiver).Nullable || Runtime(receiver) != TypeRef.Scope || receiver.Scope is not { } scope)
            throw Error(node.Source, "Member assignment requires a non-null Scope.", "E0206");
        if (context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        int offset = FieldOffset(scope.Layout, node.Text!);
        if (offset < 0) throw SemanticDiagnosticFactory.MissingMember(hir.Sources[node.Source.Source.Value],
            new(node.Source.Start, node.Source.Length), node.Text!, scope.Fields.Keys);
        var field = hir.ScopeLayouts[scope.Layout].Fields[offset];
        HirTypeId target = field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType;
        try { Require(value, target, valueSource, allowImplicit: true, context: context); }
        catch (CompileException error) when (scope.Locations.TryGetValue(node.Text!, out var fieldLocation))
        {
            var definition = hir.Locations[fieldLocation.Value];
            SemanticDiagnosticFactory.FixedStorage(error, hir.Sources[definition.Source.Source.Value],
                definition, hir.Types[target.Value].RuntimeType);
            throw;
        }
        value = Convert(value, target, valueSource, context);
        CheckMemberContracts(scope, node.Text!, value, valueSource, context);
        CheckSessionMemberAssignment(scope, node.Text!, value, valueSource, context);
        InvalidateScopeAliases(scope, context);
        var updated = scope with { Fields = scope.Fields.SetItem(node.Text!, value) };
        context.Objects[scope.Region] = updated;
        context.FieldWrites.Add((scope.Region, node.Text!));
        context.RecordWrite(new(SemanticWriteKind.Field, value, Region: scope.Region, Field: node.Text!));
        if (scope.Locations.TryGetValue(node.Text!, out var location))
        {
            context.Values[location] = value;
            if (StandardCell(location, context) is { } canonical)
            {
                context.CellWrites.Add(canonical);
                MarkProgramCell(canonical, context);
            }
        }
        return new(hir.Type(TypeRef.Null), Add(node with { Data = offset }, hir.Type(TypeRef.Null), [receiver.Node, value.Node], context));
    }

    private HirTypeId RangeType(HirTypeId element)
    {
        return hir.Type(new HirType(new(TypeKind.Range, hir.Types[element.Value].RuntimeType), Element: element.Value));
    }

    private int FieldOffset(int layout, string name)
    {
        var fields = hir.ScopeLayouts[layout].Fields;
        for (int index = 0; index < fields.Length; index++)
            if (fields[index].Name == name) return index;
        return -1;
    }

    private void ValidatePattern(string pattern, HirSourceRange source)
    {
        try { _ = new Regex(pattern); }
        catch (ArgumentException error) { throw Error(source, $"Invalid regular expression: {error.Message}", "E0207"); }
    }
}
