using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly bool ChangesDirectory = graph.NativeCalls.Values.Contains("environment.set_working_directory");
        private readonly Dictionary<(HpirPathOp Operation, bool LeftText, bool RightText), int> pathImports = [];
        private LlvmStructType? pathRecord;
        internal bool PathAnchorsNeeded => pathImports.Count != 0 || LiveTextFileAnchors || HasFileMetadata || HasFileText
            || HasFileTextWrites || HasFileRename || HasTextProcessSpawn || HasTextDirectoryPath;

        internal static bool IsPathRecord(LlvmType type) => type is LlvmStructType { Name: "msync.path" };

        internal string? RawPathLiteral(LlvmGlobalVariable global) =>
            graph.Constants.TryGetValue(global, out var literal) && literal is { Kind: NativeKind.Path, Path: null }
                ? literal.Text : null;

        internal void CheckPathRecord(LlvmType type, SourceSpan? source)
        {
            if (ReferenceEquals(pathRecord, type)) return;
            if (type is not LlvmStructType { Name: "msync.path" } structure)
                throw new MsyncException("Path does not use its physical record", source);
            Leaf[] leaves = Leaves(type);
            ReadOnlySpan<int> offsets = [HpirPathImports.Endpoint, HpirPathImports.IsQualified, HpirPathImports.Raw,
                HpirPathImports.Directory, HpirPathImports.Home, HpirPathImports.Spelling, HpirPathImports.EndpointAddress];
            if (Data.GetTypeAllocSize(type).GetFixedValue() != HpirPathImports.PathBytes
                || Data.GetABITypeAlign(type).Value != HpirPathImports.PathAlignment || leaves.Length != offsets.Length)
                throw new MsyncException("Path layout disagrees with the HPIR call ABI", source);
            for (int index = 0; index < leaves.Length; ++index)
                if (leaves[index].Offset != offsets[index] || (index < 2
                    ? !leaves[index].Type.IsIntegerTy(8) : !IsGuestPointer(leaves[index].Type)))
                    throw new MsyncException("Path field disagrees with the HPIR call ABI", source);
            pathRecord = structure;
        }

        internal HpirPathOp PathOperation(LlvmCallInstruction call, LlvmFunction target)
        {
            if (graph.RuntimeCalls.TryGetValue(target, out var binary))
            {
                if (binary == NativeBinary.Qualify) return HpirPathOp.Qualify;
                if (binary == NativeBinary.PathJoin) return HpirPathOp.Join;
                if (binary is NativeBinary.Equal or NativeBinary.NotEqual && call.ArgumentCount == 2
                    && IsPathRecord(call.GetArgument(0).Type) && IsPathRecord(call.GetArgument(1).Type))
                    return binary == NativeBinary.Equal ? HpirPathOp.Equal : HpirPathOp.NotEqual;
            }
            if (graph.NativeCalls.TryGetValue(target, out var name))
            {
                if (name == "path.qualify") return HpirPathOp.Qualify;
                if (name == "path.join") return HpirPathOp.Join;
                if (name == "environment.working_directory") return HpirPathOp.WorkingDirectory;
                if (name == "environment.set_working_directory") return HpirPathOp.SetWorkingDirectory;
                if (name == "fs.canonical") return HpirPathOp.Canonical;
                if (name == "environment.home_directory") return HpirPathOp.HomeDirectory;
                if (name == "path.identity" && IsPathRecord(call.Type)) return HpirPathOp.FromText;
                if (name.StartsWith("fs.", StringComparison.Ordinal)
                    && HpirPathImports.TryLexical(name[3..], out var lexical)) return lexical;
            }
            if (!graph.RuntimeConversions.TryGetValue(target, out var conversion) || conversion.TargetType < 0
                || conversion.Operation is not (LlvmRuntimeConversionOperation.Convert or LlvmRuntimeConversionOperation.Coerce))
                return HpirPathOp.Invalid;
            var input = call.GetArgument(0);
            bool either = Union(input.Type) == (TypeKind.Path | TypeKind.String);
            return graph.Types[conversion.TargetType].Kind switch
            {
                TypeKind.String when IsPathRecord(input.Type) || either => HpirPathOp.NativeString,
                TypeKind.Path when StringCarrier(input) || either => HpirPathOp.FromText,
                _ => HpirPathOp.Invalid
            };
        }

        internal bool StringCarrier(LlvmValue value) => IsGuestPointer(value.Type)
            && (uint)value.LogicalType < (uint)graph.Types.Count && graph.Types[value.LogicalType].Kind == TypeKind.String;

        internal int LiveTransferImport(bool text, bool observed, bool pending)
        {
            return FutureImport(HpirLiveTransferImports.Create(text, TransferCompletionLayout(observed), pending));
        }

        internal int PathImport(HpirPathOp operation, bool leftText, bool rightText)
        {
            var key = (operation, leftText, rightText);
            if (pathImports.TryGetValue(key, out int imported)) return imported;
            if (HpirPathImports.CanAllocate(operation)) _ = TextLayout();
            imported = Imports.Count;
            Imports.Add(HpirPathImports.Create(operation, leftText, rightText));
            pathImports.Add(key, imported);
            return imported;
        }
    }

    private sealed partial class Body
    {
        private int pathCalls, pathEqualityCalls, pathDirectoryChanges, pathLiterals, pathSpills, pathUnionProjections;

        private bool TryPathLiteral(LlvmLoadInstruction load)
        {
            if (load.PointerOperand is not LlvmGlobalVariable global || geometry.RawPathLiteral(global) is not { } raw)
                return false;
            if (geometry.TrySessionPath(global, out int id))
            {
                LoadPathOccurrence(load, id);
                return true;
            }
            int output = PathRecordAddress(load, load, result: true);
            EmitPathLiteral(load, output, raw);
            return true;
        }

        private void EmitPathLiteral(LlvmInstruction load, int output, string raw)
        {
            int text = Reserve(8, 8);
            Add(new(HpirOp.AddressOfStatic, text, Immediate: geometry.TextLiteral(raw)), load.Source);
            var operation = geometry.ChangesDirectory ? HpirPathOp.FromLiteral : HpirPathOp.FromText;
            EmitCall(load, ~geometry.PathImport(operation, true, false), -1, [output, text], safepoint: false);
            ++pathLiterals;
        }

        private bool PreparePathCell(LlvmGlobalVariable global, LlvmInstruction site)
        {
            if (geometry.RawPathLiteral(global) is not { } raw) return false;
            geometry.CheckPathRecord(global.ValueType, site.Source);
            if (geometry.TrySessionPath(global, out int id))
            {
                operands.Add(global, PathOccurrenceAddress(site, id));
                return true;
            }
            int record = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment), address = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, address, record), site.Source);
            EmitPathLiteral(site, address, raw);
            operands.Add(global, address);
            return true;
        }

        private bool TryPathCall(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            HpirPathOp operation = geometry.PathOperation(call, target);
            if (operation == HpirPathOp.Invalid) return false;
            int count = HpirPathImports.OperandCount(operation);
            if (call.ArgumentCount != count) throw Error("invalid Path import arity", call);
            if (operation == HpirPathOp.Qualify) return EmitQualification(call);
            if (count == 0)
            {
                int address = PathRecordAddress(call, call, result: true);
                EmitCall(call, ~geometry.PathImport(operation, false, false), -1, [address], safepoint: false);
                ++pathCalls;
                return true;
            }
            LlvmValue left = call.GetArgument(0);
            LlvmValue? right = count == 2 ? call.GetArgument(1) : null;
            bool leftUnion = KnownPathUnion(left), rightUnion = right is not null && KnownPathUnion(right);
            bool path = HpirPathImports.ReturnsPath(operation);
            if (leftUnion && operation == HpirPathOp.FromText)
            {
                geometry.CheckPathRecord(call.Type, call.Source);
                NormalizePathUnion(call, left, RecordLocation(call));
                return true;
            }
            if (leftUnion && operation == HpirPathOp.NativeString)
            {
                ProjectPathUnionText(call, left);
                return true;
            }
            bool leftText = !leftUnion && PathTextOperand(left, call);
            bool rightText = right is not null && !rightUnion && PathTextOperand(right, call);
            if (operation == HpirPathOp.FromText && !leftText)
            {
                geometry.CheckPathRecord(call.Type, call.Source);
                int result = RecordLocation(call);
                foreach (var leaf in geometry.Leaves(call.Type))
                    Copy(Width(leaf.Type), checked(result + leaf.Offset), LeafLocation(left, leaf.Offset), call.Source);
                return true;
            }
            var args = new int[count + (path ? 1 : 0)];
            if (path) args[0] = PathRecordAddress(call, call, result: true);
            args[args.Length - count] = PathCallAddress(call, left, leftText, leftUnion);
            if (right is not null) args[^1] = PathCallAddress(call, right, rightText, rightUnion);
            int import = geometry.PathImport(operation, leftText, rightText);
            EmitCall(call, ~import, path ? -1 : Location(call), args, HpirPathImports.CanAllocate(operation));
            ++pathCalls;
            if (operation is HpirPathOp.Equal or HpirPathOp.NotEqual) ++pathEqualityCalls;
            if (operation == HpirPathOp.SetWorkingDirectory) ++pathDirectoryChanges;
            return true;
        }

        private bool KnownPathUnion(LlvmValue value) => geometry.Union(value.Type) == (TypeKind.Path | TypeKind.String);

        private bool PathTextDemand(LlvmCallInstruction call, LlvmValue input) =>
            call.CalledFunction is { } target && call.ArgumentCount == 1 && call.GetArgument(0) == input
            && geometry.PathOperation(call, target) == HpirPathOp.FromText;

        private int PathCallAddress(LlvmCallInstruction site, LlvmValue value, bool text, bool union)
        {
            if (text) return TextCell(value) ? ObserveText(site, value) : Location(value);
            if (!union) return PathRecordAddress(value, site);
            int record = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment);
            return NormalizePathUnion(site, value, record);
        }

        private int NormalizePathUnion(LlvmInstruction site, LlvmValue value, int record)
        {
            int address = Reserve(8, 8), isPath = Reserve(1, 1);
            Add(new(HpirOp.AddressOf, address, record), site.Source);
            CompareTag(site, value, TypeKind.Path, isPath);
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: isPath, B: branch + 1), site.Source);
            CopyUnionPath(site, value, record);
            int jump = code.Count;
            Add(new(HpirOp.Jump), site.Source);
            code[branch] = code[branch] with { D = code.Count };
            EmitCall(site, ~geometry.PathImport(HpirPathOp.FromText, true, false), -1, [address, UnionText(value)], safepoint: false);
            code[jump] = code[jump] with { A = code.Count };
            ++pathUnionProjections;
            return address;
        }

        private void ProjectPathUnionText(LlvmCallInstruction site, LlvmValue value)
        {
            int isPath = Reserve(1, 1), output = Location(site);
            CompareTag(site, value, TypeKind.Path, isPath);
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: isPath, B: branch + 1), site.Source);
            int record = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment);
            int address = Reserve(8, 8);
            CopyUnionPath(site, value, record);
            Add(new(HpirOp.AddressOf, address, record), site.Source);
            EmitCall(site, ~geometry.PathImport(HpirPathOp.NativeString, false, false), output, [address], safepoint: false);
            int jump = code.Count;
            Add(new(HpirOp.Jump), site.Source);
            code[branch] = code[branch] with { D = code.Count };
            Copy(HpirWidth.U64, output, UnionText(value), site.Source);
            code[jump] = code[jump] with { A = code.Count };
            ++pathUnionProjections;
        }

        private int UnionText(LlvmValue value) => LeafLocation(value, geometry.FieldOffset(value.Type, 2));

        private void CopyUnionPath(LlvmInstruction site, LlvmValue value, int record)
        {
            int raw = geometry.FieldOffset(value.Type, 1), roots = geometry.FieldOffset(value.Type, 2);
            Copy(HpirWidth.U16, record + HpirPathImports.Endpoint, LeafLocation(value, raw), site.Source);
            for (int index = 0; index < 5; ++index)
                Copy(HpirWidth.U64, record + HpirPathImports.Raw + index * 8, LeafLocation(value, roots + index * 8), site.Source);
        }

        private bool TryLiveTransfer(LlvmFileOutInst transfer)
        {
            if (transfer.Mode != LlvmFileOutMode.Transfer || !Geometry.IsPathRecord(transfer.Destination.Type))
                return false;
            if (TryLiveSelectionTransfer(transfer) || TryPathBatchTransfer(transfer)) return true;
            if (TryLiveScalarTextTransfer(transfer)) return true;
            if (KnownPathUnion(transfer.Value) && !FutureRoot(transfer.Value))
            {
                TransferPathUnion(transfer);
                return true;
            }
            bool text = !Geometry.IsPathRecord(transfer.Value.Type);
            bool pending = TextCell(transfer.Value);
            if (text && (!geometry.StringCarrier(transfer.Value) || FutureRoot(transfer.Value) && !pending)) return false;
            bool observed = !transfer.UseEmpty;
            int source = text ? Location(transfer.Value) : PathRecordAddress(transfer.Value, transfer);
            int destination = PathRecordAddress(transfer.Destination, transfer);
            if (geometry.MixedTextResult(transfer.Value) || geometry.StoredText(transfer.Value))
                SelectTextCarrier(transfer, transfer.Value, Emit);
            else Emit(pending);
            ++livePathTransfers;
            return true;

            void Emit(bool completion)
            {
                int import = geometry.LiveTransferImport(text, observed, completion);
                EmitCall(transfer, ~import, observed ? Location(transfer) : -1, [source, destination], safepoint: observed);
            }
        }

        private int livePathTransfers, unionPathTransfers;

        private void TransferPathUnion(LlvmFileOutInst transfer)
        {
            LlvmValue value = transfer.Value;
            int destination = PathRecordAddress(transfer.Destination, transfer);
            bool observed = !transfer.UseEmpty;
            int receipt = observed ? Location(transfer) : -1;
            int isPath = Reserve(1, 1);
            CompareTag(transfer, value, TypeKind.Path, isPath);
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: isPath, B: branch + 1), transfer.Source);

            int record = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment);
            CopyUnionPath(transfer, value, record);
            int address = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, address, record), transfer.Source);
            EmitCall(transfer, ~geometry.LiveTransferImport(false, observed, false), receipt,
                [address, destination], safepoint: observed);
            int jump = code.Count;
            Add(new(HpirOp.Jump), transfer.Source);

            code[branch] = code[branch] with { D = code.Count };
            EmitCall(transfer, ~geometry.LiveTransferImport(true, observed, false), receipt,
                [UnionText(value), destination], safepoint: observed);
            code[jump] = code[jump] with { A = code.Count };
            ++livePathTransfers;
            ++unionPathTransfers;
        }

        private void EmitFileTextRead(LlvmFileInInst input)
        {
            bool text = PathTextOperand(input.SourceOperand, input);
            int operand = text ? Location(input.SourceOperand) : PathRecordAddress(input.SourceOperand, input);
            EmitCall(input, ~geometry.FileTextImport(text), Location(input), [operand]);
        }

        private bool PathTextOperand(LlvmValue value, LlvmInstruction origin)
        {
            if (Geometry.IsPathRecord(value.Type)) return false;
            if (geometry.IsGuestPointer(value.Type) && (!FutureRoot(value)
                || TextCell(value) && origin is LlvmCallInstruction call && PathTextDemand(call, value))) return true;
            throw Error("Path operation needs a ready Path or String operand", origin);
        }

        private int PathRecordAddress(LlvmValue value, LlvmInstruction origin, bool result = false)
        {
            geometry.CheckPathRecord(value.Type, origin.Source);
            int storage;
            if (result || value is LlvmArgument or LlvmLoadInstruction or LlvmCallInstruction or LlvmSelectInstruction or LlvmPhiInstruction)
                storage = RecordLocation(value);
            else
            {
                storage = Reserve(HpirPathImports.PathBytes, HpirPathImports.PathAlignment);
                foreach (var leaf in geometry.Leaves(value.Type))
                    Copy(Width(leaf.Type), checked(storage + leaf.Offset), LeafLocation(value, leaf.Offset), origin.Source);
                ++pathSpills;
            }
            int address = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, address, storage), origin.Source);
            return address;
        }

        private void ReportPaths(CompilationTrace? trace)
        {
            trace?.Metric("paths.literal_origins", pathLiterals);
            trace?.Metric("paths.session_occurrence_uses", sessionPathValues);
            trace?.Metric("paths.typed_calls", pathCalls);
            trace?.Metric("paths.equality_calls", pathEqualityCalls);
            trace?.Metric("paths.directory_changes", pathDirectoryChanges);
            trace?.Metric("paths.union_projections", pathUnionProjections);
            trace?.Metric("paths.file_metadata_calls", fileMetadataCalls);
            trace?.Metric("paths.materialized_operands", pathSpills);
            trace?.Metric("paths.live_transfers", livePathTransfers);
            trace?.Metric("paths.transfer_union_sites", unionPathTransfers);
            trace?.Metric("paths.transfer_bool_text", boolFileFormats);
            trace?.Metric("paths.transfer_number_text", numberFileFormats);
        }
    }
}
