using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue CommonTypeOf(SemanticValue[] arguments, HirSourceRange source)
    {
        SemanticValue result = ValueForType(hir.Type(new TypeRef(TypeKind.Never)));
        foreach (var argument in arguments)
        {
            if (argument.DenotedType is not { } type)
                throw Error(source, "common_type_of requires statically inferred Type values.", "E0222");
            result = Join(result, ValueForType(type));
        }
        return MetaType(result.Type);
    }

    private MetaQueryResult DescribeSignature(SemanticValue operand)
    {
        SemanticValue value = MetaWitness(operand);
        if (Runtime(value).Nullable) return MetaNull();
        if (Runtime(value) == TypeRef.Path)
            return SignatureDescriptor(new([], hir.Type(TypeRef.Process), true));

        DeclaredSignature? signature = null;
        foreach (var choice in Choices(value))
        {
            if (CallableDeclaration(choice) is not { } next) return MetaNull();
            if (signature is { } previous && (previous.Variadic != next.Variadic
                || previous.Result != next.Result || !previous.Parameters.SequenceEqual(next.Parameters)))
                return MetaNull();
            signature = next;
        }
        int signatureId = hir.Types[value.Type.Value].Signature;
        if (signature is null && signatureId >= 0)
        {
            var shape = hir.Signatures[signatureId];
            signature = new(shape.Parameters, shape.Result, shape.Variadic);
        }
        return signature is { } declared ? SignatureDescriptor(declared) : MetaNull();
    }

    private MetaQueryResult SignatureDescriptor(DeclaredSignature signature) => MetaRecord(
        ("parameters", MetaRange(signature.Parameters.Select(MetaType), hir.Type(TypeRef.Type))),
        ("arity", MetaLiteral(TypeRef.Number, new(HirLiteralKind.Number, Number: signature.Parameters.Length))),
        ("result", MetaType(signature.Result ?? hir.Type(TypeRef.Any))),
        ("result_declared", MetaBoolean(signature.Result is not null)),
        ("variadic", MetaBoolean(signature.Variadic)));

    private MetaQueryResult DescribeMembers(SemanticValue value, SemanticContext context)
    {
        SemanticScope? scope = Runtime(value) == TypeRef.Scope && value.Scope is { } origin
            ? context.Objects.GetValueOrDefault(origin.Region, origin) : null;
        var fields = scope is null ? null : hir.ScopeLayouts[scope.Layout].Fields
            .ToDictionary(field => field.Name, StringComparer.Ordinal);
        var members = MetaMembers(value, context).OrderBy(pair => pair.Key, StringComparer.Ordinal);
        var rows = members.Select(pair =>
        {
            string name = pair.Key;
            var declared = fields?.GetValueOrDefault(name);
            SemanticValue current = pair.Value;
            HirTypeId storage = declared?.StorageType ?? current.Type;
            if (scope is not null && scope.Locations.TryGetValue(name, out var location))
            {
                current = context.Values.GetValueOrDefault(location, current);
                storage = StorageType(location, context);
            }
            bool admitsNull = declared?.AdmitsNull ?? hir.Types[storage.Value].RuntimeType.Nullable;
            return MemberDescriptor(name, current.Type, admitsNull ? Nullable(storage) : storage, admitsNull);
        }).ToArray();
        // An empty descriptor range has the same usable row shape as a populated one.
        HirTypeId any = hir.Type(TypeRef.Any);
        HirTypeId element = rows.Length == 0 ? MemberDescriptor("", any, any, false).Value.Type : rows[0].Value.Type;
        return MetaRange(rows, element);
    }

    private MetaQueryResult MemberDescriptor(string name, HirTypeId current, HirTypeId storage, bool admitsNull) => MetaRecord(
        ("name", MetaLiteral(TypeRef.String, new(HirLiteralKind.String, Text: name))),
        ("type", MetaType(current)),
        ("storage_type", MetaType(storage)),
        ("admits_null", MetaBoolean(admitsNull)));

    private MetaQueryResult MetaRecord(params (string Name, MetaQueryResult Result)[] fields)
    {
        var shape = fields.Select(field => new HirField(field.Name, field.Result.Value.Type)).ToImmutableArray();
        int layout = InternShape(shape);
        var values = fields.ToImmutableDictionary(field => field.Name, field => field.Result.Value, StringComparer.Ordinal);
        return new(new(hir.Type(new HirType(TypeRef.Scope, Shape: layout)), HirNodeId.None,
            Scope: new(-1, layout, values, ImmutableDictionary<string, HirLocationId>.Empty)),
            fields.Select(field => field.Result).ToImmutableArray());
    }

    private SemanticValue EmitMetaRecord(MetaQueryResult answer, SemanticScope scope,
        HirSourceRange source, SemanticContext context)
    {
        scope = scope with { Region = ++nextRegion };
        context.Objects[scope.Region] = scope;
        if (context.Output is not { } output) return answer.Value with { Scope = scope };

        var fields = hir.ScopeLayouts[scope.Layout].Fields;
        var locations = ImmutableDictionary.CreateBuilder<string, HirLocationId>(StringComparer.Ordinal);
        var members = scope.Fields.ToBuilder();
        var slots = ImmutableArray.CreateBuilder<HirLocationId>(fields.Length);
        var children = new HirNodeId[fields.Length];
        HirTypeId unit = hir.Type(TypeRef.Null);
        for (int index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var member = EmitMetaResult(answer.Items[index], source, context);
            var slot = output.ReserveLocation(new(field.Name, field.StorageType, source, context.Function,
                HirLocationKind.Field));
            slots.Add(slot);
            locations.Add(field.Name, slot);
            members[field.Name] = member;
            context.Values[slot] = member;
            context.Storage[slot] = field.StorageType;
            children[index] = output.Add(HirKind.Declare, unit, source, [member.Node], slot.Value);
        }
        scope = scope with { Fields = members.ToImmutable(), Locations = locations.ToImmutable() };
        context.Objects[scope.Region] = scope;
        int construction = output.AddScope(new(scope.Layout, slots.MoveToImmutable()));
        return answer.Value with
        {
            Scope = scope,
            Node = output.Add(HirKind.Scope, answer.Value.Type, source, children, construction)
        };
    }
}
