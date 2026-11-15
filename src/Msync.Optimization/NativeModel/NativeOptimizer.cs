using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

public static partial class NativeOptimizer
{
    public static NativeProgram Optimize(NativeProgram program, int level = 2)
        => OptimizeCore(program, level, null);

    public static NativeProgram PlanStorage(NativeProgram program, CompilationTrace? trace = null) =>
        NativeStoragePlanning.Run(program, trace);

    public static NativeProgram Optimize(NativeProgram program, int level, CompilationTrace? trace)
    {
        if (trace?.IncludeOptimizerPasses != true)
        {
            NativeProgram result = OptimizeCore(program, level, null);
            return result;
        }
        NativeOptimizationProfile profile = new(trace);
        try
        {
            NativeProgram result = OptimizeCore(program, level, profile);
            return result;
        }
        catch (Exception error) { profile.Fail(error); throw; }
        finally { profile.Publish(); }
    }

    private static NativeProgram OptimizeCore(NativeProgram program, int level, NativeOptimizationProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (level is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(level));
        if (level == 0) return NativeStoragePlanning.Run(program with { OptimizationLevel = level }, profile?.Trace);
        program = program with { Functions = program.Functions.Select(function => function with { Storage = null }).ToArray() };
        int[] inlineBudgets = Enumerable.Repeat(level == 2 ? 384 : 1536, program.Functions.Count).ToArray();
        NativeAddressAnalysis.ProgramFacts? sharedFacts = null;
        program = NativeInlining.Run(program, level, profile, inlineBudgets, ref sharedFacts);
        NativeConstantPool constants = new(program.Constants);
        NativeFunctionCode[] functions = program.Functions.ToArray();
        List<NativeAllocationPlan> plans = program.Allocations
            .Where(plan => plan.Placement == NativeAllocationPlacement.Eliminated).ToList();
        NativeProgram workingProgram = program with { Functions = functions };
        for (int function = functions.Length - 1; function >= 0; function--)
        {
            profile?.Function(function);
            var state = (program: workingProgram, function, constants, plans, profile, level, facts: sharedFacts);
            (functions[function], sharedFacts) = profile is null
                ? OptimizeFunction(state.program, function, constants, plans, null, level, sharedFacts)
                : profile.Trace.MeasureDetailed("optimizer.function", state,
                    static item =>
                    {
                        try { return OptimizeFunction(item.program, item.function, item.constants, item.plans, item.profile, item.level, item.facts); }
                        catch (Exception error) { item.profile!.Fail(error); throw; }
                    },
                    CompilationSize.Of(functions[function]), static result => CompilationSize.Of(result.Function),
                    attributes: new Dictionary<string, string>
                    { ["function.name"] = functions[function].Name, ["function.id"] = function.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            workingProgram = program with { Functions = functions };
        }
        NativeProgram optimized = program with
        { Functions = functions, Constants = constants.Values.ToArray(), Allocations = plans.ToArray(), OptimizationLevel = level };
        NativeProgram compacted = Compact(optimized, profile, sharedFacts);
        NativeProgram compact = InlineCleaned(compacted, level, profile, inlineBudgets, ref sharedFacts);
        compact = NativeStoragePlanning.Run(compact, profile?.Trace, sharedFacts);
        return Compact(NativeRegisterAllocation.Run(compact, profile, sharedFacts), profile);
    }

    private static NativeProgram Compact(NativeProgram program, NativeOptimizationProfile? profile,
        NativeAddressAnalysis.ProgramFacts? sharedFacts = null)
    {
        NativeFunctionCode[] functions = new NativeFunctionCode[program.Functions.Count];
        int[][] positions = new int[functions.Length][];
        bool[][] retained = new bool[functions.Length][];
        List<int>? changed = sharedFacts is null ? null : [];
        for (int index = 0; index < functions.Length; index++)
        {
            NativeFunctionCode original = program.Functions[index];
            profile?.Function(index);
            var started = profile?.Start(NativeOptimizationProfile.Pass.Compact, original) ?? default;
            NativeControlOptimization.Result result = NativeControlOptimization.Compact(original);
            functions[index] = result.Function;
            positions[index] = result.Positions;
            retained[index] = result.Retained;
            bool fnChanged = !original.Instructions.SequenceEqual(result.Function.Instructions);
            if (fnChanged) changed?.Add(index);
            profile?.Add(NativeOptimizationProfile.Pass.Compact, started, original, result.Function, fnChanged);
        }
        NativeAllocationPlan[] allocations = program.Allocations
            .Where(plan => plan.Placement != NativeAllocationPlacement.Eliminated
                && retained[plan.Function][plan.Instruction])
            .Select(plan => plan with { Instruction = positions[plan.Function][plan.Instruction] }).ToArray();
        NativeProgram compacted = program with
        {
            Functions = functions,
            Allocations = allocations,
            ExecutionPlans = program.ExecutionPlans?.Select(plan => plan with
            {
                Start = NativeInlining.Boundary(positions[plan.Function], plan.Start),
                End = NativeInlining.Boundary(positions[plan.Function], plan.End)
            }).ToArray()
        };
        sharedFacts?.Invalidate(compacted, changed is { Count: > 0 } ? CollectionsMarshal.AsSpan(changed) : []);
        return compacted;
    }

    private static (NativeFunctionCode Function, NativeAddressAnalysis.ProgramFacts? SharedFacts) OptimizeFunction(
        NativeProgram program, int index, NativeConstantPool constants, List<NativeAllocationPlan> plans,
        NativeOptimizationProfile? profile, int level, NativeAddressAnalysis.ProgramFacts? sharedFacts)
    {
        NativeFunctionCode original = program.Functions[index];
        if (original.IsTemplate || original.Instructions.Count == 0) return (original, sharedFacts);
        NativeInstruction[] code = original.Instructions.ToArray();
        NativeFunctionCode function = original with { Instructions = code };
        NativeProgram view = FunctionView(program, index, function);
        bool changed;
        bool factsDirty = false;
        int invalidations = 0;
        int iteration = 0;
        NativeEscapeAnalysis finalEscape;
        NativeControlFlow finalFlow;
        do
        {
            profile?.Iteration(++iteration);
            var globalStarted = profile?.Start(NativeOptimizationProfile.Pass.GlobalFacts, function) ?? default;
            bool globalChanged = level >= 2 && NativeGlobalFacts.Run(function, code, constants);
            if (level >= 2) profile?.Add(NativeOptimizationProfile.Pass.GlobalFacts, globalStarted, function, globalChanged);
            if (globalChanged) { factsDirty = true; }
            var started = profile?.Start(NativeOptimizationProfile.Pass.ControlFlow, function) ?? default;
            NativeControlFlow flow = new(code);
            profile?.Add(NativeOptimizationProfile.Pass.ControlFlow, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.EscapeAnalysis, function) ?? default;
            if (sharedFacts is null) sharedFacts = new(view);
            else if (factsDirty) { sharedFacts.Invalidate(view, index); invalidations++; factsDirty = false; }
            NativeEscapeAnalysis escape = new(view, index, sharedFacts);
            profile?.Add(NativeOptimizationProfile.Pass.EscapeAnalysis, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.GlobalMemory, function) ?? default;
            bool memoryChanged = level >= 2 && NativeGlobalMemory.Run(view, index, code, flow, escape, profile);
            if (level >= 2) profile?.Add(NativeOptimizationProfile.Pass.GlobalMemory, started, function, memoryChanged);
            started = profile?.Start(NativeOptimizationProfile.Pass.LocalValues, function) ?? default;
            NativeFunctionCode tightened = NativeLocalOptimization.TightenSlotTypes(view, function, code, escape, profile);
            if (!ReferenceEquals(tightened, function)) { function = tightened; changed = true; }
            bool localChanged = new NativeLocalOptimization(view, function, code, constants, escape).Run(flow, profile);
            changed = globalChanged || memoryChanged || localChanged;
            profile?.Add(NativeOptimizationProfile.Pass.LocalValues, started, function, localChanged);
            if (memoryChanged || localChanged) { factsDirty = true; }
            started = profile?.Start(NativeOptimizationProfile.Pass.DeadMemory, function) ?? default;
            bool removedMemory = NativeDeadMemory.Run(view, index, code, escape, profile);
            changed |= removedMemory;
            profile?.Add(NativeOptimizationProfile.Pass.DeadMemory, started, function, removedMemory);
            if (removedMemory) { factsDirty = true; }
            started = profile?.Start(NativeOptimizationProfile.Pass.ControlFlow, function) ?? default;
            flow = new(code);
            profile?.Add(NativeOptimizationProfile.Pass.ControlFlow, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.DeadResults, function) ?? default;
            bool removedResults = RemoveDead(view, index, flow, code, plans);
            changed |= removedResults;
            profile?.Add(NativeOptimizationProfile.Pass.DeadResults, started, function, removedResults);
            if (removedResults) { factsDirty = true; }
            finalEscape = escape;
            finalFlow = flow;
        } while (changed);

        bool slotsChanged = false;
        NativeSlot[] updatedSlots = function.Slots.ToArray();
        foreach (int number in finalEscape.EscapingLocations)
        {
            if (!updatedSlots[number].Escapes)
            {
                updatedSlots[number] = updatedSlots[number] with { Escapes = true };
                slotsChanged = true;
            }
        }
        if (slotsChanged)
        {
            function = function with { Slots = updatedSlots };
            view = FunctionView(program, index, function);
            factsDirty = true;
        }
        view = FunctionView(program, index, function);
        // Last-use moves and producer retargeting preserve address/lifetime facts.
        // Capture the converged analysis here; later coloring/compaction relocates it.
        function = function with { Storage = NativeStoragePlanning.Capture(view, index, finalEscape, profile?.Trace) };
        var finalStarted = profile?.Start(NativeOptimizationProfile.Pass.LastUseMoves, function) ?? default;
        bool moved = MoveLastUses(view, index, finalFlow, code);
        if (level >= 2) moved |= CollapseSingleUseMoves(view, index, finalFlow, code, profile);
        profile?.Add(NativeOptimizationProfile.Pass.LastUseMoves, finalStarted, function, moved);
        if (moved) { factsDirty = true; }
        if (factsDirty && sharedFacts is not null) { sharedFacts.Invalidate(view, index); invalidations++; }
        finalStarted = profile?.Start(NativeOptimizationProfile.Pass.Placement, function) ?? default;
        bool placed = false;
        foreach (var allocation in finalEscape.Layouts)
        {
            NativeAllocationPlacement placement = !finalEscape.EscapingAllocations.Contains(allocation.Key)
                && !finalFlow.Cyclic[allocation.Key] && code[allocation.Key].C < 0
                    ? NativeAllocationPlacement.Frame : NativeAllocationPlacement.LogicalHeap;
            if (profile is not null)
                placed |= !plans.Any(plan => plan.Function == index && plan.Instruction == allocation.Key
                    && plan.Layout == allocation.Value && plan.Placement == placement);
            plans.RemoveAll(plan => plan.Function == index && plan.Instruction == allocation.Key);
            plans.Add(new(allocation.Key, allocation.Value, placement, index));
        }
        profile?.Add(NativeOptimizationProfile.Pass.Placement, finalStarted, function, placed);
        if (profile is not null)
        {
            profile.Trace.Metric("optimizer.iterations", iteration);
            profile.Trace.Metric("program_facts.invalidations", invalidations);
            foreach (NativeAllocationPlacement placement in Enum.GetValues<NativeAllocationPlacement>())
                profile.Trace.Metric("allocations." + placement, plans.Count(plan => plan.Function == index && plan.Placement == placement));
        }
        return (function, sharedFacts);
    }

    private static NativeProgram FunctionView(NativeProgram program, int index, NativeFunctionCode function)
    {
        NativeFunctionCode[] functions = program.Functions.ToArray(); functions[index] = function;
        return program with { Functions = functions };
    }

    private static bool RemoveDead(NativeProgram program, int index, NativeControlFlow flow,
        NativeInstruction[] code, List<NativeAllocationPlan> plans)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeLiveness live = new(program, index, flow);
        HashSet<int> addressed = code.Where(item => item.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(item => item.A).ToHashSet();
        bool changed = false;
        for (int pc = 0; pc < code.Length; pc++)
        {
            NativeInstruction instruction = code[pc];
            if (instruction.Op == NativeOp.Drop && !addressed.Contains(instruction.A)
                && !function.Slots[instruction.A].Escapes && function.Slots[instruction.A].Role == NativeSlotRole.Temporary)
            {
                code[pc] = NativeLocalOptimization.Nop(instruction); changed = true;
                continue;
            }
            if (instruction.Result < 0 || live.After[pc].Contains(instruction.Result)
                || addressed.Contains(instruction.Result) || !Discardable(program, function, instruction)) continue;
            if (instruction.Op == NativeOp.Allocate)
            {
                plans.RemoveAll(plan => plan.Function == index && plan.Instruction == pc);
                plans.Add(new(pc, instruction.A, NativeAllocationPlacement.Eliminated, index));
            }
            code[pc] = NativeLocalOptimization.Nop(instruction); changed = true;
        }
        return changed;
    }

    private static bool MoveLastUses(NativeProgram program, int index, NativeControlFlow flow, NativeInstruction[] code)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeLiveness live = new(program, index, flow);
        HashSet<int> addressed = code.Where(item => item.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(item => item.A).ToHashSet();
        bool changed = false;
        for (int pc = 0; pc < code.Length; pc++)
        {
            NativeInstruction instruction = code[pc];
            if (instruction.Op != NativeOp.Copy || instruction.A == instruction.Result
                || function.Slots[instruction.A].Role != NativeSlotRole.Temporary || addressed.Contains(instruction.A)
                || live.After[pc].Contains(instruction.A)) continue;
            code[pc] = instruction with { Op = NativeOp.Move };
            changed = true;
        }
        return changed;
    }

    private static bool CollapseSingleUseMoves(NativeProgram program, int index, NativeControlFlow flow,
        NativeInstruction[] code, NativeOptimizationProfile? profile)
    {
        NativeFunctionCode function = program.Functions[index];
        HashSet<int> addressed = code.Where(item => item.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(item => item.A).ToHashSet();
        int protectedPrefix = function.CaptureCount + function.ParameterCount;
        bool preserveEntryLocals = program.PreserveLocals && index == program.EntryPoint;
        bool anyChanged = false;
        int collapsed = 0;
        for (int round = 0; round < 3; round++)
        {
            NativeLiveness live = new(program, index, flow);
            bool roundChanged = false;
            foreach (NativeControlFlow.Block block in flow.Blocks)
            {
                int nextPc = -1;
                for (int pc = block.End - 1; pc >= block.Start; pc--)
                {
                    NativeInstruction ins = code[pc];
                    if (ins.Op == NativeOp.Nop) continue;
                    if (nextPc >= 0 && code[nextPc] is { Op: NativeOp.Move } next
                        && ins.Result >= protectedPrefix && ins.Result == next.A && next.Result != next.A
                        && !addressed.Contains(ins.Result) && !function.Slots[ins.Result].Escapes
                        && (!preserveEntryLocals || function.Slots[ins.Result].Role == NativeSlotRole.Temporary)
                        && !live.After[nextPc].Contains(ins.Result))
                    {
                        int dest = next.Result;
                        if (ins.Op is NativeOp.Move or NativeOp.Copy)
                        {
                            if (NativeTypeRelations.Accepts(program.Types, function.Slots[dest].Type, function.Slots[ins.A].Type))
                            {
                                code[pc] = dest == ins.A ? NativeLocalOptimization.Nop(ins)
                                    : next with { Op = ins.Op, A = ins.A };
                                code[nextPc] = NativeLocalOptimization.Nop(next);
                                roundChanged = true;
                                collapsed++;
                                if (code[pc].Op != NativeOp.Nop) nextPc = pc;
                                continue;
                            }
                        }
                        else if (ins.Op is NativeOp.Constant or NativeOp.Convert or NativeOp.Narrow or NativeOp.Unary
                            or NativeOp.Binary or NativeOp.Load or NativeOp.FrameAddress or NativeOp.AddressOffset
                            or NativeOp.ProjectAddress or NativeOp.Allocate or NativeOp.MakeFunction or NativeOp.MakeType
                            && NativeTypeRelations.Same(program.Types, function.Slots[dest].Type, function.Slots[ins.Result].Type)
                            && !addressed.Contains(dest))
                        {
                            code[pc] = ins with { Result = dest };
                            code[nextPc] = NativeLocalOptimization.Nop(next);
                            roundChanged = true;
                            collapsed++;
                            nextPc = pc;
                            continue;
                        }
                    }
                    nextPc = pc;
                }
            }
            if (!roundChanged) break;
            anyChanged = true;
        }
        if (collapsed > 0) profile?.Trace.Metric("optimizer.collapsed_moves", collapsed);
        return anyChanged;
    }

    private static bool Discardable(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if (instruction.Op is NativeOp.Constant or NativeOp.Copy or NativeOp.FrameAddress or NativeOp.MakeFunction) return true;
        if (instruction.Op == NativeOp.MakeType) return instruction.A < 0;
        if (instruction.Op == NativeOp.Allocate) return instruction.C < 0;
        if (instruction.Op is NativeOp.AddressOffset or NativeOp.ProjectAddress) return true;
        if (instruction.Op == NativeOp.Unary)
        {
            TypeKind kind = program.Types[function.Slots[instruction.A].Type].Kind;
            return (NativeUnary)instruction.Immediate switch
            {
                NativeUnary.IsNull => true,
                NativeUnary.Negate => kind == TypeKind.Number,
                NativeUnary.Not => kind == TypeKind.Bool,
                NativeUnary.IsType => program.Types[function.Slots[instruction.B].Type].Kind == TypeKind.Type,
                _ => false
            };
        }
        if (instruction.Op != NativeOp.Binary) return false;
        TypeKind first = program.Types[function.Slots[instruction.A].Type].Kind;
        TypeKind second = program.Types[function.Slots[instruction.B].Type].Kind;
        NativeBinary operation = (NativeBinary)instruction.Immediate;
        if (first == TypeKind.Number && second == TypeKind.Number)
            return operation <= NativeBinary.GreaterEqual;
        if (first == TypeKind.String && second == TypeKind.String && operation == NativeBinary.Add) return true;
        if (operation is < NativeBinary.Equal or > NativeBinary.GreaterEqual) return false;
        return first == TypeKind.Bool && second == TypeKind.Bool
            || first == TypeKind.Type && second == TypeKind.Type
            || first is TypeKind.String or TypeKind.Path && second is TypeKind.String or TypeKind.Path;
    }
}
