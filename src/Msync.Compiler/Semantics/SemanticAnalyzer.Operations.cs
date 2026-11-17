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
                if (context.Publish && observations is not null && !children.IsEmpty)
                    Analyze(children[0], context);
                return new(hir.Type(TypeRef.Any), Add(node, hir.Type(TypeRef.Any), [], context));
            case HirKind.TypeValue:
                return new(hir.Type(TypeRef.Type), Add(node, hir.Type(TypeRef.Type), [], context),
                    new(HirLiteralKind.Type, Type: hir.Types[node.Data].RuntimeType), DenotedType: new(node.Data));
            case HirKind.TypeOf:
                {
                    SemanticContext query = context.QueryFork();
                    var value = Analyze(children[0], query);
                    HirTypeId type = value.Scope is not null || value.Element is not null ? FlowType(value, query) : value.Type;
                    var result = node with { Kind = HirKind.TypeValue, Data = type.Value };
                    return new(hir.Type(TypeRef.Type), Add(result, hir.Type(TypeRef.Type), [], context),
                        new(HirLiteralKind.Type, Type: hir.Types[type.Value].RuntimeType), DenotedType: type);
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
                    return MemberAssign(node, children[0], receiver, value, context, program.Node(children[1]).Source);
                }
            case HirKind.IsType:
                {
                    var value = Analyze(children[0], context);
                    if (program.PatternFieldTests.Contains(id)) return AnalyzePatternField(node, value, context);
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
        if (node.Kind is HirKind.PathJoin or HirKind.QualifyOn or HirKind.Transfer)
        {
            var other = Analyze(children[1], context);
            if (node.Kind == HirKind.QualifyOn) observations?.RecordQualification(id, context);
            return PairOperation(node, operand!, other, context, program.Node(children[0]).Source, program.Node(children[1]).Source);
        }
        throw Error(node.Source, $"Unknown semantic operation '{node.Kind}'.");
    }

    private SemanticValue Conformance(HirTypeId target, HirNode node, HirNodeId operand, SemanticContext context)
    {
        var value = Analyze(operand, context);
        Require(value, target, node.Source, context: context);
        Trace?.Metric("conformance_checks", 1);
        return value;
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
        TypeRef pathname = new(TypeKind.Path | TypeKind.String);
        var path = hir.Type(pathname);
        if (node.Kind == HirKind.Transfer)
        {
            Require(left, hir.Type(new TypeRef(TypeKind.Path | TypeKind.String | TypeKind.Range)), leftSource);
            if (left.Element is { } item) Require(item, path, leftSource);
            Require(right, path, rightSource);
            return new(hir.Type(TypeRef.Bool), Add(node, hir.Type(TypeRef.Bool), [left.Node, right.Node], context));
        }
        if (node.Kind == HirKind.QualifyOn)
        {
            TypeRef first = Runtime(left), second = Runtime(right);
            if (pathname.Accepts(first) && TypeRef.Connection.Accepts(second)
                && first.Kind != TypeKind.Never && second.Kind != TypeKind.Never)
                throw SemanticDiagnosticFactory.QualificationOrder(hir.Sources[node.Source.Source.Value],
                    new(node.Source.Start, node.Source.Length));
            Require(left, hir.Type(TypeRef.Connection), leftSource);
        }
        else Require(left, path, leftSource);
        Require(right, path, rightSource);
        return new(hir.Type(TypeRef.Path), Add(node, hir.Type(TypeRef.Path), [left.Node, right.Node], context));
    }

    private SemanticValue Cast(HirNode node, SemanticValue value, SemanticContext context)
    {
        HirTypeId target = ResolveHirType(new(node.Data), context);
        TypeRef expected = hir.Types[target.Value].RuntimeType;
        if (!ExplicitConversions.Possible(Runtime(value), expected))
            throw Error(node.Source, $"Cannot cast {Runtime(value)} to {expected}.", "E0220");
        if (hir.Types[target.Value].Concept >= 0) Require(value, target, node.Source, context: context);
        HirType description = hir.Types[target.Value];
        bool structural = expected.WithoutNull == TypeRef.Scope
            && (description.Shape >= 0 || !description.Alternatives.IsDefaultOrEmpty);
        if (expected.Accepts(Runtime(value)) && !structural) return value;
        if (structural)
        {
            HirTypeId view = StructuralStorageType(target);
            HirTypeId result = ViewOf(view, value.Type);
            SemanticValue narrowed = RetypeFlowValue(value, result, context, []);
            return narrowed with
            {
                Type = result,
                CheckedType = view,
                Node = Add(node with { Data = view.Value }, result, [value.Node], context)
            };
        }
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
            return new(hir.Type(processType), Add(node with
            {
                Data = -1,
                Effects = node.Effects | NativeEffects(NativeSignatures.Get("process." + node.Text))
            },
                hir.Type(processType), [receiver.Node], context));
        if (Runtime(receiver) == TypeRef.Process)
            throw Error(node.Source, $"Process has no member '{node.Text}'.", "E0205");
        if (Runtime(receiver) == TypeRef.Connection)
            throw Error(node.Source, "Write 'connection @ path' to qualify a path; '::' selects lexical members.", "E0206");
        if (Runtime(receiver) != TypeRef.Scope)
            throw Error(node.Source, $"Cannot select '{node.Text}' from {Runtime(receiver)}; '::' requires Scope or Process.", "E0206");
        HirTypeId receiverType = SchemaType(receiver);
        if (receiver.CheckedType is { } checkedReceiver)
        {
            HirFlowTypeAlgebra algebra = new(hir);
            HirTypeId view = hir.Types[checkedReceiver.Value].RuntimeType.Accepts(Runtime(receiver))
                ? algebra.Narrow(checkedReceiver, Runtime(receiver), true) : checkedReceiver;
            receiverType = ViewOf(view, receiver.Type);
        }
        receiver = receiver with { Type = receiverType };
        var scope = receiver.Scope ?? ValueForType(receiver.Type).Scope;
        if (scope is not null && context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        SemanticValue? selected = null;
        if (scope is null || !TryScopeField(scope, node.Text!, context, out selected))
        {
            SemanticScope? schema = ValueForType(receiver.Type).Scope;
            if (scope is null) scope = schema;
            else
            {
                HirField? declared = hir.ScopeLayouts[scope.Layout].Fields.FirstOrDefault(field => field.Name == node.Text);
                if (declared is not null) scope = scope with
                {
                    Fields = scope.Fields.SetItem(node.Text!,
                    selected = ValueForType(declared.AdmitsNull ? Nullable(declared.StorageType) : declared.StorageType))
                };
                else scope = schema ?? scope;
            }
        }
        if (scope is null || selected is null && !TryScopeField(scope, node.Text!, context, out selected))
            throw SemanticDiagnosticFactory.MissingMember(hir.Sources[node.Source.Source.Value],
                new(node.Source.Start, node.Source.Length), node.Text!, scope?.Fields.Keys ?? []);
        var value = selected!;
        int offset = FieldOffset(scope.Layout, node.Text!);
        HirLocationId? location = scope.Locations.TryGetValue(node.Text!, out var member) ? member : null;
        if (location is { } fieldLocation && context.Values.TryGetValue(fieldLocation, out var actual)) value = actual;
        if (location is { } flowLocation) value = FlowRead(id, flowLocation, value, context);
        if (receiver.CheckedType is { } checkedType)
        {
            HirFlowTypeAlgebra algebra = new(hir);
            HirTypeId memberType = StructuralStorageType(algebra.Member(checkedType, node.Text!));
            if (hir.Types[memberType.Value].RuntimeType.Accepts(Runtime(value)))
                memberType = algebra.Narrow(memberType, Runtime(value), true);
            HirTypeId result = ViewOf(memberType, value.Type);
            value = RetypeFlowValue(value, result, context, []) with { Type = result, CheckedType = memberType };
        }
        if (hir.Types[receiver.Type.Value].ScopeDomain.Count == 0)
            value = WithoutLayoutFacts(value);
        var storageField = hir.ScopeLayouts[scope.Layout].Fields[offset];
        HirTypeId constraint = location is { } stored ? StorageType(stored, context)
            : value.StorageConstraint is { } carrier ? InferredStorageType(carrier)
            : InferredStorageType(storageField.AdmitsNull ? Nullable(storageField.StorageType) : storageField.StorageType);
        // Recursive type records can share a symbolic row. Each load still owns
        // its own source path, unless the slot was replaced by a different value.
        if (value.Origin is not null && receiver.Origin is { } input
            && !context.FieldWrites.Contains((scope.Region, node.Text!)))
            value = value with { Origin = new(input.Root, [.. input.Fields.AsSpan(), node.Text!]) };
        SemanticTypeRead? typeRead = value.TypeRead;
        if (value.DenotedType is { } denoted && scope.Region >= 0 && context.WriteEvents is { } events)
        {
            typeRead = new(scope.Region, node.Text!, denoted) { Target = receiver.Origin };
            events.Add(new(null, context.ActiveSingletonGuards, TypeRead: typeRead));
        }
        return value with
        {
            Node = Add(node with { Data = offset,
                Effects = receiver.CheckedType is null ? node.Effects : node.Effects | HirEffects.MayFail },
                value.Type, [receiver.Node], context),
            Location = location,
            StorageConstraint = constraint,
            TypeRead = typeRead
        };

    }

    private bool TryScopeField(SemanticScope scope, string name, SemanticContext context, out SemanticValue value)
    {
        scope = context.Objects.GetValueOrDefault(scope.Region, scope);
        if (scope.Locations.TryGetValue(name, out var location) && context.Values.TryGetValue(location, out value!))
            return true;
        if (scope.Fields.TryGetValue(name, out value!)) return true;
        return context.InputFields?.TryField(scope, name, context, out value!) == true;
    }

    private SemanticValue MemberAssign(HirNode node, HirNodeId receiverExpression, SemanticValue receiver, SemanticValue value,
        SemanticContext context, HirSourceRange valueSource)
    {
        receiver = receiver with { Type = SchemaType(receiver) };
        if (Runtime(receiver).Nullable || Runtime(receiver) != TypeRef.Scope)
            throw Error(node.Source, "Member assignment requires a non-null Scope.", "E0206");
        if (receiver.CheckedType is { } checkedType)
            receiver = receiver with { Type = ViewOf(new HirFlowTypeAlgebra(hir).Narrow(checkedType, Runtime(receiver), true), receiver.Type) };
        if (receiver.Scope is null) receiver = receiver with { Scope = ValueForType(receiver.Type).Scope };
        if (receiver.Scope is not { } scope)
            throw Error(node.Source, "Member assignment requires a non-null Scope.", "E0206");
        if (scope.Region >= 0 && context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        if (FieldOffset(scope.Layout, node.Text!) < 0
            && ValueForType(receiver.Type).Scope is { } schema && schema.Fields.ContainsKey(node.Text!))
        {
            scope = schema with { Aliases = ScopeAliases(scope).Distinct().Order().ToImmutableArray() };
            receiver = receiver with { Scope = scope };
        }
        if (scope.Region < 0)
        {
            // A summary describes a possible receiver, not an object-table
            // entry. Give this write its own alias view.
            var view = DynamicElement(receiver, context).Scope!;
            if (Subject(receiverExpression, context) is { } current && ReferenceEquals(current.Scope, scope))
                SetSubject(receiverExpression, current with { Scope = view }, context);
            scope = view;
        }
        int offset = FieldOffset(scope.Layout, node.Text!);
        if (offset < 0) throw SemanticDiagnosticFactory.MissingMember(hir.Sources[node.Source.Source.Value],
            new(node.Source.Start, node.Source.Length), node.Text!, scope.Fields.Keys);
        var field = hir.ScopeLayouts[scope.Layout].Fields[offset];
        HirLocationId? storageLocation = scope.Locations.TryGetValue(node.Text!, out var actualField) ? actualField : null;
        HirTypeId target = storageLocation is { } stored ? StorageType(stored, context)
            : scope.Fields.GetValueOrDefault(node.Text!)?.StorageConstraint is { } carrier ? InferredStorageType(carrier)
            : InferredStorageType(field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType);
        try { Require(value, target, valueSource, allowImplicit: true, context: context); }
        catch (CompileException error) when (scope.Locations.TryGetValue(node.Text!, out var fieldLocation))
        {
            var definition = hir.Locations[fieldLocation.Value];
            SemanticDiagnosticFactory.FixedStorage(error, hir.Sources[definition.Source.Source.Value],
                definition, hir.Types[target.Value].RuntimeType);
            throw;
        }
        if (storageLocation is { } cell)
        {
            var resolved = ResolveHirType(target, context);
            if (field.AdmitsNull) resolved = Nullable(resolved);
            if (resolved != target) SealStorage(cell, resolved, context);
            target = resolved;
        }
        value = Convert(value, target, valueSource, context);
        value = value with { StorageConstraint = target };
        context.InputFields?.AdmitMemberContract(scope.Region, node.Text!, context);
        CheckMemberContracts(scope, node.Text!, value, valueSource, context);
        CheckSessionMemberAssignment(scope, node.Text!, value, valueSource, context);
        CheckClosedMemberAssignment(scope, node.Text!, value, valueSource, context);
        InvalidateScopeAliases(scope, node.Text!, value, context);
        var updated = scope with { Fields = scope.Fields.SetItem(node.Text!, value) };
        context.Objects[scope.Region] = updated;
        context.FieldWrites.Add((scope.Region, node.Text!));
        context.RecordWrite(new(SemanticWriteKind.Field, value, Region: scope.Region, Field: node.Text!, Target: receiver.Origin));
        if (scope.Locations.TryGetValue(node.Text!, out var location))
        {
            context.Values[location] = value;
            if (StandardCell(location, context) is { } canonical)
            {
                context.CellWrites.Add(canonical);
                MarkProgramCell(canonical, context);
            }
        }
        return new(hir.Type(TypeRef.Null), Add(node with { Data = offset,
            Effects = receiver.CheckedType is null ? node.Effects : node.Effects | HirEffects.MayFail },
            hir.Type(TypeRef.Null), [receiver.Node, value.Node], context));
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
