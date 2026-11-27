using System.Numerics;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal TypeKind Union(LlvmType type) => type is LlvmStructType record
            ? graph.UnionTypes.GetValueOrDefault(record) : TypeKind.Never;

        internal LlvmRuntimeConversionOperation? RepresentationOperation(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && graph.RuntimeConversions.TryGetValue(target, out var binding)
                ? binding.Operation : null;

        internal (TypeRef? Type, IReadOnlyList<int>? Layouts) ConversionTarget(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.RuntimeConversions.TryGetValue(target, out var binding)
                || binding.TargetType < 0) return default;
            var type = graph.Types[binding.TargetType];
            return (ReflectedType(binding.TargetType), type.Layouts);
        }

        internal TypeKind Member(LlvmValue value, TypeKind domain)
        {
            if (value.Type.IsDoubleTy()) return domain & TypeKind.Number;
            if (value.Type.IsIntegerTy(1)) return domain & TypeKind.Bool;
            if (IsPathRecord(value.Type)) return domain & TypeKind.Path;
            if (value.Type is LlvmStructType { Name: "msync.function" }) return domain & TypeKind.Function;
            if (value.Type is LlvmStructType { IsOpaque: false, Elements.Count: 0 }) return domain & TypeKind.Null;
            if (!IsGuestPointer(value.Type)) return TypeKind.Never;
            var possible = domain & ~(TypeKind.Null | TypeKind.Bool | TypeKind.Number | TypeKind.Path | TypeKind.Function);
            return (uint)value.LogicalType < (uint)graph.Types.Count
                ? possible & graph.Types[value.LogicalType].Kind : possible;
        }
    }

    private sealed partial class Body
    {
        private int unionPacks, unionProjections, unionRepacks, unionTagReads;

        private bool TryUnionCall(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1 || geometry.RepresentationOperation(call) is not { } operation) return false;
            var value = call.GetArgument(0);
            var from = geometry.Union(value.Type);
            if (operation == LlvmRuntimeConversionOperation.IsNull)
            {
                if (FutureRoot(value)) return false;
                if ((from & TypeKind.Null) != 0) CompareTag(call, value, TypeKind.Null, Location(call));
                else if (geometry.IsGuestPointer(value.Type))
                {
                    int zero = Reserve(8, 8);
                    Add(new(HpirOp.ConstU64, zero), call.Source);
                    Add(new(HpirOp.EqU64, Location(call), Location(value), zero), call.Source);
                }
                else Add(new(HpirOp.ConstU8, Location(call), Immediate: geometry.Member(value, TypeKind.Null) != 0 ? 1 : 0), call.Source);
                return true;
            }
            var to = geometry.Union(call.Type);
            if (FutureRoot(value) && !(FutureCell(value) && UnionCell(call))) return false;
            if (operation != LlvmRuntimeConversionOperation.Represent)
            {
                if (!TryAdmitConversion(call, value, operation, from)) return false;
                if (from == TypeKind.Never && to == TypeKind.Never && ReferenceEquals(call.Type, value.Type))
                {
                    CopySameRepresentation(call, value);
                    return true;
                }
            }
            else if (call.UseEmpty) return true;
            if (from == TypeKind.Never && to == TypeKind.Never)
            {
                if (!geometry.IsGuestPointer(call.Type) || geometry.Member(value, TypeKind.Null) == 0) return false;
                Add(new(HpirOp.ConstU64, Location(call)), call.Source);
                return true;
            }
            if (from == TypeKind.Never) PackUnion(call, value, UnionMember(call, value, to));
            else if (to == TypeKind.Never) ProjectUnion(call, value, UnionMember(call, call, from));
            else RepackUnion(call, value, from, to);
            return true;
        }

        private void CopySameRepresentation(LlvmCallInstruction call, LlvmValue value)
        {
            if (Aggregate(call.Type))
                foreach (var leaf in geometry.Leaves(call.Type))
                    Copy(Width(leaf.Type), LeafLocation(call, leaf.Offset), LeafLocation(value, leaf.Offset), call.Source);
            else Copy(Width(call.Type), Location(call), Location(value), call.Source);
        }

        private bool TryAdmitConversion(LlvmCallInstruction call, LlvmValue value, LlvmRuntimeConversionOperation operation, TypeKind from)
        {
            if (operation is not (LlvmRuntimeConversionOperation.Narrow or LlvmRuntimeConversionOperation.Convert
                or LlvmRuntimeConversionOperation.Coerce)) return false;
            var target = geometry.ConversionTarget(call);
            if (target.Type is not { } expected) return false;
            var type = geometry.ReflectedType(call, 0);
            var actual = from != TypeKind.Never ? from : type?.Kind ?? TypeKind.Never;
            if ((actual & expected.Kind) == 0 || ((actual & expected.Kind & TypeKind.Range) != 0
                && expected.Element is { } item && type?.Element is { } supplied && !item.Accepts(supplied))) return false;
            if ((actual & ~expected.Kind) != 0)
            {
                if (operation != LlvmRuntimeConversionOperation.Narrow || from == TypeKind.Never) return false;
                GuardUnion(call, value, actual & ~expected.Kind, expected.Kind);
            }
            if ((actual & expected.Kind & TypeKind.Scope) != 0 && target.Layouts is { Count: > 0 } layouts
                && !geometry.HasProvenLayout(value, layouts))
                GuardScopeConversion(call, value, from, expected.Nullable, layouts);
            return true;
        }

        private void GuardScopeConversion(LlvmCallInstruction call, LlvmValue value, TypeKind domain, bool nullable,
            IReadOnlyList<int> layouts)
        {
            int address, skip = -1;
            if (domain == TypeKind.Never)
            {
                address = Location(value);
                if (nullable)
                {
                    int zero = Reserve(8, 8), isNull = Reserve(1, 1);
                    Add(new(HpirOp.ConstU64, zero), call.Source);
                    Add(new(HpirOp.EqU64, isNull, address, zero), call.Source);
                    skip = code.Count;
                    Add(new(HpirOp.Branch, A: isNull, D: skip + 1), call.Source);
                }
            }
            else
            {
                address = LeafLocation(value, geometry.FieldOffset(value.Type, 2));
                int isScope = Reserve(1, 1);
                CompareTag(call, value, TypeKind.Scope, isScope);
                skip = code.Count;
                Add(new(HpirOp.Branch, A: isScope, B: skip + 1), call.Source);
            }
            GuardScope(call, address, layouts);
            if (skip >= 0) code[skip] = domain == TypeKind.Never
                ? code[skip] with { B = code.Count } : code[skip] with { D = code.Count };
        }

        private void GuardUnion(LlvmCallInstruction call, LlvmValue value, TypeKind rejected, TypeKind expected)
        {
            int condition = Reserve(1, 1);
            for (uint mask = (uint)rejected; mask != 0; mask &= mask - 1)
            {
                var kind = (TypeKind)(mask & (0U - mask));
                CompareTag(call, value, kind, condition);
                int branch = code.Count;
                Add(new(HpirOp.Branch, A: condition, B: branch + 1), call.Source);
                EmitFailure(call, $"Expected {new TypeRef(expected)}, received {new TypeRef(kind)}.");
                code[branch] = code[branch] with { D = code.Count };
            }
        }

        private void GuardScope(LlvmCallInstruction call, int address, IReadOnlyList<int> layouts)
        {
            int condition = Reserve(1, 1);
            int[] joins = new int[layouts.Count];
            for (int index = 0; index < layouts.Count; index++)
            {
                EmitCall(call, ~geometry.ProjectionLayoutImport(layouts[index], call.Source), condition, [address], safepoint: false);
                joins[index] = code.Count;
                Add(new(HpirOp.Branch, D: code.Count + 1, A: condition), call.Source);
            }
            EmitFailure(call, "Object allocation is outside its closed layout domain.");
            foreach (int join in joins) code[join] = code[join] with { B = code.Count };
        }

        private TypeKind UnionMember(LlvmCallInstruction call, LlvmValue value, TypeKind domain)
        {
            TypeKind member = geometry.Member(value, domain);
            if (!BitOperations.IsPow2((uint)member))
                throw Error($"a physical union member outside its declared domain {domain}", call);
            return member;
        }

        private static int Tag(TypeKind member) => BitOperations.TrailingZeroCount((uint)member);

        private void CompareTag(LlvmInstruction call, LlvmValue value, TypeKind member, int result)
        {
            int tag = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, tag, Immediate: Tag(member)), call.Source);
            Add(new(HpirOp.EqU8, result, LeafLocation(value, geometry.FieldOffset(value.Type, 0)), tag), call.Source);
            ++unionTagReads;
        }

        private void PackUnion(LlvmCallInstruction call, LlvmValue value, TypeKind member)
        {
            int output = RecordLocation(call);
            Add(new(HpirOp.ConstU8, output, Immediate: Tag(member)), call.Source);
            int raw = geometry.FieldOffset(call.Type, 1);
            int rawBytes = UnionRawBytes(call.Type);
            int usedRaw = member switch { TypeKind.Number => 8, TypeKind.Path => 2, TypeKind.Bool => 1, _ => 0 };
            if (rawBytes > usedRaw) Add(new(ConstOp((HpirWidth)rawBytes), output + raw, Immediate: 0), call.Source);
            int roots = geometry.FieldOffset(call.Type, 2);
            int storedRoots = 0;
            if (UnionCell(call))
                Copy(HpirWidth.U64, UnionCompletion(call), FutureCell(value) ? Location(value) : absentFuture, call.Source);
            foreach (var (leaf, offset) in UnionPayload(value.Type, member, raw, roots))
            {
                if (FutureInput(value) == HpirFutureInput.Deferred)
                    Add(new(ConstOp(Width(leaf.Type)), output + offset, Immediate: 0), call.Source);
                else
                {
                    int payload = FutureCell(value) ? Payload(value) : LeafLocation(value, leaf.Offset);
                    Copy(Width(leaf.Type), output + offset, payload, call.Source);
                }
                if (offset >= roots) ++storedRoots;
            }
            ClearUnionRoots(call, output + roots, storedRoots);
            ++unionPacks;
        }

        private void ProjectUnion(LlvmCallInstruction call, LlvmValue value, TypeKind member)
        {
            int raw = geometry.FieldOffset(value.Type, 1), roots = geometry.FieldOffset(value.Type, 2);
            if (FutureCell(call)) Copy(HpirWidth.U64, Location(call), UnionCompletion(value), call.Source);
            foreach (var (leaf, offset) in UnionPayload(call.Type, member, raw, roots))
            {
                int source = LeafLocation(value, offset);
                if (Aggregate(call.Type)) Copy(Width(leaf.Type), RecordLocation(call) + leaf.Offset, source, call.Source);
                else Copy(Width(leaf.Type), FutureCell(call) ? Payload(call) : Location(call), source, call.Source);
            }
            ++unionProjections;
        }

        private void RepackUnion(LlvmCallInstruction call, LlvmValue value, TypeKind from, TypeKind to)
        {
            if ((from & to) == TypeKind.Never) throw Error("disjoint physical unions", call);
            int output = RecordLocation(call);
            if (UnionCell(call)) Copy(HpirWidth.U64, UnionCompletion(call), UnionCompletion(value), call.Source);
            Copy(HpirWidth.U8, output, LeafLocation(value, 0), call.Source);
            int rawBytes = UnionRawBytes(call.Type), sharedRaw = Math.Min(rawBytes, UnionRawBytes(value.Type));
            int raw = geometry.FieldOffset(call.Type, 1);
            if (rawBytes > sharedRaw) Add(new(ConstOp((HpirWidth)rawBytes), output + raw, Immediate: 0), call.Source);
            if (sharedRaw != 0)
                Copy((HpirWidth)sharedRaw, output + raw, LeafLocation(value, geometry.FieldOffset(value.Type, 1)), call.Source);
            int roots = geometry.FieldOffset(call.Type, 2), inputRoots = geometry.FieldOffset(value.Type, 2);
            int count = Math.Min(UnionRootCount(call.Type), UnionRootCount(value.Type));
            for (int index = 0; index < count; ++index)
                Copy(HpirWidth.U64, output + roots + index * 8, LeafLocation(value, inputRoots + index * 8), call.Source);
            ClearUnionRoots(call, output + roots, count);
            ++unionRepacks;
        }

        private void ClearUnionRoots(LlvmCallInstruction call, int roots, int used)
        {
            for (int index = used; index < UnionRootCount(call.Type); ++index)
                Add(new(HpirOp.ConstU64, roots + index * 8, Immediate: 0), call.Source);
        }

        private IEnumerable<(Geometry.Leaf Leaf, int Offset)> UnionPayload(LlvmType type, TypeKind member, int raw, int roots)
        {
            var leaves = geometry.Leaves(type);
            int rawLeaves = member switch { TypeKind.Bool or TypeKind.Number => 1, TypeKind.Path => 2, _ => 0 };
            for (int index = 0; index < rawLeaves; ++index)
                yield return (leaves[index], raw + leaves[index].Offset);
            for (int index = rawLeaves; index < leaves.Length; ++index)
                yield return (leaves[index], roots + (index - rawLeaves) * 8);
        }

        private static int UnionRawBytes(LlvmType type) =>
            ((LlvmStructType)type).Elements[1] is LlvmIntegerType integer ? (int)integer.BitWidth / 8 : 0;

        private static int UnionRootCount(LlvmType type) =>
            checked((int)((LlvmArrayType)((LlvmStructType)type).Elements[2]).NumElements);
    }
}
