using System.Numerics;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private int rangeUnionItemsPacked;

        private (LlvmType Type, int Logical, TypeKind Kind)? LiteralRangeElement(HirNode node)
        {
            var range = Hir.Types[node.Type.Value];
            if (range.Element >= 0)
            {
                var item = Hir.Types[range.Element];
                TypeKind kinds = item.RuntimeType.Kind | (item.Nullable ? TypeKind.Null : TypeKind.Never);
                if (!PackLiteralItems(kinds)) return null;
                HirTypeId element = new(range.Element);
                return (owner.Type(element), owner.metadata.Type(element), kinds);
            }
            return range.RuntimeType.Element is { } type && PackLiteralItems(type.Kind)
                ? (owner.RepresentationType(type.Kind), owner.metadata.RuntimeType(type), type.Kind) : null;
        }

        private bool PackLiteralItems(TypeKind kind) => kind == (TypeKind.Null | TypeKind.Number)
            || owner.session is { } && BitOperations.PopCount((uint)kind) > 1;

        private LlvmValue PackRangeItem(LlvmValue item, (LlvmType Type, int Logical, TypeKind Kind)? carrier)
        {
            if (carrier is not { } element || ReferenceEquals(item.Type, element.Type)) return item;
            var trace = owner.trace?.Detail;
            trace?.Metric("builtin.range.union_items_packed", ++rangeUnionItemsPacked);
            if (element.Kind == (TypeKind.Null | TypeKind.Number))
                trace?.Metric("builtin.range.nullable_number_items_packed", rangeUnionItemsPacked);
            return ConversionCall(LlvmRuntimeConversionOperation.Represent, element.Type, element.Logical, [item]);
        }

        private IReadOnlyList<LlvmValue> SelectConcatLiterals(HirNode node, IReadOnlyList<LlvmValue> arguments)
        {
            if (LiteralRangeElement(node) is not { } carrier) return arguments;
            LlvmValue[]? selected = null;
            for (int index = 0; index < arguments.Count; index++)
            {
                if (arguments[index] is not LlvmCallInstruction { CalledFunction: { } constructor, UseEmpty: true } literal
                    || owner.constructors.GetValueOrDefault(constructor) != NativeKind.Range
                    || literal.Arguments.All(item => item.Type == carrier.Type)) continue;
                // Select the common payload at the fresh producer. Do not
                // rewrite a snapshot that a different consumer already owns.
                var items = literal.Arguments.Select(item => PackRangeItem(item, carrier)).ToArray();
                (selected ??= arguments.ToArray())[index] = ConstructRange(node, items);
                literal.EraseFromParent();
            }
            return selected ?? arguments;
        }
    }
}
