using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int sessionPathLayout = graph.SessionGeneration?.HpirLayouts?.PathLayout ?? -1;
        private Dictionary<HpirPathOrigin, int>? sessionPathIds;
        private Dictionary<int, HpirSessionPathLiteral>? sessionPathRelocations;
        private HashSet<HpirPathOrigin>? preparedPathOrigins;

        internal bool RetainedPathOrigin(NativeConstant literal) => graph.SessionGeneration is { } session
            && literal.OriginId >= 0 && literal.OriginId < session.InheritedSources;

        internal void NotePreparedPath(LlvmInstruction operand)
        {
            if (graph.SessionGeneration is not { }
                || !TryContextualLiteral(operand, out var literal)
                || literal is not { Kind: NativeKind.Path, OriginId: >= 0 }) return;
            (preparedPathOrigins ??= []).Add(new(literal.OriginId, literal.OriginOffset));
        }

        internal bool TrySessionPath(LlvmGlobalVariable global, out int id)
        {
            id = -1;
            if (graph.SessionGeneration is not { }
                || !graph.Constants.TryGetValue(global, out var literal)
                || literal is not { Kind: NativeKind.Path, Path: null, Text: { } raw, OriginId: >= 0 }) return false;
            var origin = new HpirPathOrigin(literal.OriginId, literal.OriginOffset);
            var ids = sessionPathIds ??= [];
            if (ids.TryGetValue(origin, out id)) return true;

            _ = TextLayout();
            if (sessionPathLayout < 0)
            {
                sessionPathLayout = Layouts.Count;
                Layouts.Add(new(HpirPathImports.PathBytes, HpirPathImports.PathAlignment,
                    [HpirPathImports.Raw, HpirPathImports.Directory, HpirPathImports.Home]));
            }
            id = Globals.Count;
            Globals.Add(new(id, sessionPathLayout));
            ids.Add(origin, id);
            (sessionPathRelocations ??= []).Add(id, new(origin, raw));
            return true;
        }
    }

    private sealed partial class Body
    {
        private int sessionPathValues;

        private int PathOccurrenceAddress(LlvmInstruction site, int id)
        {
            int pointer = Reserve(8, 8);
            Add(new(HpirOp.AddressOfGlobal, pointer, Immediate: checked(id * 2L)), site.Source);
            ++sessionPathValues;
            return pointer;
        }

        private void LoadPathOccurrence(LlvmLoadInstruction load, int id)
        {
            geometry.CheckPathRecord(load.Type, load.Source);
            int pointer = PathOccurrenceAddress(load, id);
            foreach (var leaf in geometry.Leaves(load.Type))
                Copy(Width(leaf.Type), LeafLocation(load, leaf.Offset), Indirect(pointer, leaf.Offset, load), load.Source);
        }
    }
}
