using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal int FileByteImport(HpirImport import) => FutureImport(import);
    }

    private sealed partial class Body
    {
        private int byteSnapshots, byteTraversals, byteAppends;
        private int byteScratch = -1;

        private bool TryFileByteWrite(LlvmFileOutInst output)
        {
            if (output.Mode is not (LlvmFileOutMode.WriteBytes or LlvmFileOutMode.AppendBytes)) return false;
            LlvmValue path = output.Destination, range = output.Value;
            bool union = KnownPathUnion(path);
            bool textPath = !union && !Geometry.IsPathRecord(path.Type);
            if (textPath && (!geometry.IsGuestPointer(path.Type) || geometry.ReflectedType(output, 1)?.Kind != TypeKind.String))
                return false;
            if (FutureRoot(range)) return false;

            var vector = geometry.VectorShape(range);
            var transform = vector is null ? geometry.TransformShape(range) : null;
            if (vector is { } words ? !words.Empty && !words.Element.IsDoubleTy()
                : transform is not { Vector: not null }
                    || !transform.Callback.ReturnType.IsDoubleTy()
                    || geometry.FutureResult(transform.Callback)
                    || geometry.ReductionWork(transform.Callback) != LlvmRangeCallbackWork.Ready)
                return false;

            bool append = output.Mode == LlvmFileOutMode.AppendBytes;
            int destination;
            if (textPath) destination = TextCell(path) ? ObserveText(output, path) : Location(path);
            else if (union) destination = NormalizePathUnion(output, path, Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment));
            else destination = PathRecordAddress(path, output);

            if (byteScratch < 0) byteScratch = Reserve(HpirFileByteImports.ChunkBytes, 8);
            int buffer = Reserve(8, 8), capacity = Reserve(8, 8), capacityPtr = Reserve(8, 8), owner = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, buffer, byteScratch), output.Source);
            Add(new(HpirOp.AddressOf, capacityPtr, capacity), output.Source);
            EmitCall(output, ~geometry.FileByteImport(HpirFileByteImports.Open(append, textPath)), owner, [destination, capacityPtr]);
            int skip = BranchWhenZero(output, capacity);
            if (transform is null)
            {
                VectorTransformCursor cursor = default;
                if (!vector!.Value.Empty)
                {
                    int remaining = Reserve(8, 8), source = Location(range);
                    Copy(HpirWidth.U64, remaining, ~source, output.Source);
                    cursor = OpenSnapshotWords(output, source, remaining);
                }
                ConsumeBytes(output, owner, buffer, capacity, vector.Value.Empty,
                    (available, item) => PullVectorTransform(output, cursor, available, item));
                ++byteSnapshots;
            }
            else
            {
                var cursor = OpenTransform(output, range, transform);
                ConsumeBytes(output, owner, buffer, capacity, cursor.Empty, (available, item) =>
                {
                    int input = PullTransformInput(output, cursor, available);
                    int hasItem = CursorBranch(available, output.Source);
                    CallTransformCallback(output, cursor, input, item);
                    LinkCursorBranch(hasItem, hasItem + 1, code.Count);
                });
                ++byteTraversals;
            }
            LinkCursorBranch(skip, code.Count, skip + 1);
            if (append) ++byteAppends;
            return true;
        }

        private void ConsumeBytes(LlvmFileOutInst site, int owner, int buffer, int capacity, bool empty, Action<int, int> next)
        {
            var source = site.Source;
            int filled = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, filled), source);
            if (!empty)
            {
                int one = Reserve(8, 8), limit = Reserve(8, 8), address = Reserve(8, 8), ordinal = Reserve(8, 8);
                int available = Reserve(1, 1), fits = Reserve(1, 1), item = Reserve(8, 8);
                Add(new(HpirOp.ConstU64, one, Immediate: 1), source);
                Add(new(HpirOp.ConstU64, limit, Immediate: byte.MaxValue), source);
                int pull = code.Count;
                next(available, item);
                int hasItem = CursorBranch(available, source);
                Add(new(HpirOp.OrdinalF64ToU64, ordinal, item), source);
                Add(new(HpirOp.LeU64, fits, ordinal, limit), source);
                int valid = CursorBranch(fits, source);
                EmitFailure(site, HpirFileByteImports.InvalidByte);
                LinkCursorBranch(valid, code.Count, valid + 1);
                Add(new(HpirOp.AddPointer, address, buffer, filled), source);
                Copy(HpirWidth.U8, ~address, ordinal, source);
                Add(new(HpirOp.AddU64, filled, filled, one), source);
                Add(new(HpirOp.LtU64, fits, filled, capacity), source);
                int hasRoom = CursorBranch(fits, source);
                EmitCall(site, ~geometry.FileByteImport(HpirFileByteImports.Chunk(final: false)), capacity, [owner, buffer, filled]);
                Add(new(HpirOp.ConstU64, filled), source);
                Add(new(HpirOp.Jump, A: pull), source);
                LinkCursorBranch(hasRoom, pull, hasRoom + 1);
                LinkCursorBranch(hasItem, hasItem + 1, code.Count);
            }
            EmitCall(site, ~geometry.FileByteImport(HpirFileByteImports.Chunk(final: true)), -1, [owner, buffer, filled]);
        }

        private void ReportFileByteWrites(CompilationTrace? trace)
        {
            trace?.Metric("paths.file_byte_snapshot_writes", byteSnapshots);
            trace?.Metric("paths.file_byte_compiled_traversals", byteTraversals);
            trace?.Metric("paths.file_byte_appends", byteAppends);
        }
    }
}
