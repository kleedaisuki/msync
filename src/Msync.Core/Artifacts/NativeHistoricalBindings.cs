namespace Msync.Core.Native;

/// <summary>Native memory adapters for accepted historical typed artifacts, not source capabilities.</summary>
public sealed record NativeHistoricalBindingBundle(NativeProgram Program,
    IReadOnlyDictionary<string, int> Functions, IReadOnlyDictionary<TypeKind, int> StorageTypes,
    int LogicalTypeFunction, int TypeTestFunction);

public static class NativeHistoricalBindings
{
    public static NativeHistoricalBindingBundle Lower(IEnumerable<string> names, IEnumerable<TypeRef> logicalElements)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(logicalElements);
        return NativeLegacyV6Importer.Bindings(names, logicalElements);
    }
}

public static partial class NativeLegacyV6Importer
{
    internal static NativeHistoricalBindingBundle Bindings(IEnumerable<string> names, IEnumerable<TypeRef> logicalElements) =>
        Importer.Bindings(names, logicalElements);

    private sealed partial class Importer
    {
        internal static NativeHistoricalBindingBundle Bindings(IEnumerable<string> names, IEnumerable<TypeRef> logicalElements)
        {
            // Only the shared metadata tables and native emitter are used: no stack instructions or source replay.
            LegacyV6Module empty = new([], [], [], [], [], [], [], 0, ArtifactStage.Relocatable, null, 0, ModuleSemantics.Current);
            Importer owner = new(empty);
            TypeRef[] elements = logicalElements.Append(TypeRef.Any).Distinct().ToArray();
            foreach (IGrouping<TypeKind, TypeRef> group in elements.GroupBy(element => element.Kind))
            {
                TypeRef description = group.Aggregate(new TypeRef(TypeKind.Never), TypeRef.Union);
                owner.CreateStorage(owner.HistoricalPhysicalType(description), description);
            }
            Dictionary<string, int> functions = new(StringComparer.Ordinal);
            foreach (string name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!IsStorage(name)) throw new ArgumentException($"'{name}' is not a historical allocator binding.", nameof(names));
                functions.Add(name, owner.StorageAdapter(name));
            }
            Dictionary<TypeKind, int> storage = [];
            foreach (TypeKind kind in owner.storageShapes.Keys)
                storage.Add(kind, owner.Type(TypeKind.Scope, layouts: owner.storageShapes.Values
                    .Where(shape => kind == TypeKind.Any || (kind & shape.Carrier) == shape.Carrier)
                    .Select(shape => shape.Header).Distinct().Order().ToArray()));
            int logicalType = owner.MetadataAdapter(test: false);
            int typeTest = owner.MetadataAdapter(test: true);
            int entry = owner.adapters.Count, nil = owner.Type(TypeKind.Null), constant = owner.Constants.Count;
            owner.Constants.Add(new(NativeKind.Null));
            owner.adapters.Add(new("<historical:bindings>", [new(nil)],
                [new(NativeOp.Constant, 0, constant), new(NativeOp.Return, A: 0)], new([], nil, [])));
            NativeProgram program = owner.Run() with { EntryPoint = entry };
            NativeArtifact.Validate(program);
            return new(program, functions, storage, logicalType, typeTest);
        }

        private int HistoricalPhysicalType(TypeRef logical)
        {
            TypeKind kind = logical.Kind & ~TypeKind.Storage;
            if ((logical.Kind & TypeKind.Storage) != 0) kind |= TypeKind.Scope;
            return Type(kind, (logical.Kind & TypeKind.Range) != 0 && logical.Element is not null
                ? HistoricalPhysicalType(logical.Element) : -1);
        }

        private int MetadataAdapter(bool test)
        {
            int identity = adapters.Count;
            int any = Type(TypeKind.Any), type = Type(TypeKind.Type), result = Type(test ? TypeKind.Bool : TypeKind.Type);
            int[] parameters = test ? [any, type] : [any];
            LegacyV6Function function = new(test ? "<historical:is_type>" : "<historical:value_type>", parameters.Length,
                new(parameters, result, [], [], false), []);
            adapters.Add(new FunctionImport(this, identity, function).MetadataAdapter(test));
            return identity;
        }

        private sealed partial class FunctionImport
        {
            internal NativeFunctionCode MetadataAdapter(bool test)
            {
                emitting = true;
                foreach (int parameter in function.Signature.Parameters) slots.Add(new(parameter, NativeSlotRole.Parameter));
                Item value = new(function.Signature.Parameters[0], Register: 0);
                Item result = test ? TypeTest(value, new(function.Signature.Parameters[1], Register: 1)) : ValueDescription(value);
                Add(new(NativeOp.Return, A: result.Register));
                Compact();
                return new(function.Name, slots.ToArray(), code.ToArray(),
                    new(function.Signature.Parameters, function.Signature.Result, []), function.Signature.Parameters.Count);
            }
        }
    }
}
