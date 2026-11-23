using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private enum VectorAccess : byte { None, At, Front, First, Size, Empty }

    private sealed partial class Geometry
    {
        internal bool KnownEmptyRangeConsumer(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && graph.EmptyRangeConsumers?.Contains(target) == true;

        internal LlvmSnapshotCarrier? SelectedSnapshot(LlvmCallInstruction call, int index = 0) =>
            SelectedCarrier(call, index) is { Completion: false } ready ? ready : null;

        internal LlvmSnapshotCarrier? SelectedCarrier(LlvmCallInstruction call, int index = 0) =>
            call.CalledFunction is { } target && graph.SnapshotRangeConsumers is { } consumers
            && consumers.TryGetValue(target, out var shape) ? index == 0 ? shape.First : shape.Second : null;

        internal VectorAccess VectorAccessFor(LlvmCallInstruction call) => call.CalledFunction is not { } target
            ? VectorAccess.None : graph.NativeCalls.GetValueOrDefault(target) switch
            {
                "ranges.at" => VectorAccess.At,
                "ranges.front" => VectorAccess.Front,
                "ranges.first" => VectorAccess.First,
                "ranges.size" => VectorAccess.Size,
                "ranges.empty" => VectorAccess.Empty,
                _ => VectorAccess.None
            };
    }

    private sealed partial class Body
    {
        private int vectorGets, vectorFronts, vectorFirsts, vectorSizes, vectorEmpties, vectorBoundsKnown;

        private bool TryVectorAccess(LlvmCallInstruction call)
        {
            if (call.ArgumentCount is not (1 or 2)) return false;
            VectorAccess operation = geometry.VectorAccessFor(call);
            bool indexed = operation == VectorAccess.At;
            if (operation == VectorAccess.None || call.ArgumentCount != (indexed ? 2 : 1)) return false;
            LlvmValue vector = call.GetArgument(0);
            if (geometry.KnownEmptyRangeConsumer(call)) return EmitKnownEmpty(call, operation);
            if (AccessSnapshot(call) is not { } shape) return false;
            if (shape.Empty && operation != VectorAccess.At) return EmitKnownEmpty(call, operation);
            if (operation == VectorAccess.Empty)
            {
                EmitEmptyLength(call, ~Location(vector));
                ++vectorEmpties;
                return true;
            }
            if (operation == VectorAccess.Size)
            {
                if (!call.Type.IsDoubleTy()) return false;
                Add(new(HpirOp.ConvertI64ToF64, Location(call), ~Location(vector)), call.Source);
                vectorSizes++;
                return true;
            }

            LlvmValue? subscript = indexed ? call.GetArgument(1) : null;
            if (indexed && (!subscript!.Type.IsDoubleTy() || FutureRoot(subscript))) return false;
            int? literalLength = shape.Empty ? 0 : vector is LlvmCallInstruction { CalledFunction: { } constructor } literal
                && geometry.IsVectorConstructor(constructor) ? literal.ArgumentCount : null;
            if (literalLength != 0 && call.Type != shape.Element) return false;
            int numericIndex = indexed ? Location(subscript!) : -1;
            switch (operation)
            {
                case VectorAccess.At: ++vectorGets; break;
                case VectorAccess.Front: ++vectorFronts; break;
                case VectorAccess.First: ++vectorFirsts; break;
            }
            ulong? constant = !indexed ? 0UL : subscript is LlvmConstantFP number
                ? HpirOrdinal.FromNumber(BitConverter.UInt64BitsToDouble(number.Value.BitcastToAPInt().GetZExtValue())) : null;
            if (constant == HpirOrdinal.Invalid || literalLength == 0
                || literalLength is { } length && constant >= (ulong)length)
            {
                VectorAccessFailure(call, operation, numericIndex);
                return true;
            }

            int owner = Location(vector);
            if (literalLength is not null && constant is { } literalIndex)
            {
                int offset = checked(HpirTransformImports.VectorHeaderBytes + (int)literalIndex * geometry.VectorStride(shape));
                ReadVectorElement(call, shape, owner, offset);
                vectorBoundsKnown++;
                return true;
            }

            int ordinal = Reserve(8, 8);
            Add(constant is { } scalar ? new(HpirOp.ConstU64, ordinal, Immediate: (long)scalar)
                : new(HpirOp.OrdinalF64ToU64, ordinal, numericIndex), call.Source);
            int available = Reserve(1, 1);
            Add(new(HpirOp.LtU64, available, ordinal, ~owner), call.Source);
            int branch = code.Count;
            Add(new(HpirOp.Branch, D: branch + 1, A: available), call.Source);
            VectorAccessFailure(call, operation, numericIndex);
            code[branch] = code[branch] with { B = code.Count };
            CopyVectorElement(call, shape, owner, indexed ? ordinal : -1);
            return true;
        }

        private bool EmitKnownEmpty(LlvmCallInstruction call, VectorAccess operation)
        {
            switch (operation)
            {
                case VectorAccess.Front:
                case VectorAccess.First:
                    VectorAccessFailure(call, operation, -1);
                    ++vectorBoundsKnown;
                    return true;
                case VectorAccess.Size:
                    Add(new(HpirOp.ConstU64, Location(call)), call.Source);
                    ++vectorSizes;
                    return true;
                case VectorAccess.Empty:
                    Add(new(HpirOp.ConstU8, Location(call), Immediate: 1), call.Source);
                    ++vectorEmpties;
                    return true;
                default:
                    return false;
            }
        }

        private void EmitEmptyLength(LlvmInstruction site, int length)
        {
            int zero = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, zero), site.Source);
            Add(new(HpirOp.EqU64, Location(site), length, zero), site.Source);
        }

        private void VectorAccessFailure(LlvmCallInstruction call, VectorAccess operation, int index)
        {
            switch (operation)
            {
                case VectorAccess.At: EmitCall(call, ~geometry.FailureImport(HpirFailure.RangeIndex), -1, [index], safepoint: false); break;
                case VectorAccess.First: EmitFailure(call, "ranges.first found no matching item"); break;
                default: EmitFailure(call, HpirFailure.RangeEmpty); break;
            }
        }

        private void CopyVectorElement(LlvmCallInstruction call, LlvmSnapshotCarrier shape, int vector, int ordinal)
        {
            int stride = geometry.VectorStride(shape);
            if (stride == 0) return;
            int data = Reserve(8, 8);
            Copy(HpirWidth.U64, data, Indirect(vector, HpirTransformImports.VectorData, call), call.Source);
            if (ordinal != -1)
            {
                int delta = Reserve(8, 8);
                Add(new(HpirOp.ConstU64, delta, Immediate: stride), call.Source);
                Add(new(HpirOp.MulU64, delta, delta, ordinal), call.Source);
                Add(new(HpirOp.AddPointer, data, data, delta), call.Source);
            }
            ReadVectorElement(call, shape, data, 0);
        }

        private void ReadVectorElement(LlvmCallInstruction call, LlvmSnapshotCarrier shape, int data, int offset)
        {
            if (shape.Completion)
            {
                Copy(HpirWidth.U64, Location(call), Indirect(data, offset + SnapshotCompletionOffset, call), call.Source);
                Copy(Width(call.Type), Payload(call), Indirect(data, offset, call), call.Source);
                return;
            }
            foreach (var leaf in geometry.Leaves(shape.Element))
                Copy(Width(leaf.Type), LeafLocation(call, leaf.Offset), Indirect(data, offset + leaf.Offset, call), call.Source);
        }

        private void ReportVectorAccess(CompilationTrace? trace)
        {
            trace?.Metric("vectors.at", vectorGets);
            trace?.Metric("vectors.front", vectorFronts);
            trace?.Metric("vectors.first", vectorFirsts);
            trace?.Metric("vectors.size", vectorSizes);
            trace?.Metric("vectors.empty", vectorEmpties);
            trace?.Metric("vectors.bounds_known", vectorBoundsKnown);
        }
    }
}
