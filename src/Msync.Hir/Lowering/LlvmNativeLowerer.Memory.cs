using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private int literalAddressSelects, literalSelectedLoads;

        private void Alloca(LlvmAllocaInstruction allocation)
        {
            if (allocation.ArraySize is not LlvmConstantInt { IsOne: true })
                throw Unsupported(allocation, "non-scalar alloca");
            int pointee = AllocaStorageType(allocation);
            if (allocation.LogicalType < 0 && !values.ContainsKey(allocation))
                SetSelectedValueType(allocation, owner.Pointer(pointee));
            int local = Register(pointee, IsSessionEntry ? NativeSlotRole.Temporary : NativeSlotRole.Local, allocation.Name);
            Add(new(NativeOp.FrameAddress, Slot(allocation), local, Source: allocation.Source));
        }
        private void Heap(LlvmHeapAllocInst allocation)
        {
            if (allocation.LogicalLayout < 0) throw Unsupported(allocation, "HeapAlloc without producer layout");
            allocations.Add((code.Count, allocation.LogicalLayout));
            Add(new(NativeOp.Allocate, Slot(allocation), allocation.LogicalLayout, AllocationSite(allocation), Source: allocation.Source));
        }
        private void FieldAddress(LlvmGetElementPtrInstruction address)
        {
            if (address.PointerOperand is LlvmGlobalVariable global && owner.SessionCells.TryGetValue(global, out var cell)
                && address.HasAllZeroIndices)
            {
                if (!values.ContainsKey(address)) values.Add(address,
                    Register(address.LogicalType >= 0 ? address.LogicalType : owner.Pointer(cell.Type)));
                if (SessionFieldAddress(address)) return;
            }
            if (StructuredFieldAddress(address)) return;
            CompilePointerRelocation(address);
        }
        private void Load(LlvmLoadInstruction load)
        {
            if (StaticLoad(load)) return;
            if (FunctionComponentLoad(load)) return;
            if (load.PointerOperand is LlvmSelectInstruction select &&
                TryLiteralAddressSelect(select, out var literal))
            {
                int result = load.LogicalType >= 0 ? load.LogicalType : literal.Type;
                if (load.LogicalType < 0) SetSelectedValueType(load, result);
                Add(new(NativeOp.Select, Slot(load), OperandSlot(select, 0),
                    ConstantSlotValue(literal.True, result), ConstantSlotValue(literal.False, result), Source: load.Source));
                trace?.Metric("addresses.literal_loads_selected", ++literalSelectedLoads);
                return;
            }
            if (RelocatedConstantLoad(load, out var constant))
            {
                Add(new(NativeOp.Constant, Slot(load), owner.Constant(constant), Source: load.Source));
                return;
            }
            int storageType = MemoryStorageType(load.GetOperandUse(0), load);
            int resultType = LoadResultType(load, storageType);
            if (!NativeTypeRelations.SameStorageRepresentation(owner.Types, storageType, resultType))
                throw Unsupported(load, $"load {load.Type} in '{function.Name}' from {load.PointerOperand.Name} ({load.PointerOperand.GetType().Name}): " +
                    $"storage #{storageType} {owner.Types[storageType]}, result #{resultType} {owner.Types[resultType]}");
            Add(new(NativeOp.Load, Slot(load), MemoryAddressSlot(load, 0, storageType), Type: storageType, Source: load.Source));
        }

        private readonly record struct LiteralAddressSelect(NativeConstant True, NativeConstant False, int Type);

        // SimplifyCFG can sink an entire literal load through a branch. Literal
        // globals encode source values, not addressable Native storage.
        private bool TryLiteralAddressSelect(LlvmSelectInstruction select, out LiteralAddressSelect literal)
        {
            literal = default;
            if (select.TrueValue is not LlvmGlobalVariable { IsConstant: true, LogicalType: >= 0 } whenTrue ||
                select.FalseValue is not LlvmGlobalVariable { IsConstant: true, LogicalType: >= 0 } whenFalse ||
                !ReferenceEquals(whenTrue.ValueType, whenFalse.ValueType) ||
                !owner.Input.Constants.TryGetValue(whenTrue, out var trueValue) ||
                !owner.Input.Constants.TryGetValue(whenFalse, out var falseValue)) return false;
            int type = owner.Types[whenTrue.LogicalType].Pointee;
            int other = owner.Types[whenFalse.LogicalType].Pointee;
            if (type < 0 || other < 0 || !NativeTypeRelations.SameStorageRepresentation(owner.Types, type, other)) return false;
            foreach (var use in select.Uses)
            {
                if (use.User is not LlvmLoadInstruction { IsSimple: true } load ||
                    !ReferenceEquals(load.PointerOperand, select) || !ReferenceEquals(load.Type, whenTrue.ValueType) ||
                    load.LogicalType >= 0 && !NativeTypeRelations.SameStorageRepresentation(owner.Types, type, load.LogicalType))
                    return false;
            }
            literal = new(trueValue, falseValue, type);
            return true;
        }
        private void FileIn(LlvmFileInInst input)
        {
            string name = input.Format switch
            {
                LlvmFileInFormat.Text => "io.read_text",
                LlvmFileInFormat.Lines => "io.read_lines",
                LlvmFileInFormat.Bytes => "io.read_bytes",
                _ => throw Unsupported(input, "file input format " + input.Format)
            };
            Add(new(NativeOp.CallNative, Slot(input), Symbol: name, Inputs: [OperandSlot(input, 0)], Source: input.Source));
        }
        private void FileOut(LlvmFileOutInst output)
        {
            if (output.Mode == LlvmFileOutMode.Transfer)
            {
                Add(new(NativeOp.Transfer, Slot(output), OperandSlot(output, 0), OperandSlot(output, 1), Source: output.Source));
                return;
            }
            string name = output.Mode switch
            {
                LlvmFileOutMode.WriteText => "io.write_text",
                LlvmFileOutMode.AppendText => "io.append_text",
                LlvmFileOutMode.WriteBytes => "io.write_bytes",
                LlvmFileOutMode.AppendBytes => "io.append_bytes",
                _ => throw Unsupported(output, "file output mode " + output.Mode)
            };
            Add(new(NativeOp.CallNative, Slot(output), Symbol: name,
                Inputs: [OperandSlot(output, 1), OperandSlot(output, 0)], Source: output.Source));
        }
        private void Call(LlvmCallInstruction call)
        {
            if (Intrinsic(call)) return;
            if (call.CalledFunction is { } conversion && RuntimeConversionCall(call, conversion)) return;
            if (call.CalledFunction is { } builtin && RuntimeBuiltinCall(call, builtin)) return;
            if (call.CalledFunction is { } callable && owner.Input.CallableCalls.TryGetValue(callable, out var binding))
            {
                CallableCall(call, binding);
                return;
            }
            if (call.CalledFunction is { } iteratorTarget && owner.Input.IteratorCalls.TryGetValue(iteratorTarget, out var iteratorOperation))
            {
                IteratorCall(call, iteratorOperation);
                return;
            }
            if (call.CalledFunction is { } sourceTarget && owner.Input.SourceFunctions.TryGetValue(sourceTarget, out var sourceInfo))
            {
                SourceCall(call, sourceTarget, sourceInfo);
                return;
            }
            if (call.CalledFunction is { } operationTarget && RuntimeOperationCall(call, operationTarget)) return;
            if (call.CalledFunction is { } binaryTarget && RuntimeBinaryCall(call, binaryTarget)) return;
            throw Unsupported(call, "call target ABI");
        }
        private void SourceCall(LlvmCallInstruction call, LlvmFunction target, LlvmSourceFunctionInfo source)
        {
            var bundle = call.GetOperandBundle("msync.callee")
                ?? throw Unsupported(call, "source call without evaluated callee descriptor");
            var arguments = ArgumentSlots(call, source.CaptureCount);
            int callee = Slot(bundle.GetInputUse(0));
            int result = Slot(call);
            calls.Add(new(code.Count, false, new bool[arguments.Length]));
            ++directCallCount;
            Add(new(NativeOp.Call, result, callee, owner.Functions[target],
                Inputs: arguments, Source: call.Source));
        }

        private void CallableCall(LlvmCallInstruction call, LlvmCallableRuntimeBinding binding)
        {
            string? dispatch = binding.Targets.Count == 0 ? null : FunctionDispatch.Encode(binding.Targets.Select(pair =>
                KeyValuePair.Create(owner.Functions[pair.Key], owner.Functions[pair.Value])));
            int callee = ArgumentSlot(call, 0);
            if (binding.Operation == LlvmCallableRuntimeOperation.Adapt)
            {
                ++adaptationCount;
                Add(new(NativeOp.AdaptFunction, Slot(call), callee, C: binding.SessionSlot, Symbol: dispatch, Source: call.Source));
                return;
            }
            var arguments = ArgumentSlots(call, 1);
            int result = Slot(call);
            calls.Add(new(code.Count, false, new bool[arguments.Length]));
            ++dynamicCallCount;
            Add(new(NativeOp.Call, result, callee, C: binding.SessionSlot, Symbol: dispatch, Inputs: arguments, Source: call.Source));
        }

    }
}
