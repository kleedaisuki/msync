using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

internal enum LlvmRangeCallbackWork : byte { Ready, Cooperative, ScopedAcquire, Other }

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<LlvmFunction, LlvmRangeCallbackWork> rangeCallbackWork = [];

        private LlvmRangeCallbackWork RangeWork(LlvmFunction callback)
        {
            if (rangeCallbackWork.TryGetValue(callback, out var found)) return found;
            return rangeCallbackWork[callback] = FindRangeWork(callback);
        }

        private LlvmRangeCallbackWork FindRangeWork(LlvmFunction callback)
        {
            HashSet<LlvmFunction> seen = [];
            Stack<LlvmFunction> pending = new();
            pending.Push(callback);
            LlvmRangeCallbackWork effect = LlvmRangeCallbackWork.Ready;
            while (pending.TryPop(out var function))
            {
                if (!seen.Add(function)) continue;
                foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
                {
                    if (instruction is LlvmFileInInst or LlvmFileOutInst) return LlvmRangeCallbackWork.Other;
                    if (instruction is not LlvmCallInstruction call) continue;
                    if (call.CalledFunction is not { } target) return LlvmRangeCallbackWork.Other;
                    if (!target.IsDeclaration) { pending.Push(target); continue; }
                    if (callableCalls.TryGetValue(target, out var callable)
                        && callable.Operation == LlvmCallableRuntimeOperation.Invoke)
                    {
                        if (callable.Targets.Count == 0 || callable.SessionSlot >= 0 && session is null)
                            return LlvmRangeCallbackWork.Other;
                        foreach (var entry in callable.Targets.Values) pending.Push(entry);
                    }
                    LlvmRangeCallbackWork own = DeclarationRangeWork(call, target);
                    if (own == LlvmRangeCallbackWork.Other) return own;
                    if (own > effect) effect = own;
                }
            }
            return effect;
        }

        private LlvmRangeCallbackWork DeclarationRangeWork(LlvmCallInstruction call, LlvmFunction target)
        {
            if (nativeCalls.TryGetValue(target, out var name))
            {
                if (name is "io.read_line" or "process.success" or "process.exit_code" or "process.stdout" or "process.stderr")
                    return LlvmRangeCallbackWork.Cooperative;
                return NativeSignatures.Get(name).RequiresWorkScope ? LlvmRangeCallbackWork.Other : LlvmRangeCallbackWork.Ready;
            }
            if (iteratorCalls.ContainsKey(target)) return LlvmRangeCallbackWork.Other;
            if (!valueOperations.TryGetValue(target, out var binding)) return LlvmRangeCallbackWork.Ready;
            return binding.Operation switch
            {
                LlvmRuntimeValueOperation.ProcessCall => (binding.Mode & 1) != 0
                    ? LlvmRangeCallbackWork.Cooperative : LlvmRangeCallbackWork.Other,
                LlvmRuntimeValueOperation.Acquire when call.ArgumentCount == 0 => LlvmRangeCallbackWork.ScopedAcquire,
                LlvmRuntimeValueOperation.SelectPath or LlvmRuntimeValueOperation.Acquire => LlvmRangeCallbackWork.Other,
                LlvmRuntimeValueOperation.Barrier when binding.Mode != 0 || binding.Symbol is not null => LlvmRangeCallbackWork.Other,
                _ => LlvmRangeCallbackWork.Ready
            };
        }
    }
}
