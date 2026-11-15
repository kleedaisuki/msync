using System.Text;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    /// <summary>
    /// Canonicalizes structurally equivalent types and merges bisimilar functions
    /// to eliminate duplicate specializations across lowering and optimization.
    /// </summary>
    public static NativeProgram FoldIdenticalFunctions(NativeProgram program, CompilationTrace? trace = null)
    {
        if (program.Functions.Count <= 1 || program.PreserveLocals || program.ExecutionPlans is { Count: > 0 })
            return program;

        if (trace?.Detail is not { } detail)
            return FoldIdenticalFunctionsCore(program, null);

        NativeProgram result = program;
        detail.Run("compiler.lower.fold_functions", (program, detail),
            state => result = FoldIdenticalFunctionsCore(state.program, state.detail));
        return result;
    }

    private sealed partial class Lowering
    {
        private NativeProgram FoldIdenticalFunctions(NativeProgram program) =>
            NativeHirLowerer.FoldIdenticalFunctions(program, trace);
    }

    private static NativeProgram FoldIdenticalFunctionsCore(NativeProgram program, CompilationTrace? trace)
    {
        program = CanonicalizeTypes(program, trace);
        int count = program.Functions.Count;
        var allocationsByFunction = IndexAllocations(program, count);
        var dispatchTables = CollectDispatchTables(program);

        // Alpha-normalize local/temp slots first so dead slots or renumbered temps
        // left behind by inlining/DCE don't block structural bisimulation.
        NativeFunctionCode[] canonical = new NativeFunctionCode[count];
        long slotsBefore = 0, slotsAfter = 0, slotsCompactedFunctions = 0;
        for (int i = 0; i < count; i++)
        {
            var fn = program.Functions[i];
            if (fn.IsTemplate || i == program.EntryPoint)
            {
                canonical[i] = fn;
                continue;
            }
            var normalized = CanonicalizeLocalSlots(fn);
            canonical[i] = normalized;
            if (trace is not null)
            {
                slotsBefore += fn.Slots.Count;
                slotsAfter += normalized.Slots.Count;
                if (!ReferenceEquals(normalized, fn)) slotsCompactedFunctions++;
            }
        }
        trace?.Metric("fold.slots.before", slotsBefore);
        trace?.Metric("fold.slots.after", slotsAfter);
        trace?.Metric("fold.slots.compacted_functions", slotsCompactedFunctions);

        // Pre-partition by call-independent structure; singleton groups never refine further.
        int[] group = new int[count];
        Dictionary<string, int> initialIds = new(StringComparer.Ordinal);
        List<int> candidates = [];
        for (int i = 0; i < count; i++)
        {
            var fn = canonical[i];
            if (fn.IsTemplate || i == program.EntryPoint)
            {
                group[i] = i;
                continue;
            }
            string shape = StaticShapeKey(fn, allocationsByFunction[i]);
            if (!initialIds.TryGetValue(shape, out int g))
                initialIds[shape] = g = i;
            group[i] = g;
            candidates.Add(i);
        }

        var active = FilterNonSingletons(candidates, group);
        trace?.Metric("fold.functions.before", count);
        trace?.Metric("fold.candidate_functions", active.Count);

        int iterations = 0;
        if (active.Count > 1)
        {
            int[] nextGroup = (int[])group.Clone();
            Dictionary<string, int> splitIds = new(StringComparer.Ordinal);
            while (true)
            {
                iterations++;
                splitIds.Clear();
                foreach (int fnIndex in active)
                {
                    string key = DynamicKey(canonical[fnIndex], group[fnIndex], group);
                    if (!splitIds.TryGetValue(key, out int representative))
                        splitIds[key] = representative = fnIndex;
                    nextGroup[fnIndex] = representative;
                }

                bool changed = ApplyPartition(active, group, nextGroup);
                if (!changed)
                {
                    // Verify no dispatch table collapses two keys with distinct remapped targets.
                    changed = SplitConflictingDispatchKeys(dispatchTables, group);
                    if (!changed) break;
                }
                active = FilterNonSingletons(active, group);
                if (active.Count <= 1) break;
            }
        }

        int[] denseIndex = new int[count];
        List<NativeFunctionCode> compacted = [];
        for (int i = 0; i < count; i++)
        {
            if (group[i] == i)
            {
                denseIndex[i] = compacted.Count;
                compacted.Add(canonical[i]);
            }
            else
                denseIndex[i] = -1;
        }

        trace?.Metric("fold.iterations", iterations);
        trace?.Metric("fold.functions.after", compacted.Count);
        trace?.Metric("fold.functions.eliminated", count - compacted.Count);
        if (compacted.Count == count) return program;

        int Remap(int fn) => fn < 0 ? fn : denseIndex[group[fn]];
        return RewriteProgramFunctions(program, group, compacted, Remap);
    }

    private static NativeProgram CanonicalizeTypes(NativeProgram program, CompilationTrace? trace)
    {
        int typeCount = program.Types.Count;
        trace?.Metric("fold.types.before", typeCount);
        if (typeCount <= 1)
        {
            trace?.Metric("fold.types.after", typeCount);
            return program;
        }

        int[] group = new int[typeCount];
        Dictionary<string, int> baseIds = new(StringComparer.Ordinal);
        List<int> candidates = [];
        StringBuilder sb = new(32);
        for (int i = 0; i < typeCount; i++)
        {
            var type = program.Types[i];
            sb.Clear();
            sb.Append((ulong)type.Kind).Append('|');
            if (type.Layouts is { Count: > 0 } layouts)
                foreach (int layout in layouts) sb.Append(layout).Append(',');
            string baseKey = sb.ToString();
            if (!baseIds.TryGetValue(baseKey, out int rep))
                baseIds[baseKey] = rep = i;
            group[i] = rep;
            candidates.Add(i);
        }

        var active = FilterNonSingletons(candidates, group);
        if (active.Count > 1)
        {
            int[] nextGroup = (int[])group.Clone();
            Dictionary<(int Group, int Element, int Pointee), int> splitIds = [];
            while (true)
            {
                splitIds.Clear();
                foreach (int t in active)
                {
                    var type = program.Types[t];
                    var key = (group[t],
                        type.Element < 0 ? -1 : group[type.Element],
                        type.Pointee < 0 ? -1 : group[type.Pointee]);
                    if (!splitIds.TryGetValue(key, out int rep))
                        splitIds[key] = rep = t;
                    nextGroup[t] = rep;
                }
                if (!ApplyPartition(active, group, nextGroup)) break;
                active = FilterNonSingletons(active, group);
                if (active.Count <= 1) break;
            }
        }

        int[] denseType = new int[typeCount];
        List<NativeCodeType> compactedTypes = [];
        for (int i = 0; i < typeCount; i++)
        {
            if (group[i] == i)
            {
                denseType[i] = compactedTypes.Count;
                compactedTypes.Add(program.Types[i]);
            }
            else
                denseType[i] = -1;
        }

        trace?.Metric("fold.types.after", compactedTypes.Count);
        if (compactedTypes.Count == typeCount) return program;

        int RemapType(int id) => id < 0 ? id : denseType[group[id]];
        for (int i = 0; i < compactedTypes.Count; i++)
        {
            var t = compactedTypes[i];
            int elem = RemapType(t.Element);
            int pointee = RemapType(t.Pointee);
            if (elem != t.Element || pointee != t.Pointee)
                compactedTypes[i] = t with { Element = elem, Pointee = pointee };
        }

        var layoutsOut = program.Layouts.Select(layout => new NativeCodeLayout(
            layout.Fields.Select(field => field with { Type = RemapType(field.Type) }).ToArray())).ToArray();

        var functionsOut = program.Functions.Select(fn => fn with
        {
            Slots = fn.Slots.Select(slot => slot with { Type = RemapType(slot.Type) }).ToArray(),
            Signature = fn.Signature with
            {
                Parameters = fn.Signature.Parameters.Select(RemapType).ToArray(),
                Result = RemapType(fn.Signature.Result),
                Captures = fn.Signature.Captures.Select(c => c with { Type = RemapType(c.Type) }).ToArray()
            },
            Instructions = fn.Instructions.Select(ins =>
                ins.Type < 0 ? ins : ins with { Type = RemapType(ins.Type) }).ToArray()
        }).ToArray();

        return program with
        {
            Types = compactedTypes.ToArray(),
            Layouts = layoutsOut,
            Functions = functionsOut
        };
    }

    private static bool ApplyPartition(List<int> active, int[] group, int[] nextGroup)
    {
        bool changed = false;
        foreach (int fnIndex in active)
        {
            if (group[fnIndex] != nextGroup[fnIndex])
            {
                changed = true;
                group[fnIndex] = nextGroup[fnIndex];
            }
        }
        return changed;
    }

    private static bool SplitConflictingDispatchKeys(List<IReadOnlyDictionary<int, int>> tables, int[] group)
    {
        bool split = false;
        Dictionary<(int Group, int TargetGroup), int> reps = [];
        foreach (var table in tables)
        {
            reps.Clear();
            foreach (var (key, target) in table)
            {
                if (key < 0) continue;
                int keyGroup = group[key];
                int targetGroup = target < 0 ? target : group[target];
                var bucket = (keyGroup, targetGroup);
                if (!reps.TryGetValue(bucket, out int rep))
                    reps[bucket] = rep = key;
                if (group[key] != rep)
                {
                    group[key] = rep;
                    split = true;
                }
            }
        }
        return split;
    }

    private static List<int> FilterNonSingletons(IReadOnlyList<int> candidates, int[] group)
    {
        Dictionary<int, int> counts = [];
        foreach (int id in candidates)
        {
            int g = group[id];
            counts[g] = counts.GetValueOrDefault(g) + 1;
        }
        List<int> result = [];
        foreach (int id in candidates)
            if (counts[group[id]] > 1)
                result.Add(id);
        return result;
    }

    private static List<NativeAllocationPlan>[] IndexAllocations(NativeProgram program, int count)
    {
        var result = new List<NativeAllocationPlan>[count];
        for (int i = 0; i < count; i++) result[i] = [];
        foreach (var plan in program.Allocations)
            result[plan.Function].Add(plan);
        foreach (var list in result)
            if (list.Count > 1) list.Sort((a, b) => a.Instruction.CompareTo(b.Instruction));
        return result;
    }

    private static List<IReadOnlyDictionary<int, int>> CollectDispatchTables(NativeProgram program)
    {
        List<IReadOnlyDictionary<int, int>> tables = [];
        if (program.SessionDispatches is { } dispatches)
            foreach (var dispatch in dispatches) tables.Add(dispatch.Targets);
        HashSet<string> seenSymbols = new(StringComparer.Ordinal);
        foreach (var fn in program.Functions)
            foreach (var ins in fn.Instructions)
                if (ins.Op is NativeOp.Call or NativeOp.AdaptFunction
                    && ins.Symbol is { } symbol && seenSymbols.Add(symbol))
                    tables.Add(FunctionDispatch.Targets(symbol));
        return tables;
    }

    private static string StaticShapeKey(NativeFunctionCode fn, List<NativeAllocationPlan> allocations)
    {
        StringBuilder sb = new(256);
        sb.Append(fn.Name).Append('|')
          .Append(fn.ParameterCount).Append('|').Append(fn.CaptureCount).Append('|')
          .Append(fn.IteratorCount).Append('|').Append(fn.Signature.Result);

        if (fn.ReflectionSignature is { } refl)
        {
            sb.Append("|R:"); AppendTypeRef(sb, refl.Result);
            foreach (var p in refl.Parameters) { sb.Append(','); AppendTypeRef(sb, p); }
        }
        sb.Append("|P:");
        foreach (int p in fn.Signature.Parameters) sb.Append(p).Append(',');
        sb.Append("|C:");
        foreach (var c in fn.Signature.Captures) sb.Append((byte)c.Mode).Append(':').Append(c.Type).Append(',');
        sb.Append("|S:");
        foreach (var s in fn.Slots)
            sb.Append((byte)s.Role).Append(':').Append(s.Type).Append(':').Append(s.Escapes ? '1' : '0').Append(',');
        sb.Append("|A:");
        foreach (var a in allocations)
            sb.Append(a.Instruction).Append(':').Append(a.Layout).Append(':').Append((byte)a.Placement).Append(',');

        sb.Append("|I:");
        foreach (var ins in fn.Instructions)
        {
            sb.Append((byte)ins.Op).Append(':').Append(ins.Result).Append(':').Append(ins.Type)
              .Append(':').Append(ins.Immediate).Append('[');
            if (ins.Inputs is { } inputs)
                foreach (int input in inputs) sb.Append(input).Append(',');
            sb.Append(']');
            switch (ins.Op)
            {
                case NativeOp.MakeFunction:
                    sb.Append('_').Append(',').Append(ins.B).Append(',').Append(ins.C)
                      .Append(',').Append(ins.Symbol ?? "");
                    break;
                case NativeOp.Call or NativeOp.AdaptFunction:
                    sb.Append(ins.A).Append(",_,").Append(ins.C)
                      .Append(',').Append(ins.Symbol is null ? '0' : '1');
                    break;
                case NativeOp.Allocate:
                    // Non-session execution does not key runtime behavior on allocation-site ids (B).
                    sb.Append(ins.A).Append(",_,").Append(ins.C)
                      .Append(',').Append(ins.Symbol ?? "");
                    break;
                default:
                    sb.Append(ins.A).Append(',').Append(ins.B).Append(',').Append(ins.C)
                      .Append(',').Append(ins.Symbol ?? "");
                    break;
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static NativeFunctionCode CanonicalizeLocalSlots(NativeFunctionCode fn)
    {
        int fixedCount = fn.CaptureCount + fn.ParameterCount;
        if (fn.IsTemplate || fn.Instructions.Count == 0 || fn.Slots.Count <= fixedCount)
            return fn;

        int[] map = new int[fn.Slots.Count];
        Array.Fill(map, -1);
        List<NativeSlot> newSlots = new(fn.Slots.Count);
        for (int i = 0; i < fixedCount; i++)
        {
            map[i] = i;
            newSlots.Add(fn.Slots[i]);
        }

        int MapSlot(int slot)
        {
            if ((uint)slot >= (uint)map.Length) return slot;
            int mapped = map[slot];
            if (mapped >= 0) return mapped;
            mapped = newSlots.Count;
            map[slot] = mapped;
            newSlots.Add(fn.Slots[slot]);
            return mapped;
        }

        var rewritten = new NativeInstruction[fn.Instructions.Count];
        bool changed = false;
        for (int i = 0; i < rewritten.Length; i++)
        {
            NativeInstruction ins = fn.Instructions[i];
            NativeOperands valueOps = NativeInstructionFacts.ValueOperands(ins)
                | (ins.Op is NativeOp.FrameAddress or NativeOp.BindLocation ? NativeOperands.A : 0);

            int a = (valueOps & NativeOperands.A) != 0 && ins.A >= 0 ? MapSlot(ins.A) : ins.A;
            int b = (valueOps & NativeOperands.B) != 0 && ins.B >= 0 ? MapSlot(ins.B) : ins.B;
            int c = (valueOps & NativeOperands.C) != 0 && ins.C >= 0 ? MapSlot(ins.C) : ins.C;
            int[]? inputs = null;
            if (ins.Inputs is { Count: > 0 } origInputs)
            {
                for (int k = 0; k < origInputs.Count; k++)
                {
                    int orig = origInputs[k];
                    int mapped = orig >= 0 ? MapSlot(orig) : orig;
                    if (mapped != orig || inputs is not null)
                    {
                        if (inputs is null)
                        {
                            inputs = new int[origInputs.Count];
                            for (int m = 0; m < k; m++) inputs[m] = origInputs[m];
                        }
                        inputs[k] = mapped;
                    }
                }
            }
            int result = ins.Result >= 0 ? MapSlot(ins.Result) : ins.Result;

            if (a != ins.A || b != ins.B || c != ins.C || result != ins.Result || inputs is not null)
            {
                rewritten[i] = ins with { Result = result, A = a, B = b, C = c, Inputs = inputs ?? ins.Inputs };
                changed = true;
            }
            else
            {
                rewritten[i] = ins;
            }
        }

        if (!changed && newSlots.Count == fn.Slots.Count) return fn;
        NativeStoragePlan? storage = fn.Storage;
        if (storage is not null)
        {
            NativeSlotStorage[] remapped = new NativeSlotStorage[newSlots.Count];
            for (int slot = 0; slot < map.Length; slot++)
                if (map[slot] >= 0) remapped[map[slot]] = storage.Slots[slot];
            storage = storage with { Slots = remapped };
        }
        return fn with { Slots = newSlots.ToArray(), Instructions = rewritten, Storage = storage };
    }

    private static void AppendTypeRef(StringBuilder sb, TypeRef type)
    {
        sb.Append((ulong)type.Kind).Append(type.Nullable ? '?' : '.');
        if (type.Element is { } elem) { sb.Append('<'); AppendTypeRef(sb, elem); sb.Append('>'); }
    }

    private static string DynamicKey(NativeFunctionCode fn, int currentGroup, int[] group)
    {
        StringBuilder sb = new(64);
        sb.Append(currentGroup).Append('|');
        foreach (var ins in fn.Instructions)
        {
            if (ins.Op == NativeOp.MakeFunction)
                sb.Append(ins.A < 0 ? ins.A : group[ins.A]).Append(';');
            else if (ins.Op is NativeOp.Call or NativeOp.AdaptFunction)
            {
                sb.Append(ins.B < 0 ? ins.B : group[ins.B]);
                if (ins.Symbol is { } symbol)
                {
                    sb.Append('(');
                    foreach (var (k, v) in CanonicalDispatchPairs(symbol, t => t < 0 ? t : group[t]))
                        sb.Append(k).Append(':').Append(v).Append(',');
                    sb.Append(')');
                }
                sb.Append(';');
            }
        }
        return sb.ToString();
    }

    private static List<(int Key, int Value)> CanonicalDispatchPairs(string symbol, Func<int, int> remap)
    {
        List<(int Key, int Value)> pairs = [];
        foreach (var (k, v) in FunctionDispatch.Targets(symbol))
            pairs.Add((remap(k), remap(v)));
        pairs.Sort();
        for (int i = pairs.Count - 1; i > 0; i--)
            if (pairs[i] == pairs[i - 1]) pairs.RemoveAt(i);
        return pairs;
    }

    private static NativeProgram RewriteProgramFunctions(NativeProgram program, int[] group,
        List<NativeFunctionCode> compacted, Func<int, int> remap)
    {
        Dictionary<string, string> dispatchCache = new(StringComparer.Ordinal);
        string RemapDispatch(string symbol)
        {
            if (dispatchCache.TryGetValue(symbol, out var cached)) return cached;
            var pairs = CanonicalDispatchPairs(symbol, remap)
                .Select(pair => KeyValuePair.Create(pair.Key, pair.Value));
            return dispatchCache[symbol] = FunctionDispatch.Encode(pairs);
        }

        for (int newIdx = 0; newIdx < compacted.Count; newIdx++)
        {
            var fn = compacted[newIdx];
            bool modified = false;
            NativeInstruction[]? instructions = null;
            for (int i = 0; i < fn.Instructions.Count; i++)
            {
                var ins = fn.Instructions[i];
                NativeInstruction updated = ins.Op switch
                {
                    NativeOp.MakeFunction when ins.A >= 0 => ins with { A = remap(ins.A) },
                    NativeOp.Call or NativeOp.AdaptFunction => ins with
                    {
                        B = ins.B >= 0 ? remap(ins.B) : ins.B,
                        Symbol = ins.Symbol is null ? null : RemapDispatch(ins.Symbol)
                    },
                    _ => ins
                };
                if (!Equals(updated, ins))
                {
                    instructions ??= fn.Instructions.ToArray();
                    instructions[i] = updated;
                    modified = true;
                }
            }
            if (modified) compacted[newIdx] = fn with { Instructions = instructions! };
        }

        var allocations = program.Allocations
            .Where(plan => group[plan.Function] == plan.Function)
            .Select(plan => plan with { Function = remap(plan.Function) })
            .ToArray();

        var dispatches = program.SessionDispatches is null ? null : program.SessionDispatches
            .Select(table =>
            {
                Dictionary<int, int> remapped = [];
                foreach (var (k, v) in table.Targets) remapped[remap(k)] = remap(v);
                return new SessionDispatchTable(table.Slot, remapped);
            }).ToArray();

        return program with
        {
            Functions = compacted.ToArray(),
            EntryPoint = remap(program.EntryPoint),
            Allocations = allocations,
            SessionDispatches = dispatches
        };
    }
}
