using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private readonly Dictionary<LlvmValue, int> temporaries = [];

        private void PrepareTemporaries(CompilationTrace? trace)
        {
            var selected = new List<(LlvmInstruction Value, HpirWidth Width, int Color)>();
            var index = new Dictionary<LlvmInstruction, int>();
            int[] peaks = new int[9];
            foreach (var block in function.BasicBlocks)
            {
                index.Clear();
                foreach (var instruction in block.Instructions) index.Add(instruction, index.Count);
                if (index.Count < 2) continue;
                var banks = new List<int>?[peaks.Length];
                foreach (var instruction in block.Instructions)
                {
                    if (!IsLocalValue(instruction, out HpirWidth width)) continue;
                    int start = index[instruction];
                    int end = LifetimeEnd(instruction, index, start);
                    if (end < 0) continue;
                    var bank = banks[(int)width] ??= [];
                    int color = PutTemporary(bank, start, end);
                    peaks[(int)width] = Math.Max(peaks[(int)width], color + 1);
                    selected.Add((instruction, width, color));
                }
            }

            int[] bases = new int[peaks.Length];
            int cells = 0, bytes = 0;
            for (int width = 8; width != 0; width /= 2)
            {
                if (peaks[width] == 0) continue;
                int extent = checked(width * peaks[width]);
                bases[width] = Reserve(extent, width);
                cells += peaks[width];
                bytes += extent;
            }
            foreach (var (value, width, color) in selected)
                temporaries.Add(value, bases[(int)width] + color * (int)width);
            trace?.Metric("frame.temporaries", selected.Count);
            trace?.Metric("frame.temporary_cells", cells);
            trace?.Metric("frame.temporary_bytes", bytes);
            trace?.Metric("frame.temporary_reuses", selected.Count - cells);
        }

        private bool IsLocalValue(LlvmInstruction instruction, out HpirWidth width)
        {
            width = instruction.Type switch
            {
                LlvmIntegerType { BitWidth: 1 or 8 } => HpirWidth.U8,
                LlvmIntegerType { BitWidth: 16 } => HpirWidth.U16,
                LlvmIntegerType { BitWidth: 32 } => HpirWidth.U32,
                LlvmIntegerType { BitWidth: 64 } => HpirWidth.U64,
                _ when instruction.Type.IsDoubleTy() => HpirWidth.U64,
                _ => HpirWidth.Void
            };
            return !FutureCell(instruction) && width != HpirWidth.Void && instruction is LlvmLoadInstruction or LlvmBinaryOperator
                or LlvmUnaryOperator or LlvmCmpInstruction or LlvmSelectInstruction or LlvmCallInstruction;
        }

        private static int LifetimeEnd(LlvmInstruction instruction, Dictionary<LlvmInstruction, int> positions, int start)
        {
            int end = start;
            foreach (var use in instruction.Uses)
            {
                // These users can alias a location or lower to a copy on an edge.
                // Their operands belong to ordinary fixed storage, not the local bank.
                if (use.User is not LlvmInstruction consumer || consumer is LlvmPhiInstruction
                    or LlvmInsertValueInstruction or LlvmCastInstruction or LlvmFreezeInstruction
                    || !positions.TryGetValue(consumer, out int position) || position <= start)
                    return -1;
                end = Math.Max(position, end);
            }
            return end;
        }

        private static int PutTemporary(List<int> lastUses, int start, int end)
        {
            for (int index = 0; index < lastUses.Count; ++index)
            {
                if (lastUses[index] >= start) continue;
                lastUses[index] = end;
                return index;
            }
            int added = lastUses.Count;
            lastUses.Add(end);
            return added;
        }
    }
}
