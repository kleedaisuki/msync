// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/Mem2Reg.cpp and PromoteMemoryToRegister.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmPromoteMemoryToRegister
{
    public static bool IsAllocaPromotable(LlvmAllocaInstruction alloca) => Promotability(alloca) is null;
    private static string? Promotability(LlvmAllocaInstruction alloca)
    {
        LlvmType? expected = null;
        foreach (var user in alloca.Users)
        {
            LlvmType type;
            if (user is LlvmLoadInstruction load)
            {
                if (load.IsVolatile) return "volatile_load";
                type = load.Type;
            }
            else if (user is LlvmStoreInstruction store)
            {
                if (ReferenceEquals(store.ValueOperand, alloca)) return "stored_address";
                if (store.IsVolatile) return "volatile_store";
                type = store.ValueOperand.Type;
            }
            else if (user is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } intrinsic &&
                (descriptor.IsLifetimeStartOrEnd || intrinsic.IsDroppable() || descriptor.IsFakeUse)) continue;
            else return "non_load_store_user";
            if (expected is null) expected = type;
            else if (!ReferenceEquals(type, expected)) return "mixed_access_types";
        }
        return null;
    }

    public static bool Run(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace = null)
        => Run(function, tree, trace, null);
    public static bool Run(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace,
        LlvmAssumptionCache? assumptions)
    {
        trace = trace?.Detail;
        var wrapper = new Wrapper(function, tree, trace, assumptions);
        if (trace is null) return wrapper.Run();
        return trace.MeasureDetailed("optimizer.pass.llvm-mem2reg", wrapper, static state => state.Run(),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => wrapper.Metrics!);
    }
    public static void PromoteMemToReg(IReadOnlyList<LlvmAllocaInstruction> allocas, LlvmDominatorTree tree,
        CompilationTrace? trace = null)
        => PromoteMemToReg(allocas, tree, trace, null);
    public static void PromoteMemToReg(IReadOnlyList<LlvmAllocaInstruction> allocas, LlvmDominatorTree tree,
        CompilationTrace? trace, LlvmAssumptionCache? assumptions)
    {
        if (allocas.Count == 0) return;
        trace = trace?.Detail;
        if (trace is null) { new Promotion(allocas, tree, null, null, assumptions).Run(); return; }
        var metrics = new Dictionary<string, ulong>();
        var promotion = new Promotion(allocas, tree, trace, metrics, assumptions);
        trace.MeasureDetailed("optimizer.llvm-mem2reg.promote", promotion, static state => { state.Run(); return 0; },
            attributes: new Dictionary<string, string> { ["function.name"] = allocas[0].Parent!.Parent!.Name }, metrics: _ => metrics);

    }
    private sealed class Wrapper(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace, LlvmAssumptionCache? assumptions)
    {
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        internal bool Run()
        {
            bool changed = false;
            var allocas = new List<LlvmAllocaInstruction>();
            while (true)
            {
                allocas.Clear();
                if (trace is null) Scan(allocas);
                else trace.Run("optimizer.llvm-mem2reg.entry-scan", (Self: this, Allocas: allocas), static state => state.Self.Scan(state.Allocas));
                if (allocas.Count == 0) return changed;
                Count(Metrics, "wrapper.iterations");
                new Promotion(allocas, tree, trace, Metrics, assumptions).Run();
                changed = true;
            }
        }
        private void Scan(List<LlvmAllocaInstruction> allocas)
        {
            for (var instruction = function.BasicBlocks[0].FirstInstruction; instruction is { IsTerminator: false }; instruction = instruction.Next)
            {
                Count(Metrics, "scan.instructions");
                if (instruction is not LlvmAllocaInstruction alloca) continue;
                string? rejection = Promotability(alloca);
                if (rejection is null) { allocas.Add(alloca); Count(Metrics, "scan.promotable"); }
                else if (Metrics is not null) Count(Metrics, "retained." + rejection);
            }
        }

    }
    private static void Count(Dictionary<string, ulong>? metrics, string key, ulong amount = 1)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + amount; }
}
