using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private (int Match, int Group, int Vector, int Pointers)? regexFindGeometry;
        private int regexFindLiteralImport = -1, regexFindDynamicImport = -1;
        private readonly Dictionary<LlvmCallInstruction, bool> regexGroupOwnership = [];
        private readonly HashSet<LlvmValue> regexGroupVectors = [];

        internal bool RegexFindTarget(LlvmFunction target) => graph.NativeCalls.GetValueOrDefault(target) == "regex.find";

        internal LlvmType? RegexGroupsElement(LlvmValue value)
        {
            if (graph.RegexFindLayouts is null || value is not LlvmLoadInstruction { IsSimple: true,
                PointerOperand: LlvmGetElementPtrInstruction { HasAllConstantIndices: true } field } || !IsGroupField(field)
                || RegexFindOwner(field.PointerOperand) is not { } find) return null;
            if (!regexGroupOwnership.TryGetValue(find, out bool local))
                regexGroupOwnership.Add(find, local = KeepsGroupVector(find));
            if (!local) return null;
            regexGroupVectors.Add(value);
            return field.PointerOperand.Type;
        }

        internal bool TryRegexGroupLayoutTest(LlvmCallInstruction call, out bool matches)
        {
            matches = false;
            if (graph.RegexFindLayouts is not { } layouts || call.ArgumentCount != 1 || call.CalledFunction is not { } target
                || !graph.ValueOperations.TryGetValue(target, out var operation) || operation.Operation != LlvmRuntimeValueOperation.HasLayout
                || call.GetArgument(0) is not LlvmCallInstruction item || VectorAccessFor(item) is not (VectorAccess.At or VectorAccess.Front)
                || !regexGroupVectors.Contains(item.GetArgument(0))) return false;
            matches = operation.Mode == layouts.Group;
            return true;
        }

        private LlvmCallInstruction? RegexFindOwner(LlvmValue value)
        {
            while (value is LlvmCallInstruction { CalledFunction: { } target } call)
            {
                if (RegexFindTarget(target)) return call;
                if (RepresentationOperation(call) != LlvmRuntimeConversionOperation.Represent || call.ArgumentCount != 1) return null;
                value = call.GetArgument(0);
            }
            return null;
        }

        private bool IsGroupField(LlvmGetElementPtrInstruction field) =>
            graph.RegexFindLayouts is { } ids && ReferenceEquals(field.GetTargetStorageType(), structs[ids.Match])
            && Data.GetIndexedOffsetInType(field.SourceElementType, field.Indices.ToArray()) == HpirRegexResultImports.MatchGroups;

        // The ordinary record field is writable. Keep its initial vector fact
        // only while the result is local and no use can replace that field.
        private bool KeepsGroupVector(LlvmValue result)
        {
            foreach (var user in result.Users)
            {
                if (user is LlvmCallInstruction call)
                {
                    if (RepresentationOperation(call) == LlvmRuntimeConversionOperation.IsNull) continue;
                    if (RepresentationOperation(call) == LlvmRuntimeConversionOperation.Represent && KeepsGroupVector(call)) continue;
                    if (call.CalledFunction is { } target && graph.ValueOperations.TryGetValue(target, out var operation)
                        && operation.Operation == LlvmRuntimeValueOperation.HasLayout) continue;
                }
                else if (user is LlvmGetElementPtrInstruction { HasAllConstantIndices: true } field
                    && field.Users.All(use => use is LlvmLoadInstruction load && load.PointerOperand == field
                        || !IsGroupField(field) && IsReadAddress(use))) continue;
                else if (IsReadAddress(user)) continue;
                else if (user is LlvmICmpInstruction) continue;
                return false;
            }
            return true;
        }

        private static bool IsReadAddress(LlvmUser use) => use is LlvmSelectInstruction selected
            && selected.Users.All(user => user is LlvmLoadInstruction load && load.PointerOperand == selected);

        internal (int Import, int Group, int Vector, int Pointers) RegexFindImport(bool literal, SourceSpan? source)
        {
            var layout = regexFindGeometry ??= RegexFindGeometry(source);
            ref int import = ref (literal ? ref regexFindLiteralImport : ref regexFindDynamicImport);
            if (import < 0) import = FutureImport(HpirRegexResultImports.Find(layout.Match, literal));
            return (import, layout.Group, layout.Vector, layout.Pointers);
        }

        private (int Match, int Group, int Vector, int Pointers) RegexFindGeometry(SourceSpan? source)
        {
            if (graph.RegexFindLayouts is not { } logical) throw new MsyncException("Missing producer layouts for regex.find", source);
            int match = LayoutId(logical.Match, source), group = LayoutId(logical.Group, source);
            if (!HpirRegexResultImports.MatchLayout(Layouts[match]) || !HpirRegexResultImports.GroupLayout(Layouts[group]))
                throw new MsyncException("The regex.find producer layouts disagree with the physical record ABI", source);
            if (materializedLayout < 0) materializedLayout = AddLayout(HpirTransformImports.VectorLayout());
            _ = TextLayout();
            return (match, group, materializedLayout, AddLayout(new(sizeof(ulong), sizeof(ulong), [0])));
        }
    }

    private sealed partial class Body
    {
        private int regexFinds, regexGroupLayoutChecksElided;

        private bool TryRegexGroupLayoutTest(LlvmCallInstruction call)
        {
            if (!geometry.TryRegexGroupLayoutTest(call, out bool matches)) return false;
            Add(new(HpirOp.ConstU8, Location(call), Immediate: matches ? 1 : 0), call.Source);
            ++regexGroupLayoutChecksElided;
            return true;
        }

        private bool TryRegexFind(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !geometry.RegexFindTarget(target)) return false;
            TypeKind result = geometry.Union(call.Type);
            if (call.ArgumentCount != 2 || !geometry.IsGuestPointer(call.Type) && result != (TypeKind.Null | TypeKind.Scope)
                || call.Arguments.Any(argument => !geometry.IsGuestPointer(argument.Type) || FutureRoot(argument)))
                throw Error("a regex.find call without its ready String operands and nullable record result", call);
            var pattern = call.GetArgument(1);
            int literal = geometry.RegexLiteral(pattern);
            var recipe = geometry.RegexFindImport(literal >= 0, call.Source);
            int pointer = result == TypeKind.Never ? Location(call) : RecordLocation(call) + geometry.FieldOffset(call.Type, 2);
            EmitCall(call, ~recipe.Import, pointer, [Location(call.GetArgument(0)), literal < 0 ? Location(pattern) : Immediate(literal),
                Immediate(recipe.Group), Immediate(recipe.Vector), Immediate(recipe.Pointers)]);
            if (result != TypeKind.Never) RegexOptionalTag(call, pointer);
            ++regexFinds;
            if (literal >= 0) ++regexLiterals;
            return true;

            int Immediate(int value)
            {
                int location = Reserve(4, 4);
                Add(new(HpirOp.ConstU32, location, Immediate: value), call.Source);
                return location;
            }
        }

        private void RegexOptionalTag(LlvmCallInstruction call, int pointer)
        {
            int output = RecordLocation(call);
            int zero = Reserve(8, 8), missing = Reserve(1, 1);
            Add(new(HpirOp.ConstU64, zero), call.Source);
            Add(new(HpirOp.EqU64, missing, pointer, zero), call.Source);
            Add(new(HpirOp.ConstU8, output, Immediate: Tag(TypeKind.Scope)), call.Source);
            int branch = code.Count;
            Add(new(HpirOp.Branch, D: branch + 2, A: missing, B: branch + 1), call.Source);
            Add(new(HpirOp.ConstU8, output, Immediate: Tag(TypeKind.Null)), call.Source);
            ++unionPacks;
        }
    }
}
