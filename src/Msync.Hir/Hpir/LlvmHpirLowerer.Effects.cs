using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly List<HpirTransferInput> TransferInputs = [];
        internal readonly List<HpirContextCheck> ContextChecks = [];

        internal bool IsEntry(LlvmFunction function) => ReferenceEquals(graph.Entry, function);

        internal int TransferImport(HpirTransferInput input, bool shared)
        {
            int id = Imports.Count;
            int binding = TransferInputs.Count;
            TransferInputs.Add(input);
            Imports.Add(new(shared ? HpirService.SharedTransfer : HpirService.Transfer, binding, HpirWidth.Void, []));
            return id;
        }

        internal int ContextCheckImport(HpirContextCheck check)
        {
            int import = Imports.Count;
            Imports.Add(new(HpirService.ContextCheck, ContextChecks.Count, HpirWidth.U8, []));
            ContextChecks.Add(check);
            return import;
        }

        internal bool IsPersistentSession => graph.SessionGeneration is not null;

        internal bool PathJoin(LlvmFunction target) =>
            graph.RuntimeCalls.TryGetValue(target, out var operation) && operation == NativeBinary.PathJoin;

        internal bool TrySelection(LlvmFunction target, out string? pattern)
        {
            pattern = null;
            if (!graph.ValueOperations.TryGetValue(target, out var binding)
                || binding.Operation != LlvmRuntimeValueOperation.SelectPath) return false;
            pattern = binding.Symbol!;
            return true;
        }

        internal bool TryContextualLiteral(LlvmValue value, out NativeConstant constant)
        {
            if (value is LlvmLoadInstruction { IsSimple: true, PointerOperand: LlvmGlobalVariable global }
                && graph.Constants.TryGetValue(global, out var literal))
            {
                constant = literal;
                return true;
            }
            constant = null!;
            return false;
        }

        internal bool IndependentOfPreparedTransfers(LlvmInstruction instruction)
        {
            if (instruction is LlvmFileInInst) return false;
            if (instruction is not LlvmCallInstruction call) return true;
            if (call.CalledFunction is not { IsDeclaration: true } target) return false;
            if (graph.RuntimeConversions.ContainsKey(target) || graph.RuntimeCalls.ContainsKey(target)
                || target.IntrinsicDescriptor is not null) return true;
            return graph.NativeCalls.TryGetValue(target, out var name) &&
                (HpirScalarImports.TryTextOutput(name, out _) || NativeSignatures.TryGet(name, out var signature)
                    && signature.Effect == NativeEffect.Pure
                    && (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates)) == 0);
        }

        internal bool SharesPreparedFileFrontier(LlvmInstruction instruction) => instruction switch
        {
            LlvmFileInInst { Format: LlvmFileInFormat.Text or LlvmFileInFormat.Lines } => true,
            LlvmCallInstruction { CalledFunction: { } target } => FileMetadata(target) != HpirFileMetadata.Invalid
                || IsFileRename(target) || IsDirectoryCreation(target)
                || WorkBinding(target)?.Operation is LlvmRuntimeValueOperation.Barrier or LlvmRuntimeValueOperation.Acquire,
            _ => false
        };
    }

    private sealed partial class Body
    {
        private HashSet<LlvmInstruction>? effectOperands;
        private Dictionary<LlvmFileOutInst, int>? effectImports;
        private Dictionary<LlvmCallInstruction, int>? contextCheckImports;
        private HashSet<LlvmInstruction>? contextCheckOperands;

        private void PrepareEffectInputs(CompilationTrace? trace)
        {
            if (!geometry.IsEntry(function) || function.BasicBlocks.Count != 1) return;
            var block = function.BasicBlocks[0];
            if (block.LastInstruction is not LlvmReturnInstruction) return;
            List<(LlvmFileOutInst Site, HpirTransferInput Input)> transfers = [];
            var operands = new HashSet<LlvmInstruction>();
            var recipe = new HashSet<LlvmInstruction>();
            bool sharedFrontier = false;
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not LlvmFileOutInst transfer) continue;
                recipe.Clear();
                if (transfer.Mode == LlvmFileOutMode.Transfer && transfer.UseEmpty
                    && TryTransfer(transfer, recipe, out var input))
                {
                    operands.UnionWith(recipe);
                    transfers.Add((transfer, input));
                    continue;
                }
                if (transfer.Mode == LlvmFileOutMode.Transfer && Geometry.IsPathRecord(transfer.Destination.Type)
                    || geometry.CanWriteFileText(transfer))
                {
                    // Selection owns the residual scalar arm; do not elide
                    // any operand from its rejected binding recipe.
                    sharedFrontier = true;
                    continue;
                }
                trace?.Metric("effects.transfer.rejected_operands", 1);
                return;
            }
            if (transfers.Count == 0) return;

            // The region is about resource effects, not whether the function
            // performs other computation or keeps using its operand values.
            // Keep opaque resource users live until their frontier is shared.
            foreach (var instruction in block.Instructions)
            {
                if (instruction is LlvmFileOutInst || operands.Contains(instruction)
                    || geometry.IndependentOfPreparedTransfers(instruction)) continue;
                if (geometry.SharesPreparedFileFrontier(instruction))
                {
                    sharedFrontier = true;
                    continue;
                }
                trace?.Metric("effects.transfer.rejected_frontier", 1);
                return;
            }
            if (sharedFrontier && transfers.Any(transfer => transfer.Input.Kind == HpirTransferSource.Selection))
            {
                trace?.Metric("effects.transfer.rejected_mixed_selection", 1);
                return;
            }

            effectImports = new(transfers.Count);
            foreach (var (site, input) in transfers)
                effectImports.Add(site, geometry.TransferImport(input, sharedFrontier));
            foreach (var operand in operands) geometry.NotePreparedPath(operand);
            int shared = RetainSharedEffectOperands(operands, effectImports);
            effectOperands = operands;
            trace?.Metric("effects.transfer.sites", transfers.Count);
            trace?.Metric("effects.transfer.shared_frontier", sharedFrontier ? transfers.Count : 0);
            trace?.Metric("effects.transfer.paths", transfers.Count(transfer => transfer.Input.Kind == HpirTransferSource.Path));
            trace?.Metric("effects.transfer.text", transfers.Count(transfer => transfer.Input.Kind == HpirTransferSource.Text));
            trace?.Metric("effects.transfer.selections", transfers.Count(transfer => transfer.Input.Kind == HpirTransferSource.Selection));
            trace?.Metric("effects.sliced_operands", operands.Count);
            trace?.Metric("effects.shared_operands_retained", shared);
        }

        private static int RetainSharedEffectOperands(HashSet<LlvmInstruction> operands,
            IReadOnlyDictionary<LlvmFileOutInst, int> selected) =>
            RetainOrdinaryUsers(operands, user => user is LlvmFileOutInst transfer && selected.ContainsKey(transfer));

        private static int RetainOrdinaryUsers(HashSet<LlvmInstruction> operands, Func<LlvmUser, bool> selected)
        {
            Queue<LlvmInstruction>? shared = null;
            foreach (var producer in operands)
                if (producer.Users.Any(user => !selected(user)
                        && (user is not LlvmInstruction consumer || !operands.Contains(consumer))))
                    (shared ??= new()).Enqueue(producer);
            if (shared is null) return 0;
            int retained = 0;
            while (shared.TryDequeue(out var producer))
            {
                if (!operands.Remove(producer)) continue;
                ++retained;
                for (int index = 0; index < producer.OperandCount; index++)
                    if (producer.GetOperand(index) is LlvmInstruction input && operands.Contains(input)) shared.Enqueue(input);
            }
            return retained;
        }

        private bool TryEmitFileOut(LlvmInstruction instruction)
        {
            if (instruction is LlvmFileOutInst transfer && effectImports?.TryGetValue(transfer, out int import) == true)
            {
                EmitCall(instruction, ~import, -1, [], safepoint: false);
                return true;
            }
            return IsSlicedEffectOperand(instruction);
        }

        private bool IsSlicedEffectOperand(LlvmInstruction instruction) =>
            effectOperands?.Contains(instruction) == true || contextCheckOperands?.Contains(instruction) == true;

        private bool TryTransfer(LlvmFileOutInst transfer, HashSet<LlvmInstruction> operands, out HpirTransferInput input)
        {
            input = null!;
            if (!TryLocalPath(transfer.Destination, operands, out var destination)) return false;
            if (transfer.Value is LlvmCallInstruction { CalledFunction: { } select, ArgumentCount: 1 } call
                && geometry.TrySelection(select, out string? pattern)
                && TryLocalPath(call.GetArgument(0), operands, out var directory))
            {
                operands.Add(call);
                input = new(HpirTransferSource.Selection, Local(directory), null, Local(destination), pattern, transfer.Source);
                return true;
            }
            if (geometry.TryContextualLiteral(transfer.Value, out var literal) && literal.Kind == NativeKind.String)
            {
                operands.Add((LlvmInstruction)transfer.Value);
                input = new(HpirTransferSource.Text, null, literal.Text!, Local(destination), null, transfer.Source);
                return true;
            }
            if (!TryLocalPath(transfer.Value, operands, out var path)) return false;
            input = new(HpirTransferSource.Path, Local(path), null, Local(destination), null, transfer.Source);
            return true;
        }

        private bool TryLocalPath(LlvmValue value, HashSet<LlvmInstruction> operands, out NativePath path)
        {
            if (geometry.TryContextualLiteral(value, out var literal)
                && literal.Kind == NativeKind.Path && literal.Path is null && literal.Text is { } raw
                && !geometry.RetainedPathOrigin(literal))
            {
                path = new(raw);
                operands.Add((LlvmInstruction)value);
                return true;
            }

            if (value is LlvmCallInstruction { CalledFunction: { } join, ArgumentCount: 2 } call
                && geometry.PathJoin(join)
                && TryLocalPath(call.GetArgument(0), operands, out var left)
                && TryLocalPath(call.GetArgument(1), operands, out var right))
            {
                path = PathSemantics.JoinValue(left, right, windowsHost: true);
                operands.Add(call);
                return true;
            }

            path = default;
            return false;
        }

        private static HpirLocalPath Local(NativePath path) => new(path.Raw, path.LocalSpelling);
    }
}
