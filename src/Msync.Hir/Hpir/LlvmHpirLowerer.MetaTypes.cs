using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int typeNameImport = -1;

        internal string? MetaTypeOperation(LlvmCallInstruction call) =>
            call.CalledFunction is { } target ? graph.NativeCalls.GetValueOrDefault(target) : null;

        internal bool IsTypeWitness(LlvmCallInstruction call, int index) =>
            IsGuestPointer(call.GetArgument(index).Type) && ReflectedType(call, index)?.Kind == TypeKind.Type;

        internal int TypeNameImport()
        {
            if (typeNameImport >= 0) return typeNameImport;
            _ = TextLayout();
            typeNameImport = Imports.Count;
            Imports.Add(new(HpirService.TypeName, -1, HpirWidth.U64, [HpirWidth.U64]));
            return typeNameImport;
        }

        internal void FinishTypeNames(CompilationTrace? trace)
        {
            if (typeNameImport < 0) return;
            // Preserve Type witness ordinals in an immutable suffix of the
            // already-linked Text bank. Formatting is entirely producer work.
            var names = new string[TypeLiterals.Count];
            foreach (var (type, ordinal) in typeIds)
                names[ordinal] = type.ToString();
            Imports[typeNameImport] = Imports[typeNameImport] with { Binding = TextLiterals.Count };
            TextLiterals.AddRange(names);
            trace?.Metric("meta.type_display_literals", names.Length);
        }
    }

    private sealed partial class Body
    {
        private int sameTypeWitnesses, typeDisplayLookups;

        private bool TryMetaTypeCall(LlvmCallInstruction call)
        {
            switch (geometry.MetaTypeOperation(call))
            {
                case "meta.is_same" when call.ArgumentCount == 2 && call.Type.IsIntegerTy(1)
                    && geometry.IsTypeWitness(call, 0) && geometry.IsTypeWitness(call, 1):
                    // InternType assigns one handle to each structural TypeRef.
                    Add(new(HpirOp.EqU64, Location(call), Location(call.GetArgument(0)), Location(call.GetArgument(1))), call.Source);
                    ++sameTypeWitnesses;
                    return true;
                case "meta.type_name" when call.ArgumentCount == 1
                    && geometry.StringValue(call) && geometry.IsTypeWitness(call, 0):
                    EmitImportCall(call, geometry.TypeNameImport(), [Location(call.GetArgument(0))], safepoint: false);
                    ++typeDisplayLookups;
                    return true;
                default:
                    return false;
            }
        }

        private void ReportMetaTypes(CompilationTrace? trace)
        {
            if (sameTypeWitnesses != 0) trace?.Metric("meta.same_type_witnesses", sameTypeWitnesses);
            if (typeDisplayLookups != 0) trace?.Metric("meta.type_name_lookups", typeDisplayLookups);
        }
    }
}
