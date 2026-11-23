using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly List<HpirTypeLiteral> TypeLiterals = graph.SessionGeneration?.HpirTypes is { } priorLiterals
            ? new(priorLiterals.Literals) : [];
        internal readonly List<int> TypeCells = [];
        private readonly Dictionary<TypeRef, int> typeIds = graph.SessionGeneration?.HpirTypes is { } priorTypes
            ? new(priorTypes.Ids) : [];
        private readonly List<TypeRef>? sessionTypeWitnesses = graph.SessionGeneration is { } session
            ? new(session.HpirTypes?.Witnesses ?? []) : null;
        private readonly Dictionary<int, int> typeCellIds = [];
        private readonly Dictionary<int, TypeRef> reflectedTypes = [];
        private int typeAccepts = -1;

        internal TypeRef? TypeLiteral(LlvmGlobalVariable global) =>
            graph.Constants.TryGetValue(global, out var constant) && constant.Kind == NativeKind.Type ? constant.Type : null;

        internal int InternType(TypeRef type)
        {
            if (typeIds.TryGetValue(type, out int index)) return index;
            int element = type.Element is not null ? InternType(type.Element) : -1;
            index = TypeLiterals.Count;
            TypeLiterals.Add(new(type.Kind, element));
            typeIds.Add(type, index);
            sessionTypeWitnesses?.Add(type);
            return index;
        }

        internal int TypeCell(TypeRef type)
        {
            int id = InternType(type);
            if (typeCellIds.TryGetValue(id, out int cell)) return cell;
            cell = TypeCells.Count;
            TypeCells.Add(id);
            typeCellIds.Add(id, cell);
            return cell;
        }

        internal TypeRef? ReflectedType(LlvmInstruction origin, int arg)
        {
            int logical = origin.GetOperandUse(arg).LogicalType;
            LlvmValue value = origin.GetOperand(arg)!;
            if ((uint)logical >= (uint)graph.Types.Count) logical = value.LogicalType;
            if ((uint)logical < (uint)graph.Types.Count && graph.Types[logical].Pointee < 0)
                return ReflectedType(logical);
            if (value.Type.IsDoubleTy()) return TypeRef.Number;
            if (value.Type.IsIntegerTy(1)) return TypeRef.Bool;
            if (IsPathRecord(value.Type)) return TypeRef.Path;
            if (value.Type is LlvmStructType { Name: "msync.function" }) return TypeRef.Function;
            if (value.Type is LlvmStructType { IsOpaque: false, Elements.Count: 0 }) return TypeRef.Null;
            return null;
        }

        private TypeRef ReflectedType(int logical)
        {
            if (reflectedTypes.TryGetValue(logical, out var known)) return known;
            var type = graph.Types[logical];
            var kinds = (type.Kind & TypeKind.Storage) == 0 ? type.Kind : (type.Kind & ~TypeKind.Storage) | TypeKind.Scope;
            var reflected = new TypeRef(kinds, type.Element < 0 ? null : ReflectedType(type.Element));
            reflectedTypes.Add(logical, reflected);
            return reflected;
        }

        internal int TypeAcceptImport()
        {
            if (typeAccepts >= 0) return typeAccepts;
            typeAccepts = Imports.Count;
            Imports.Add(new(HpirService.TypeAccepts, 0, HpirWidth.U8, [HpirWidth.U64, HpirWidth.U64]));
            return typeAccepts;
        }
    }

    private sealed partial class Body
    {
        private int typeLiteralLoads, staticTypePredicates, dynamicTypePredicates;

        private bool TryTypeLiteral(LlvmLoadInstruction load)
        {
            if (LiteralType(load) is not { } type) return false;
            EmitType(Location(load), type, load.Source);
            ++typeLiteralLoads;
            return true;
        }

        private TypeRef? LiteralType(LlvmValue value) => value is LlvmLoadInstruction { IsSimple: true,
            PointerOperand: LlvmGlobalVariable global } ? geometry.TypeLiteral(global) : null;

        private void EmitType(int location, TypeRef type, SourceSpan? source) =>
            Add(new(HpirOp.ConstU64, location, Immediate: geometry.InternType(type) + 1L), source);

        private bool TryTypeCall(LlvmCallInstruction call)
        {
            var operation = geometry.RepresentationOperation(call);
            if (operation == LlvmRuntimeConversionOperation.TypeOf && call.ArgumentCount == 1)
            {
                if (geometry.ReflectedType(call, 0) is not { } type) return false;
                EmitType(Location(call), type, call.Source);
                return true;
            }
            if (operation != LlvmRuntimeConversionOperation.IsType || call.ArgumentCount != 2
                || geometry.ReflectedType(call, 0) is not { } actual) return false;
            var value = call.GetArgument(0);
            var expected = call.GetArgument(1);
            int result = Location(call);
            var union = geometry.Union(value.Type);
            if (union == TypeKind.Never || FutureRoot(value))
            {
                int handle = actual.Kind == TypeKind.Type && !FutureRoot(value) ? Location(value) : -1;
                TypePredicate(call, result, expected, handle, handle < 0 ? actual : LiteralType(value));
                return true;
            }
            CompareUnionTypes(call, value, expected, actual, union, result);
            return true;
        }

        private void CompareUnionTypes(LlvmCallInstruction call, LlvmValue value, LlvmValue expected,
            TypeRef actual, TypeKind domain, int result)
        {
            List<int> exits = [];
            int comparison = Reserve(1, 1);
            for (uint mask = (uint)domain; mask != 0; mask &= mask - 1)
            {
                var member = (TypeKind)(mask & (0U - mask));
                CompareTag(call, value, member, comparison);
                int branch = code.Count;
                Add(new(HpirOp.Branch, A: comparison, B: branch + 1), call.Source);
                int handle = member == TypeKind.Type ? LeafLocation(value, geometry.FieldOffset(value.Type, 2)) : -1;
                TypePredicate(call, result, expected, handle, handle >= 0 ? null : new(member,
                    member is TypeKind.Range or TypeKind.Storage ? actual.Element : null));
                exits.Add(code.Count);
                Add(new(HpirOp.Jump), call.Source);
                code[branch] = code[branch] with { D = code.Count };
            }
            Add(new(HpirOp.ConstU8, result), call.Source);
            foreach (int exit in exits) code[exit] = code[exit] with { A = code.Count };
        }

        private void TypePredicate(LlvmCallInstruction call, int result, LlvmValue expected, int handle, TypeRef? actual)
        {
            if (actual is not null && LiteralType(expected) is { } constant)
            {
                Add(new(HpirOp.ConstU8, result, Immediate: constant.Accepts(actual) ? 1 : 0), call.Source);
                ++staticTypePredicates;
                return;
            }
            if (handle < 0) { handle = Reserve(8, 8); EmitType(handle, actual!, call.Source); }
            EmitCall(call, ~geometry.TypeAcceptImport(), result, [Location(expected), handle], safepoint: false);
            ++dynamicTypePredicates;
        }
    }
}
