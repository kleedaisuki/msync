using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeLocalOptimization(NativeProgram program, NativeFunctionCode function,
    NativeInstruction[] code, NativeConstantPool constants, NativeEscapeAnalysis escape)
{
    private readonly record struct Address(int Allocation, int Slot, int Field, int Layout);
    private sealed record Memory(int Source, NativeConstant? Constant, int Type, int Store);
    private readonly Dictionary<int, int> aliases = [];
    private readonly Dictionary<int, NativeConstant> known = [];
    private readonly Dictionary<int, Address> addresses = [];
    private readonly Dictionary<int, Address> bindings = [];
    private readonly Dictionary<Address, Memory> memory = [];
    private readonly HashSet<int> bound = code.Where(item => item.Op == NativeOp.BindLocation).Select(item => item.A).ToHashSet();
    private readonly HashSet<int> addressed = code.Where(item => item.Op == NativeOp.FrameAddress).Select(item => item.A)
        .Concat(code.Where(item => item.Op == NativeOp.BindLocation).Select(item => item.A)).ToHashSet();
    private readonly bool[] singleAssignment = NativeDefinitionFacts.SingleAssignmentTemporaries(function);
    private bool[] cyclic = [];
    private long freshDefinitions;
    private NativeInstruction? previous;
    private NativeFrameCells frameCells = null!;
    private readonly HashSet<int> initialized = [];
    private int[] stableParameters = [];
    private int revisions;
    private HashSet<int> unusedBindings = [];
    private readonly List<int> redundantNarrows = [];
    private int[] provenTypes = [];
    private bool[] provenNonNull = [];
    private int[] slotOrigins = [];
    private readonly Dictionary<(int Source, int Type), int> availableNarrows = [];
    private readonly Dictionary<int, int> nullChecks = [];
    private int eliminatedNarrows;
    private int eliminatedConverts;

    internal static NativeFunctionCode TightenSlotTypes(NativeProgram program, NativeFunctionCode function,
        NativeInstruction[] code, NativeEscapeAnalysis escape, NativeOptimizationProfile? profile = null)
    {
        int slotCount = function.Slots.Count;
        if (slotCount == 0 || code.Length == 0) return function;
        bool[] blocked = new bool[slotCount];
        for (int s = 0; s < slotCount; s++)
        {
            NativeSlot slot = function.Slots[s];
            NativeCodeType codeType = program.Types[slot.Type];
            if (slot.Role is NativeSlotRole.Capture or NativeSlotRole.Parameter
                || slot.Escapes || escape.EscapingLocations.Contains(s)
                || (program.PreserveLocals && slot.Role != NativeSlotRole.Temporary)
                || program.SessionRoots?.Contains(s) == true
                || codeType.Pointee >= 0 || codeType.Element >= 0 || codeType.Kind == TypeKind.Never)
                blocked[s] = true;
        }
        foreach (NativeInstruction ins in code)
        {
            if (ins.Op is NativeOp.FrameAddress or NativeOp.BindLocation) blocked[ins.A] = true;
            if (ins.Op == NativeOp.NextIterator && ins.Result >= 0) blocked[ins.Result] = true;
            foreach (int def in NativeInstructionFacts.Definitions(ins))
                if (def != ins.Result || ins.Op is not (NativeOp.Copy or NativeOp.Move))
                    blocked[def] = true;
        }
        NativeSlot[]? slots = null;
        int tightened = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool roundChanged = false;
            int[] writeType = new int[slotCount];
            Array.Fill(writeType, -1);
            foreach (NativeInstruction ins in code)
            {
                if (ins.Op is not (NativeOp.Copy or NativeOp.Move) || ins.Result < 0 || blocked[ins.Result]) continue;
                int rhs = (slots ?? (IReadOnlyList<NativeSlot>)function.Slots)[ins.A].Type;
                int existing = writeType[ins.Result];
                if (existing == -1) writeType[ins.Result] = rhs;
                else if (existing != rhs && existing != -2)
                {
                    if (NativeTypeRelations.Accepts(program.Types, existing, rhs)) { }
                    else if (NativeTypeRelations.Accepts(program.Types, rhs, existing)) writeType[ins.Result] = rhs;
                    else writeType[ins.Result] = -2;
                }
            }
            for (int s = 0; s < slotCount; s++)
            {
                int target = writeType[s];
                if (blocked[s] || target < 0) continue;
                int oldType = (slots ?? (IReadOnlyList<NativeSlot>)function.Slots)[s].Type;
                if (target == oldType) continue;
                NativeCodeType nt = program.Types[target];
                if (nt.Pointee >= 0 || nt.Element >= 0 || nt.Kind == TypeKind.Never
                    || !NativeTypeRelations.Accepts(program.Types, oldType, target)) continue;
                bool narrowSafe = true;
                foreach (NativeInstruction ins in code)
                {
                    if (ins.Op == NativeOp.Narrow && ins.A == s
                        && !NativeTypeRelations.Accepts(program.Types, target, ins.Type))
                    { narrowSafe = false; break; }
                }
                if (!narrowSafe) continue;
                slots ??= [.. function.Slots];
                slots[s] = slots[s] with { Type = target };
                tightened++;
                roundChanged = true;
            }
            if (!roundChanged) break;
        }
        if (tightened > 0) profile?.Trace.Metric("local_opt.tightened_slots", tightened);
        return slots is null ? function : function with { Slots = slots };
    }

    internal bool Run(NativeControlFlow flow, NativeOptimizationProfile? profile = null)
    {
        cyclic = flow.Cyclic;
        frameCells = new(program, function, escape);
        profile?.Trace.Metric("local_opt.frame_cells_known", frameCells.Known ? 1 : 0);
        redundantNarrows.Clear();
        HashSet<int> used = code.SelectMany(instruction => NativeInstructionFacts.Reads(instruction)
            .Concat(instruction.Op == NativeOp.BindLocation ? [] : NativeInstructionFacts.Definitions(instruction))).ToHashSet();
        unusedBindings = bound.Where(slot => !used.Contains(slot) && !function.Slots[slot].Escapes
            && function.Slots[slot].Role != NativeSlotRole.Capture
            && !escape.EscapingLocations.Contains(slot)
            && !(program.PreserveLocals && function.Slots[slot].Role != NativeSlotRole.Temporary)
            && program.SessionRoots?.Contains(slot) != true).ToHashSet();
        HashSet<int> consumed = code.Where(instruction => instruction.Op is NativeOp.Move or NativeOp.Drop)
            .Select(instruction => instruction.A).ToHashSet();
        stableParameters = Enumerable.Range(function.CaptureCount, function.ParameterCount)
            .Where(slot => !consumed.Contains(slot)).ToArray();
        int slotCount = function.Slots.Count;
        provenTypes = new int[slotCount];
        provenNonNull = new bool[slotCount];
        slotOrigins = new int[slotCount];
        (bool[]?[]? entryInitialized, int[]?[]? entryTypes, bool[]?[]? entryNonNull, Dictionary<(int Source, int Type), int>[]? entryNarrows) =
            flow.Blocks.Count > 1 ? ComputeEntryFacts(flow, true) : (null, null, null, null);
        bool changed = false;
        eliminatedNarrows = 0;
        eliminatedConverts = 0;
        for (int blockIndex = 0; blockIndex < flow.Blocks.Count; blockIndex++)
        {
            NativeControlFlow.Block block = flow.Blocks[blockIndex];
            Clear();
            if (entryInitialized is not null && entryInitialized[blockIndex] is { } liveInit)
                for (int slot = 0; slot < liveInit.Length; slot++)
                    if (liveInit[slot]) initialized.Add(slot);
            if (entryTypes is not null && entryTypes[blockIndex] is { } liveTypes)
                Array.Copy(liveTypes, provenTypes, slotCount);
            if (entryNonNull is not null && entryNonNull[blockIndex] is { } liveNonNull)
                Array.Copy(liveNonNull, provenNonNull, slotCount);
            if (entryNarrows is not null && entryNarrows[blockIndex] is { } liveNarrows)
                foreach (var pair in liveNarrows)
                    if (initialized.Contains(pair.Value))
                        availableNarrows[pair.Key] = pair.Value;
            for (int pc = block.Start; pc < block.End; pc++) changed |= Step(pc);
        }
        changed |= RemoveUnusedNarrows(profile);
        if (eliminatedNarrows > 0) profile?.Trace.Metric("local_opt.eliminated_narrows", eliminatedNarrows);
        if (eliminatedConverts > 0) profile?.Trace.Metric("local_opt.eliminated_converts", eliminatedConverts);
        profile?.Trace.Metric("local_opt.fresh_definitions", freshDefinitions);
        return changed;
    }

    private bool RemoveUnusedNarrows(NativeOptimizationProfile? profile)
    {
        if (redundantNarrows.Count == 0) return false;
        // Cross-block reuse can introduce reads of a previously unused result.
        // Keep its definition until every block has consumed the entry facts.
        HashSet<int> reads = code.SelectMany(NativeInstructionFacts.Reads).ToHashSet();
        int removed = 0;
        foreach (int pc in redundantNarrows)
        {
            NativeInstruction instruction = code[pc];
            if (reads.Contains(instruction.Result)) continue;
            code[pc] = Nop(instruction);
            removed++;
        }
        eliminatedNarrows += removed;
        profile?.Trace.Metric("local_opt.redundant_narrow_candidates", redundantNarrows.Count);
        profile?.Trace.Metric("local_opt.redundant_narrows_removed", removed);
        profile?.Trace.Metric("local_opt.redundant_narrows_retained", redundantNarrows.Count - removed);
        return removed > 0;
    }

    private void ResetProvenTypes()
    {
        for (int s = 0; s < function.Slots.Count; s++)
        {
            int type = function.Slots[s].Type;
            provenTypes[s] = type;
            NativeCodeType ct = program.Types[type];
            provenNonNull[s] = ct.Pointee < 0 && ct.Kind != TypeKind.Never && (ct.Kind & TypeKind.Null) == 0;
            slotOrigins[s] = -1;
        }
    }

    private (bool[]?[]? Init, int[]?[]? Types, bool[]?[]? NonNull, Dictionary<(int Source, int Type), int>[]? Narrows) ComputeEntryFacts(
        NativeControlFlow flow, bool trackInit)
    {
        int slotCount = function.Slots.Count;
        int blockCount = flow.Blocks.Count;
        if (slotCount == 0 || blockCount <= 1) return (null, null, null, null);
        int[] blockOfPc = new int[code.Length];
        List<int>[] predecessors = Enumerable.Range(0, blockCount).Select(_ => new List<int>()).ToArray();
        for (int b = 0; b < blockCount; b++)
        {
            NativeControlFlow.Block blk = flow.Blocks[b];
            for (int pc = blk.Start; pc < blk.End; pc++) blockOfPc[pc] = b;
            foreach (int succ in blk.Successors) predecessors[succ].Add(b);
        }
        bool[]?[]? entryInit = trackInit ? new bool[blockCount][] : null;
        bool[]?[]? exitInit = trackInit ? new bool[blockCount][] : null;
        int[]?[] entryTypes = new int[blockCount][];
        int[]?[] exitTypes = new int[blockCount][];
        bool[]?[] entryNonNull = new bool[blockCount][];
        bool[]?[] exitNonNull = new bool[blockCount][];
        Dictionary<(int Source, int Type), int>[] entryNarrows = new Dictionary<(int, int), int>[blockCount];
        Dictionary<(int Source, int Type), int>[] exitNarrows = new Dictionary<(int, int), int>[blockCount];
        int[] falseTargetBlock = new int[blockCount];
        Array.Fill(falseTargetBlock, -1);
        (int A, int B)[] falseNonNull = new (int, int)[blockCount];
        Array.Fill(falseNonNull, (-1, -1));
        int[]?[] falseExtraNonNull = new int[blockCount][];

        if (trackInit)
        {
            entryInit![0] = new bool[slotCount];
            int entryCount = Math.Min(slotCount, function.CaptureCount + function.ParameterCount);
            for (int i = 0; i < entryCount; i++) entryInit[0]![i] = true;
        }
        entryTypes[0] = new int[slotCount];
        entryNonNull[0] = new bool[slotCount];
        entryNarrows[0] = [];
        for (int s = 0; s < slotCount; s++)
        {
            int type = function.Slots[s].Type;
            entryTypes[0]![s] = type;
            NativeCodeType ct = program.Types[type];
            entryNonNull[0]![s] = ct.Pointee < 0 && ct.Kind != TypeKind.Never && (ct.Kind & TypeKind.Null) == 0;
        }

        bool[] scratchInit = trackInit ? new bool[slotCount] : [];
        int[] scratchTypes = new int[slotCount];
        bool[] scratchNonNull = new bool[slotCount];
        Dictionary<(int Source, int Type), int> scratchNarrows = [];
        int[] origins = new int[slotCount];
        Dictionary<int, int> isNullExpr = [];
        bool changed;
        do
        {
            changed = false;
            for (int b = 0; b < blockCount; b++)
            {
                if (b > 0)
                {
                    bool anyPred = false;
                    foreach (int pred in predecessors[b])
                    {
                        if (exitTypes[pred] is not { } predTypes || exitNonNull[pred] is not { } predNN) continue;
                        Dictionary<(int Source, int Type), int> predNarrows = exitNarrows[pred];
                        (int extraA, int extraB) = falseTargetBlock[pred] == b ? falseNonNull[pred] : (-1, -1);
                        int[]? extraList = falseTargetBlock[pred] == b ? falseExtraNonNull[pred] : null;
                        if (!anyPred)
                        {
                            if (trackInit) Array.Copy(exitInit![pred]!, scratchInit, slotCount);
                            Array.Copy(predTypes, scratchTypes, slotCount);
                            Array.Copy(predNN, scratchNonNull, slotCount);
                            if (extraA >= 0) scratchNonNull[extraA] = true;
                            if (extraB >= 0) scratchNonNull[extraB] = true;
                            if (extraList is not null) foreach (int slot in extraList) scratchNonNull[slot] = true;
                            scratchNarrows.Clear();
                            foreach (var pair in predNarrows) scratchNarrows[pair.Key] = pair.Value;
                            anyPred = true;
                        }
                        else
                        {
                            for (int s = 0; s < slotCount; s++)
                            {
                                if (trackInit) scratchInit[s] &= exitInit![pred]![s];
                                bool pnn = predNN[s] || s == extraA || s == extraB
                                    || (extraList is not null && Array.IndexOf(extraList, s) >= 0);
                                scratchNonNull[s] &= pnn;
                                if (scratchTypes[s] != predTypes[s])
                                    scratchTypes[s] = JoinProvenType(s, scratchTypes[s], predTypes[s]);
                            }
                            if (scratchNarrows.Count > 0)
                            {
                                foreach (var key in scratchNarrows.Keys.ToArray())
                                    if (!predNarrows.TryGetValue(key, out int existing) || existing != scratchNarrows[key])
                                        scratchNarrows.Remove(key);
                            }
                        }
                    }
                    if (!anyPred) continue;
                    if (entryTypes[b] is null)
                    {
                        if (trackInit) entryInit![b] = (bool[])scratchInit.Clone();
                        entryTypes[b] = (int[])scratchTypes.Clone();
                        entryNonNull[b] = (bool[])scratchNonNull.Clone();
                        entryNarrows[b] = new(scratchNarrows);
                    }
                    else
                    {
                        bool differs = entryNarrows[b].Count != scratchNarrows.Count
                            || !entryNarrows[b].All(p => scratchNarrows.TryGetValue(p.Key, out int v) && v == p.Value);
                        if (!differs)
                            for (int s = 0; s < slotCount; s++)
                                if (entryTypes[b]![s] != scratchTypes[s] || entryNonNull[b]![s] != scratchNonNull[s]
                                    || (trackInit && entryInit![b]![s] != scratchInit[s]))
                                { differs = true; break; }
                        if (!differs && exitTypes[b] is not null) continue;
                        if (trackInit) Array.Copy(scratchInit, entryInit![b]!, slotCount);
                        Array.Copy(scratchTypes, entryTypes[b]!, slotCount);
                        Array.Copy(scratchNonNull, entryNonNull[b]!, slotCount);
                        entryNarrows[b] = new(scratchNarrows);
                    }
                }
                else if (exitTypes[0] is not null) continue;

                if (trackInit) Array.Copy(entryInit![b]!, scratchInit, slotCount);
                Array.Copy(entryTypes[b]!, scratchTypes, slotCount);
                Array.Copy(entryNonNull[b]!, scratchNonNull, slotCount);
                scratchNarrows.Clear();
                foreach (var pair in entryNarrows[b]) scratchNarrows[pair.Key] = pair.Value;
                Array.Fill(origins, -1);
                isNullExpr.Clear();
                falseTargetBlock[b] = -1;
                falseNonNull[b] = (-1, -1);
                falseExtraNonNull[b] = null;

                NativeControlFlow.Block body = flow.Blocks[b];
                for (int pc = body.Start; pc < body.End; pc++)
                {
                    NativeInstruction ins = code[pc];
                    if (trackInit)
                    {
                        if (ins.Op == NativeOp.Drop || (ins.Op == NativeOp.Move && ins.A != ins.Result))
                            scratchInit[ins.A] = false;
                        if (ins.Op == NativeOp.BindLocation) scratchInit[ins.A] = false;
                        else if (ins.Result >= 0 && ins.Op != NativeOp.NextIterator) scratchInit[ins.Result] = true;
                    }
                    foreach (int def in NativeInstructionFacts.Definitions(ins)) RemoveNarrowForSlot(scratchNarrows, def);
                    if (ins.Op == NativeOp.Drop || (ins.Op == NativeOp.Move && ins.A != ins.Result) || ins.Op == NativeOp.BindLocation)
                        RemoveNarrowForSlot(scratchNarrows, ins.A);
                    if (NativeInstructionFacts.MemoryBarrier(ins) && !PrivateDrop(ins) && scratchNarrows.Count > 0)
                    {
                        foreach (var key in scratchNarrows.Where(p => ExternallyWritable(p.Key.Source) || ExternallyWritable(p.Value))
                            .Select(p => p.Key).ToArray())
                            scratchNarrows.Remove(key);
                    }
                    if (ins.Op == NativeOp.Narrow && ins.A != ins.Result && ins.Result >= 0)
                        scratchNarrows[(ins.A, ins.Type)] = ins.Result;
                    AdvanceTypeFacts(ins, scratchTypes, scratchNonNull, origins, isNullExpr, out int fA, out int fB);
                    if (fA >= 0 && pc == body.End - 1 && ins.Op == NativeOp.Branch && ins.B != ins.C)
                    {
                        falseTargetBlock[b] = blockOfPc[ins.C];
                        falseNonNull[b] = (fA, fB);
                        List<int>? extras = null;
                        for (int s = 0; s < slotCount; s++)
                        {
                            if (s == fA || s == fB) continue;
                            if (origins[s] == fA || (fB >= 0 && origins[s] == fB))
                                (extras ??= []).Add(s);
                        }
                        falseExtraNonNull[b] = extras?.ToArray();
                    }
                }

                if (exitTypes[b] is null)
                {
                    if (trackInit) exitInit![b] = (bool[])scratchInit.Clone();
                    exitTypes[b] = (int[])scratchTypes.Clone();
                    exitNonNull[b] = (bool[])scratchNonNull.Clone();
                    exitNarrows[b] = new(scratchNarrows);
                    changed = true;
                }
                else
                {
                    bool outChanged = exitNarrows[b].Count != scratchNarrows.Count
                        || !exitNarrows[b].All(p => scratchNarrows.TryGetValue(p.Key, out int v) && v == p.Value);
                    for (int s = 0; s < slotCount; s++)
                    {
                        if (exitTypes[b]![s] != scratchTypes[s] || exitNonNull[b]![s] != scratchNonNull[s]
                            || (trackInit && exitInit![b]![s] != scratchInit[s]))
                        {
                            if (trackInit) exitInit![b]![s] = scratchInit[s];
                            exitTypes[b]![s] = scratchTypes[s];
                            exitNonNull[b]![s] = scratchNonNull[s];
                            outChanged = true;
                        }
                    }
                    if (outChanged)
                    {
                        exitNarrows[b] = new(scratchNarrows);
                        changed = true;
                    }
                }
            }
        } while (changed);
        return (entryInit, entryTypes, entryNonNull, entryNarrows);
    }

    private static void RemoveNarrowForSlot(Dictionary<(int Source, int Type), int> narrows, int slot)
    {
        if (narrows.Count == 0 || slot < 0) return;
        foreach (var key in narrows.Where(p => p.Key.Source == slot || p.Value == slot).Select(p => p.Key).ToArray())
            narrows.Remove(key);
    }

    private int JoinProvenType(int slot, int left, int right)
    {
        if (left == right) return left;
        if (NativeTypeRelations.Accepts(program.Types, left, right)) return left;
        if (NativeTypeRelations.Accepts(program.Types, right, left)) return right;
        return function.Slots[slot].Type;
    }

    private int TighterType(int first, int second)
    {
        if (first == second) return first;
        if (NativeTypeRelations.Accepts(program.Types, first, second)) return second;
        if (NativeTypeRelations.Accepts(program.Types, second, first)) return first;
        return first;
    }

    private bool ProvenSatisfies(int sourceType, bool isNonNull, int targetType)
    {
        if (!isNonNull) return NativeTypeRelations.Accepts(program.Types, targetType, sourceType);
        NativeCodeType src = program.Types[sourceType], dst = program.Types[targetType];
        if (src.Pointee >= 0 || dst.Pointee >= 0) return NativeTypeRelations.Accepts(program.Types, targetType, sourceType);
        TypeKind srcKind = src.Kind & ~TypeKind.Null;
        if ((dst.Kind & srcKind) != srcKind) return false;
        if ((srcKind & TypeKind.Scope) != 0 && dst.Layouts is not null)
        {
            if (src.Layouts is null) return false;
            foreach (int layout in src.Layouts) if (!dst.Layouts.Contains(layout)) return false;
        }
        return dst.Element < 0 || (srcKind & TypeKind.Range) == 0
            || src.Element >= 0 && NativeTypeRelations.Accepts(program.Types, dst.Element, src.Element);
    }

    private void AdvanceTypeFacts(NativeInstruction ins, int[] types, bool[] nonNull, int[] origins,
        Dictionary<int, int> nullChecks, out int branchFalseSlotA, out int branchFalseSlotB)
    {
        branchFalseSlotA = -1;
        branchFalseSlotB = -1;
        if (ins.Op == NativeOp.Branch && nullChecks.TryGetValue(ins.A, out int tested))
        {
            branchFalseSlotA = tested;
            branchFalseSlotB = origins[tested];
        }
        int nextOrigin = -1;
        int nextType = -1;
        bool nextNN = false;
        bool hasKnownTransfer = false;
        if (ins.Op is NativeOp.Copy or NativeOp.Move or NativeOp.Narrow or NativeOp.Convert)
        {
            int src = ins.A;
            if (ins.Op is NativeOp.Copy or NativeOp.Move)
            {
                nextType = TighterType(function.Slots[ins.Result].Type, types[src]);
                nextNN = nonNull[src] || (program.Types[function.Slots[ins.Result].Type] is { Pointee: < 0 } rt
                    && rt.Kind != TypeKind.Never && (rt.Kind & TypeKind.Null) == 0);
                nextOrigin = !ExternallyWritable(src) ? (origins[src] >= 0 ? origins[src] : src) : -1;
                hasKnownTransfer = true;
            }
            else if (ins.Op == NativeOp.Narrow)
            {
                nextType = TighterType(types[src], ins.Type);
                NativeCodeType nt = program.Types[nextType];
                nextNN = nonNull[src] || (nt.Pointee < 0 && nt.Kind != TypeKind.Never && (nt.Kind & TypeKind.Null) == 0);
                nextOrigin = !ExternallyWritable(src) ? (origins[src] >= 0 ? origins[src] : src) : -1;
                hasKnownTransfer = true;
            }
            else
            {
                nextType = TighterType(function.Slots[ins.Result].Type, ins.Type);
                NativeCodeType nt = program.Types[nextType];
                nextNN = nt.Pointee < 0 && nt.Kind != TypeKind.Never && (nt.Kind & TypeKind.Null) == 0;
                hasKnownTransfer = true;
            }
        }
        foreach (int def in NativeInstructionFacts.Definitions(ins))
        {
            int baseType = function.Slots[def].Type;
            types[def] = baseType;
            NativeCodeType ct = program.Types[baseType];
            nonNull[def] = ct.Pointee < 0 && ct.Kind != TypeKind.Never && (ct.Kind & TypeKind.Null) == 0;
            origins[def] = -1;
            for (int k = 0; k < origins.Length; k++) if (origins[k] == def) origins[k] = -1;
            nullChecks.Remove(def);
            foreach (int k in nullChecks.Where(p => p.Value == def).Select(p => p.Key).ToArray()) nullChecks.Remove(k);
        }
        if (NativeInstructionFacts.MemoryBarrier(ins) && !PrivateDrop(ins))
        {
            for (int s = 0; s < types.Length; s++)
            {
                if (!ExternallyWritable(s)) continue;
                int baseType = function.Slots[s].Type;
                types[s] = baseType;
                NativeCodeType ct = program.Types[baseType];
                nonNull[s] = ct.Pointee < 0 && ct.Kind != TypeKind.Never && (ct.Kind & TypeKind.Null) == 0;
                origins[s] = -1;
                nullChecks.Remove(s);
            }
        }
        if (ins.Op == NativeOp.Narrow && !ExternallyWritable(ins.A))
        {
            types[ins.A] = TighterType(types[ins.A], ins.Type);
            NativeCodeType nt = program.Types[types[ins.A]];
            if (nt.Pointee < 0 && nt.Kind != TypeKind.Never && (nt.Kind & TypeKind.Null) == 0) nonNull[ins.A] = true;
        }
        if (hasKnownTransfer && ins.Result >= 0 && ins.Result != ins.A)
        {
            types[ins.Result] = nextType;
            nonNull[ins.Result] = nextNN;
            origins[ins.Result] = nextOrigin;
        }
        else if (hasKnownTransfer && ins.Result == ins.A && ins.Result >= 0)
        {
            types[ins.Result] = nextType;
            nonNull[ins.Result] = nextNN;
        }
        else if (ins.Op is NativeOp.Allocate or NativeOp.Constant && ins.Result >= 0)
        {
            if (ins.Op == NativeOp.Allocate || constants[ins.A].Kind != NativeKind.Null)
                nonNull[ins.Result] = true;
        }
        if (ins.Op == NativeOp.Unary && (NativeUnary)ins.Immediate == NativeUnary.IsNull && !ExternallyWritable(ins.A))
            nullChecks[ins.Result] = ins.A;
    }

    private bool Step(int pc)
    {
        int previousRevisions = revisions;
        NativeInstruction original = code[pc];
        NativeInstruction instruction = NativeInstructionFacts.RewriteValues(original, Resolve);
        instruction = FrameAccess(instruction);
        instruction = Simplify(pc, instruction);
        if (instruction.Op == NativeOp.Load && addresses.TryGetValue(instruction.A, out Address address)
            && memory.TryGetValue(address, out Memory? stored) && stored.Type == instruction.Type)
            instruction = stored.Constant is { } constant
                ? Constant(instruction, constant) : instruction with { Op = NativeOp.Copy, A = stored.Source, B = -1 };
        ObserveReads(instruction);
        foreach (int result in NativeInstructionFacts.Definitions(instruction))
        {
            if (result == instruction.Result && singleAssignment[result] && !addressed.Contains(result) && !cyclic[pc])
            { freshDefinitions++; continue; }
            Invalidate(result, instruction.Op == NativeOp.BindLocation);
        }
        if (NativeInstructionFacts.MemoryBarrier(instruction) && !PrivateDrop(instruction)) Barrier();
        AdvanceTypeFacts(instruction, provenTypes, provenNonNull, slotOrigins, nullChecks, out _, out _);
        if (instruction.Op == NativeOp.Narrow && instruction.A != instruction.Result)
            availableNarrows[(instruction.A, instruction.Type)] = instruction.Result;
        instruction = Track(pc, instruction);
        if (instruction != original) revisions++;
        code[pc] = instruction;
        if (instruction.Op == NativeOp.Drop || instruction.Op == NativeOp.Move && instruction.A != instruction.Result)
        {
            initialized.Remove(instruction.A);
            RemoveNarrowForSlot(availableNarrows, instruction.A);
        }
        else if (instruction.Op == NativeOp.BindLocation)
        {
            RemoveNarrowForSlot(availableNarrows, instruction.A);
        }
        if (instruction.Result >= 0 && instruction.Op != NativeOp.NextIterator) initialized.Add(instruction.Result);
        previous = instruction;
        return revisions != previousRevisions;
    }

    private NativeInstruction FrameAccess(NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.FrameAddress && bindings.TryGetValue(instruction.A, out Address binding)
            && binding.Field >= 0)
            foreach ((int source, Address target) in addresses)
                if (target == binding && initialized.Contains(source) && frameCells.IsPrivate(source)
                    && function.Slots[source].Type == function.Slots[instruction.Result].Type)
                    return instruction with { Op = NativeOp.Copy, A = source, B = -1, C = -1 };
        // Copy is discardable; checked memory reads may only become Copy when
        // the actual private cell cannot be cleared through another alias.
        if (previous is not { Op: NativeOp.FrameAddress } address
            || instruction.A != address.Result || address.A == address.Result
            || !frameCells.IsPrivate(address.A))
            return instruction;
        NativeSlot pointer = function.Slots[address.Result];
        if (pointer.Role != NativeSlotRole.Temporary || pointer.Escapes || addressed.Contains(address.Result))
            return instruction;
        int type = function.Slots[address.A].Type;
        if (instruction.Op == NativeOp.Load && instruction.Result != address.A
            && initialized.Contains(address.A) && instruction.Type == type
            && NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, type))
            return instruction with { Op = NativeOp.Copy, A = address.A, B = -1, C = -1 };
        if (instruction.Op == NativeOp.Store && instruction.B != address.A
            && initialized.Contains(instruction.B) && frameCells.IsPrivate(instruction.B)
            && instruction.Type == type && NativeTypeRelations.Accepts(program.Types, type, function.Slots[instruction.B].Type))
            return instruction with { Op = NativeOp.Copy, Result = address.A, A = instruction.B, B = -1, C = -1 };
        return instruction;
    }

    private NativeInstruction Simplify(int pc, NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.BindLocation && unusedBindings.Contains(instruction.A)
            && initialized.Contains(instruction.B) && frameCells.IsPrivate(instruction.B)
            && addresses.TryGetValue(instruction.B, out Address binding) && binding.Field >= 0)
            return Nop(instruction);
        if (instruction.Op == NativeOp.Copy && instruction.Result == instruction.A) return Nop(instruction);
        if (instruction.Op == NativeOp.Copy && known.TryGetValue(instruction.A, out NativeConstant? value))
            return Constant(instruction, value);
        if (instruction.Op == NativeOp.Allocate && instruction.C >= 0
            && known.TryGetValue(instruction.C, out NativeConstant? count)
            && count is { Kind: NativeKind.Number, Number: 1 }) return instruction with { C = -1 };
        if (instruction.Op == NativeOp.Branch && known.TryGetValue(instruction.A, out NativeConstant? condition)
            && condition.Kind == NativeKind.Bool)
            return instruction with { Op = NativeOp.Jump, A = condition.Boolean ? instruction.B : instruction.C, B = -1, C = -1 };
        if (instruction.Op == NativeOp.Narrow && known.TryGetValue(instruction.A, out NativeConstant? supplied)
            && program.Types[instruction.Type] is { Pointee: < 0 } target && Accepts(target.Kind, supplied.Kind))
            return Constant(instruction, supplied);
        if (instruction.Op == NativeOp.Narrow && ProvenSatisfies(provenTypes[instruction.A], provenNonNull[instruction.A], instruction.Type))
        {
            if (NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, function.Slots[instruction.A].Type))
            {
                eliminatedNarrows++;
                return instruction with { Op = NativeOp.Copy, B = -1, C = -1 };
            }
            if (availableNarrows.TryGetValue((instruction.A, instruction.Type), out int prevNarrow)
                && prevNarrow != instruction.Result && initialized.Contains(prevNarrow)
                && NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, function.Slots[prevNarrow].Type))
            {
                eliminatedNarrows++;
                return instruction with { Op = NativeOp.Copy, A = prevNarrow, B = -1, C = -1 };
            }
            redundantNarrows.Add(pc);
        }
        if (instruction.Op == NativeOp.Convert
            && ProvenSatisfies(provenTypes[instruction.A], provenNonNull[instruction.A], instruction.Type)
            && program.Types[instruction.Type] is { Pointee: < 0, Element: < 0 } convTarget
            && convTarget.Kind is TypeKind.Bool or TypeKind.Number or TypeKind.Scope or TypeKind.Null
                or (TypeKind.Null | TypeKind.Scope) or (TypeKind.Null | TypeKind.Number) or (TypeKind.Null | TypeKind.Bool)
            && NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, function.Slots[instruction.A].Type))
        {
            eliminatedConverts++;
            return instruction with { Op = NativeOp.Copy, B = -1, C = -1 };
        }
        NativeConstant? typed = TypedUnary(instruction);
        if (typed is not null) return Constant(instruction, typed);
        NativeConstant? folded = NativeConstantFolding.Fold(instruction, known);
        return folded is null ? instruction : Constant(instruction, folded);
    }

    private NativeConstant? TypedUnary(NativeInstruction instruction)
    {
        if (instruction.Op != NativeOp.Unary) return null;
        NativeCodeType source = program.Types[function.Slots[instruction.A].Type];
        if (source.Pointee >= 0 || source.Kind == TypeKind.Never) return null;
        if ((NativeUnary)instruction.Immediate == NativeUnary.IsNull)
        {
            if (provenNonNull[instruction.A]) return new(NativeKind.Bool, Boolean: false);
            if (source.Kind == TypeKind.Null) return new(NativeKind.Bool, Boolean: true);
            return (source.Kind & TypeKind.Null) == 0 ? new(NativeKind.Bool, Boolean: false) : null;
        }
        if ((NativeUnary)instruction.Immediate != NativeUnary.IsType
            || !known.TryGetValue(instruction.B, out NativeConstant? target)
            || target is not { Kind: NativeKind.Type, Type: not null }) return null;
        if ((source.Kind & TypeKind.Type) != 0) return null;
        if (AcceptsStatic(target.Type, function.Slots[instruction.A].Type)) return new(NativeKind.Bool, Boolean: true);
        return (source.Kind & target.Type.Kind) == 0 ? new(NativeKind.Bool, Boolean: false) : null;
    }

    private bool AcceptsStatic(TypeRef expected, int type)
    {
        while (true)
        {
            NativeCodeType source = program.Types[type];
            if (source.Pointee >= 0 || source.Kind == TypeKind.Never || (expected.Kind & source.Kind) != source.Kind) return false;
            if (expected.Element is null) return true;
            if (source.Element < 0) return false;
            expected = expected.Element; type = source.Element;
        }
    }

    private NativeInstruction Track(int pc, NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.Constant) known[instruction.Result] = constants[instruction.A];
        if (instruction.Op == NativeOp.Copy)
        {
            if (function.Slots[instruction.Result].Type == function.Slots[instruction.A].Type
                && !addressed.Contains(instruction.A) && !function.Slots[instruction.A].Escapes
                && function.Slots[instruction.A].Role != NativeSlotRole.Capture) aliases[instruction.Result] = instruction.A;
            if (addresses.TryGetValue(instruction.A, out Address source)) addresses[instruction.Result] = source;
        }
        if (instruction.Op == NativeOp.Allocate)
            addresses[instruction.Result] = new(pc, -1, -1, instruction.A);
        if (instruction.Op == NativeOp.FrameAddress) FrameAddress(instruction);
        if (instruction.Op == NativeOp.BindLocation) Bind(instruction);
        if (instruction.Op is NativeOp.AddressOffset or NativeOp.ProjectAddress) Project(instruction);
        return instruction.Op == NativeOp.Store ? Store(pc, instruction) : instruction;
    }

    private void FrameAddress(NativeInstruction instruction)
    {
        if (bindings.TryGetValue(instruction.A, out Address binding)) addresses[instruction.Result] = binding;
        else if (!bound.Contains(instruction.A) && (function.Slots[instruction.A].Role != NativeSlotRole.Capture
            || (instruction.A < function.CaptureCount && function.Signature.Captures[instruction.A].Mode == NativeCaptureMode.Value)))
            addresses[instruction.Result] = new(-1, instruction.A, -1, -1);
    }

    private void Bind(NativeInstruction instruction)
    {
        bindings.Remove(instruction.A);
        if (addresses.TryGetValue(instruction.B, out Address address)) bindings[instruction.A] = address;
    }

    private bool PrivateDrop(NativeInstruction instruction) => instruction.Op == NativeOp.Drop
        && function.Slots[instruction.A].Role == NativeSlotRole.Temporary
        && !function.Slots[instruction.A].Escapes && !addressed.Contains(instruction.A);

    private void Project(NativeInstruction instruction)
    {
        if (!addresses.TryGetValue(instruction.A, out Address source) || source.Allocation < 0 || source.Field >= 0) return;
        int field = instruction.Op == NativeOp.AddressOffset && instruction.B == source.Layout ? instruction.C : -1;
        if (instruction.Op == NativeOp.ProjectAddress)
            foreach (NativeProjectionCase alternative in program.Projections[instruction.B].Cases)
                if (alternative.Layout == source.Layout) { field = alternative.Field; break; }
        if (field >= 0) addresses[instruction.Result] = source with { Field = field };
    }

    private NativeInstruction Store(int pc, NativeInstruction instruction)
    {
        if (!addresses.TryGetValue(instruction.A, out Address address)) { Clear(); return instruction; }
        NativeConstant? value = known.GetValueOrDefault(instruction.B);
        if (memory.TryGetValue(address, out Memory? previous) && previous.Type == instruction.Type)
        {
            bool same = value is not null && previous.Constant is not null
                ? NativeConstantPool.Same(value, previous.Constant) : previous.Source == instruction.B;
            if (same && instruction.C == 0) return Nop(instruction);
            if (Private(address) && previous.Store >= 0 && frameCells.IsPrivate(previous.Source))
            {
                NativeInstruction old = code[previous.Store];
                code[previous.Store] = Nop(old);
                revisions++;
                if (old.C != 0) instruction = instruction with { C = old.C };
            }
        }
        if (address.Slot >= 0) Invalidate(address.Slot);
        foreach (int slot in bindings.Where(pair => pair.Value == address).Select(pair => pair.Key).ToArray()) Invalidate(slot);
        memory[address] = new(instruction.B, value, instruction.Type, pc);
        return instruction;
    }

    private bool Private(Address address) => address.Allocation >= 0
        ? !escape.EscapingAllocations.Contains(address.Allocation)
        : !function.Slots[address.Slot].Escapes && !escape.EscapingLocations.Contains(address.Slot)
            && function.Slots[address.Slot].Role != NativeSlotRole.Capture;

    private void ObserveReads(NativeInstruction instruction)
    {
        foreach (int source in NativeInstructionFacts.ValueReads(instruction))
        {
            if (bindings.TryGetValue(source, out Address binding) && memory.TryGetValue(binding, out Memory? value))
                memory[binding] = value with { Store = -1 };
            Address frame = new(-1, source, -1, -1);
            if (memory.TryGetValue(frame, out Memory? stored)) memory[frame] = stored with { Store = -1 };
        }
    }

    private int Resolve(int value) => aliases.GetValueOrDefault(value, value);

    private void Invalidate(int result, bool rebind = false)
    {
        aliases.Remove(result); known.Remove(result); addresses.Remove(result);
        foreach (var pair in availableNarrows.Where(p => p.Key.Source == result || p.Value == result).Select(p => p.Key).ToArray())
            availableNarrows.Remove(pair);
        foreach (int alias in aliases.Where(pair => pair.Value == result).Select(pair => pair.Key).ToArray()) aliases.Remove(alias);
        foreach (Address address in memory.Where(pair => !rebind && pair.Key.Slot == result
            || pair.Value.Source == result && pair.Value.Constant is null).Select(pair => pair.Key).ToArray()) memory.Remove(address);
        if (!rebind && bindings.TryGetValue(result, out Address binding)) memory.Remove(binding);
        if (!rebind && bound.Contains(result) && !bindings.ContainsKey(result)) memory.Clear();
    }

    private void Clear()
    {
        aliases.Clear(); known.Clear(); addresses.Clear(); bindings.Clear(); memory.Clear(); previous = null;
        initialized.Clear(); initialized.UnionWith(stableParameters);
        availableNarrows.Clear(); nullChecks.Clear();
        ResetProvenTypes();
    }

    private void Barrier()
    {
        memory.Clear();
        foreach (int slot in Enumerable.Range(0, function.Slots.Count).Where(ExternallyWritable))
        {
            aliases.Remove(slot); known.Remove(slot); addresses.Remove(slot);
            foreach (var pair in availableNarrows.Where(p => p.Key.Source == slot || p.Value == slot).Select(p => p.Key).ToArray())
                availableNarrows.Remove(pair);
        }
        foreach (int alias in aliases.Where(pair => ExternallyWritable(pair.Value)).Select(pair => pair.Key).ToArray()) aliases.Remove(alias);
    }

    private bool ExternallyWritable(int slot) => addressed.Contains(slot) || function.Slots[slot].Escapes
        || function.Slots[slot].Role == NativeSlotRole.Capture;

    private NativeInstruction Constant(NativeInstruction instruction, NativeConstant value) =>
        new(NativeOp.Constant, instruction.Result, constants.Add(value), Type: instruction.Type, Source: instruction.Source);

    internal static NativeInstruction Nop(NativeInstruction instruction) => new(NativeOp.Nop, Source: instruction.Source);

    private static bool Accepts(TypeKind type, NativeKind value) => (type & NativeValue.TypeOfKind(value).Kind) != 0;

}

