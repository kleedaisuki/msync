// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp isSafeLoadOfSelectToSpeculate / select access classification.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class State
    {
        private SelectHandSpeculativity IsSafeLoadOfSelectToSpeculate(LlvmLoadInstruction load,
            LlvmSelectInstruction select, bool preserveCfg)
        {
            Debug.Assert(load.IsSimple);
            SelectHandSpeculativity spec = default;
            foreach (LlvmValue value in new[] { select.TrueValue, select.FalseValue })
            {
                Count("speculation.select.unconditional-load-queries");
                if (LlvmLoads.IsSafeToLoadUnconditionally(value, load.Type, load.Alignment, load, metrics: Metrics))
                    spec = spec.SetAsSpeculatable(value == select.TrueValue);
                else if (preserveCfg) return spec;
            }
            return spec;
        }

        internal RewriteableMemOps? IsSafeSelectToSpeculateOrPredicate(LlvmSelectInstruction select, bool preserveCfg)
        {
            Count("speculation.select.queries");
            RewriteableMemOps operations = [];
            List<LlvmUser> users = [];
            foreach (LlvmUser user in select.Users)
            {
                if (user is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast }) users.AddRange(user.Users);
                else users.Add(user);
            }
            foreach (LlvmUser user in users)
            {
                if (user is LlvmStoreInstruction store)
                {
                    if (store.IsVolatile || preserveCfg) return SelectRetained("store-not-predicatable");
                    operations.Add(new(store));
                    continue;
                }
                if (user is not LlvmLoadInstruction { IsVolatile: false } load) return SelectRetained("non-load-or-volatile-user");
                if (!load.IsSimple)
                {
                    if (preserveCfg) return SelectRetained("atomic-load-needs-predication");
                    operations.Add(new(load));
                    continue;
                }
                SelectHandSpeculativity spec = IsSafeLoadOfSelectToSpeculate(load, select, preserveCfg);
                if (preserveCfg && !spec.AreAllSpeculatable) return SelectRetained("load-needs-cfg-predication");
                operations.Add(new(load, spec));
            }
            Count("speculation.select.eligible");
            return operations;
        }

        private RewriteableMemOps? SelectRetained(string cause)
        {
            if (Metrics is not null) Count("speculation.select.retained." + cause);
            return null;
        }

        private void SpeculateSelectInstLoads(LlvmSelectInstruction select, LlvmLoadInstruction load,
            LlvmTransformBuilder builder)
        {
            Debug.Assert(load.IsSimple);
            builder.SetInsertPoint(load);
            LlvmValue whenTrue = builder.CreateCast(LlvmOpcode.AddrSpaceCast, select.TrueValue, load.PointerOperand.Type,
                load.Name + ".sroa.speculate.cast.true");
            LlvmValue whenFalse = builder.CreateCast(LlvmOpcode.AddrSpaceCast, select.FalseValue, load.PointerOperand.Type,
                load.Name + ".sroa.speculate.cast.false");
            LlvmLoadInstruction trueLoad = builder.CreateAlignedLoad(load.Type, whenTrue, load.Alignment,
                load.Name + ".sroa.speculate.load.true");
            LlvmLoadInstruction falseLoad = builder.CreateAlignedLoad(load.Type, whenFalse, load.Alignment,
                load.Name + ".sroa.speculate.load.false");
            trueLoad.LogicalType = falseLoad.LogicalType = load.LogicalType;
            trueLoad.GetOperandUse(0).LogicalType = falseLoad.GetOperandUse(0).LogicalType = load.GetOperandUse(0).LogicalType;
            Count("loads.speculated", 2);
            LlvmValue value = builder.CreateSelect(select.Condition, trueLoad, falseLoad, load.Name + ".sroa.speculated");
            if (value is LlvmInstruction result) result.LogicalType = load.LogicalType;
            load.ReplaceAllUsesWith(value);
        }

        internal bool RewriteSelectInstMemOps(LlvmSelectInstruction select, RewriteableMemOps operations,
            LlvmTransformBuilder builder, LlvmDomTreeUpdater? updater)
        {
            bool cfgChanged = false;
            foreach (RewriteableMemOp operation in operations)
            {
                LlvmInstruction instruction = operation.Instruction;
                if (operation.Spec.AreAllSpeculatable)
                    SpeculateSelectInstLoads(select, (LlvmLoadInstruction)instruction, builder);
                else
                {
                    Debug.Assert(updater is not null);
                    RewriteMemOpOfSelect(select, instruction, operation.Spec, builder, updater!);
                    cfgChanged = true;
                }
                instruction.EraseFromParent();
            }
            foreach (LlvmUser user in select.Users.ToArray()) ((LlvmCastInstruction)user).EraseFromParent();
            select.EraseFromParent();
            return cfgChanged;
        }
    }
}
