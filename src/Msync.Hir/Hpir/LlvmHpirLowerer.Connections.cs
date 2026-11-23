using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal NativeConstant? ConnectionLiteral(LlvmGlobalVariable value) =>
            graph.Constants.TryGetValue(value, out var constant) && constant.Kind == NativeKind.Connection
                ? constant : null;

        internal bool IsConnection(LlvmValue value) => IsGuestPointer(value.Type)
            && (uint)value.LogicalType < (uint)graph.Types.Count && graph.Types[value.LogicalType].Kind == TypeKind.Connection;
    }

    private sealed partial class Body
    {
        private int connectionLiterals, pathQualifications;

        private bool TryConnectionLiteral(LlvmLoadInstruction load)
        {
            if (load.PointerOperand is not LlvmGlobalVariable global || geometry.ConnectionLiteral(global) is not { } connection)
                return false;
            EmitConnectionLiteral(Location(load), connection, load.Source);
            return true;
        }

        private void EmitConnectionLiteral(int output, NativeConstant connection, SourceSpan? source)
        {
            long immediate = HpirConnectionAbi.Literal(connection.ConnectionKind, geometry.TextLiteral(connection.Text ?? ""));
            Add(new(HpirOp.AddressOfConnection, output, Immediate: immediate), source);
            ++connectionLiterals;
        }

        private bool PrepareConnectionCell(LlvmGlobalVariable global)
        {
            if (geometry.ConnectionLiteral(global) is not { } connection) return false;
            int storage = Reserve(8, 8), address = Reserve(8, 8);
            EmitConnectionLiteral(storage, connection, null);
            Add(new(HpirOp.AddressOf, address, storage));
            operands.Add(global, address);
            return true;
        }

        private bool EmitQualification(LlvmCallInstruction call)
        {
            LlvmValue endpoint = call.GetArgument(0), input = call.GetArgument(1);
            if (!geometry.IsConnection(endpoint) || FutureRoot(endpoint)) return false;
            bool union = KnownPathUnion(input);
            bool text = !union && PathTextOperand(input, call);
            int output = PathRecordAddress(call, call, result: true);
            int path = PathCallAddress(call, input, text, union);
            EmitCall(call, ~geometry.PathImport(HpirPathOp.Qualify, false, text), -1,
                [output, Location(endpoint), path]);
            ++pathQualifications;
            return true;
        }

        private void ReportConnections(CompilationTrace? trace)
        {
            if (connectionLiterals != 0) trace?.Metric("connections.literal_relocations", connectionLiterals);
            if (pathQualifications != 0) trace?.Metric("paths.typed_qualifications", pathQualifications);
        }
    }
}