internal sealed class NativeConstantPool
{
    private readonly record struct Key(NativeKind Kind, string? Text, ulong NumberBits,
        bool Boolean, TypeRef? Type, ConnectionKind ConnectionKind, NativePath? Path, int OriginId, int OriginOffset);
    private readonly List<NativeConstant> values;
    private readonly Dictionary<Key, int> indices = [];
    internal NativeConstantPool(IEnumerable<NativeConstant> initial)
    {
        values = initial.ToList();
        for (int index = 0; index < values.Count; index++) indices.TryAdd(Identity(values[index]), index);
    }
    internal NativeConstant this[int index] => values[index];
    internal IReadOnlyList<NativeConstant> Values => values;
    internal int Add(NativeConstant value)
    {
        Key key = Identity(value);
        if (indices.TryGetValue(key, out int found)) return found;
        int index = values.Count;
        values.Add(value); indices.Add(key, index); return index;
    }
    internal static bool Same(NativeConstant left, NativeConstant right) => left.Kind == right.Kind
        && left.Text == right.Text && left.Boolean == right.Boolean && left.Type == right.Type
        && left.ConnectionKind == right.ConnectionKind && left.Path == right.Path
        && left.OriginId == right.OriginId && left.OriginOffset == right.OriginOffset
        && BitConverter.DoubleToUInt64Bits(left.Number) == BitConverter.DoubleToUInt64Bits(right.Number);
    private static Key Identity(NativeConstant value) => new(value.Kind, value.Text,
        BitConverter.DoubleToUInt64Bits(value.Number), value.Boolean, value.Type, value.ConnectionKind, value.Path, value.OriginId, value.OriginOffset);
}
