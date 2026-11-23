using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal TypeKind ExplicitCastTarget(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && graph.RuntimeConversions.TryGetValue(target, out var binding)
                && binding is { Operation: LlvmRuntimeConversionOperation.Convert, TargetType: >= 0 }
                ? graph.Types[binding.TargetType].Kind : TypeKind.Never;

        internal int ScalarParseImport(HpirService service) => FutureImport(HpirScalarImports.ParseCall(service));
        internal int ScalarFormatImport() => FutureImport(HpirScalarImports.NumberTextCall(TextLayout()));
    }

    private sealed partial class Body
    {
        private int primitiveCasts, boolTextCasts, stringParses, pathParses, scalarUnionCasts;

        private bool TryReadyScalarCast(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1) return false;
            TypeKind target = geometry.ExplicitCastTarget(call);
            if (target is not (TypeKind.Number or TypeKind.Bool or TypeKind.String)) return false;
            if (target == TypeKind.Number && !call.Type.IsDoubleTy() || target == TypeKind.Bool && !call.Type.IsIntegerTy(1)
                || target == TypeKind.String && !geometry.IsGuestPointer(call.Type)) return false;
            LlvmValue value = call.GetArgument(0);
            if (FutureRoot(value)) return false;
            var domain = geometry.Union(value.Type);
            if (domain != TypeKind.Never)
                return TryScalarUnionCast(call, value, domain, target);

            if (target == TypeKind.Bool && value.Type.IsDoubleTy() && call.Type.IsIntegerTy(1))
            {
                Add(new(HpirOp.BoolFromF64, Location(call), Location(value)), call.Source);
                ++primitiveCasts;
                return true;
            }
            if (target == TypeKind.Number && value.Type.IsIntegerTy(1) && call.Type.IsDoubleTy())
            {
                Add(new(HpirOp.ConvertU8ToF64, Location(call), Location(value)), call.Source);
                ++primitiveCasts;
                return true;
            }
            if (target == TypeKind.String && value.Type.IsIntegerTy(1) && geometry.IsGuestPointer(call.Type))
            {
                SelectBoolText(call, Location(value), Location(call));
                ++boolTextCasts;
                return true;
            }
            if (!(target == TypeKind.Number && call.Type.IsDoubleTy() || target == TypeKind.Bool && call.Type.IsIntegerTy(1)))
                return false;
            if (Geometry.IsPathRecord(value.Type))
            {
                HpirService path = target == TypeKind.Bool ? HpirService.ParsePathBool : HpirService.ParsePathF64;
                EmitImportCall(call, geometry.ScalarParseImport(path), [PathRecordAddress(value, call)], safepoint: false);
                ++pathParses;
                return true;
            }
            if (!geometry.IsGuestPointer(value.Type) || geometry.ReflectedType(call, 0)?.Kind != TypeKind.String) return false;
            HpirService text = target == TypeKind.Bool ? HpirService.ParseBool : HpirService.ParseF64;
            EmitImportCall(call, geometry.ScalarParseImport(text), [Location(value)], safepoint: false);
            ++stringParses;
            return true;
        }

        private bool TryScalarUnionCast(LlvmCallInstruction call, LlvmValue value, TypeKind domain, TypeKind target)
        {
            const TypeKind supported = TypeKind.Number | TypeKind.Bool | TypeKind.Null | TypeKind.String | TypeKind.Path;
            if ((domain & ~supported) != 0) return false;
            int result = Location(call), cond = Reserve(1, 1);
            List<int> joins = [];
            for (uint mask = (uint)domain; mask != 0; mask &= mask - 1)
            {
                uint bit = mask & (0U - mask);
                var member = (TypeKind)bit;
                if (bit == mask)
                {
                    CastScalarArm(call, value, member, target, result);
                    break;
                }
                CompareTag(call, value, member, cond);
                int branch = code.Count;
                Add(new(HpirOp.Branch, A: cond, B: branch + 1), call.Source);
                CastScalarArm(call, value, member, target, result);
                if (member != TypeKind.Null)
                {
                    joins.Add(code.Count);
                    Add(new(HpirOp.Jump), call.Source);
                }
                code[branch] = code[branch] with { D = code.Count };
            }
            foreach (int join in joins) code[join] = code[join] with { A = code.Count };
            ++scalarUnionCasts;
            return true;
        }

        private void CastScalarArm(LlvmCallInstruction call, LlvmValue value, TypeKind member, TypeKind target, int result)
        {
            if (member == TypeKind.Null)
            {
                EmitFailure(call, $"Cannot cast Null 'null' to {new TypeRef(target)}. Handle Null first or admit Null in the target type.");
                return;
            }
            if (member == TypeKind.Path)
            {
                CastUnionPath(call, value, target, result);
                return;
            }
            uint field = member is TypeKind.Number or TypeKind.Bool ? 1U : 2U;
            int payload = LeafLocation(value, geometry.FieldOffset(value.Type, field));
            if (member == target)
            {
                Copy(member == TypeKind.Bool ? HpirWidth.U8 : HpirWidth.U64, result, payload, call.Source);
                return;
            }
            if (target == TypeKind.String)
            {
                if (member == TypeKind.Bool) SelectBoolText(call, payload, result);
                else EmitCall(call, ~geometry.ScalarFormatImport(), result, [payload]);
                return;
            }
            if (member == TypeKind.String)
            {
                HpirService service = target == TypeKind.Bool ? HpirService.ParseBool : HpirService.ParseF64;
                EmitCall(call, ~geometry.ScalarParseImport(service), result, [payload], safepoint: false);
                return;
            }
            Add(new(target == TypeKind.Bool ? HpirOp.BoolFromF64 : HpirOp.ConvertU8ToF64, result, payload), call.Source);
        }

        private void CastUnionPath(LlvmCallInstruction call, LlvmValue value, TypeKind target, int result)
        {
            int record = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment);
            int address = Reserve(8, 8);
            CopyUnionPath(call, value, record);
            Add(new(HpirOp.AddressOf, address, record), call.Source);
            if (target == TypeKind.String)
                EmitCall(call, ~geometry.PathImport(HpirPathOp.Display, false, false), result, [address]);
            else
            {
                HpirService service = target == TypeKind.Bool ? HpirService.ParsePathBool : HpirService.ParsePathF64;
                EmitCall(call, ~geometry.ScalarParseImport(service), result, [address], safepoint: false);
            }
        }

        private void SelectBoolText(LlvmCallInstruction call, int condition, int destination)
        {
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: condition, B: branch + 1, D: branch + 3), call.Source);
            Add(new(HpirOp.AddressOfStatic, destination, Immediate: geometry.TextLiteral("true")), call.Source);
            Add(new(HpirOp.Jump, A: branch + 4), call.Source);
            Add(new(HpirOp.AddressOfStatic, destination, Immediate: geometry.TextLiteral("false")), call.Source);
        }

        private void ReportScalarCasts(CompilationTrace? trace)
        {
            trace?.Metric("casts.primitive", primitiveCasts);
            trace?.Metric("casts.bool_text_literal", boolTextCasts);
            trace?.Metric("casts.string_parse", stringParses);
            trace?.Metric("casts.path_parse", pathParses);
            trace?.Metric("casts.scalar_union_sites", scalarUnionCasts);
        }
    }
}
