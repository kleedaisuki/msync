using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

public static class NativeOptimizer
{
    public static NativeProgram Optimize(NativeProgram program, int level = 2)
        => OptimizeCore(program, level, null);

    public static NativeProgram Optimize(NativeProgram program, int level, CompilationTrace? trace)
    {
        if (trace?.IncludeOptimizerPasses != true) return Optimize(program, level);
        NativeOptimizationProfile profile = new(trace);
        try { return OptimizeCore(program, level, profile); }
        catch (Exception error) { profile.Fail(error); throw; }
        finally { profile.Publish(); }
    }

    private static NativeProgram OptimizeCore(NativeProgram program, int level, NativeOptimizationProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (level is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(level));
        if (level == 0) return program with { OptimizationLevel = level };
        program = NativeInlining.Run(program, level, profile);
        NativeConstantPool constants = new(program.Constants);
        NativeFunctionCode[] functions = program.Functions.ToArray();
        List<NativeAllocationPlan> plans = program.Allocations
            .Where(plan => plan.Placement == NativeAllocationPlacement.Eliminated).ToList();
        for (int function = 0; function < functions.Length; function++)
        {
            profile?.Function(function);
            var state = (program: program with { Functions = functions }, function, constants, plans, profile, level);
            functions[function] = profile is null ? OptimizeFunction(state.program, function, constants, plans, null, level)
                : profile.Trace.MeasureDetailed("optimizer.function", state,
                    static item =>
                    {
                        try { return OptimizeFunction(item.program, item.function, item.constants, item.plans, item.profile, item.level); }
                        catch (Exception error) { item.profile!.Fail(error); throw; }
                    },
                    CompilationSize.Of(functions[function]), CompilationSize.Of,
                    attributes: new Dictionary<string, string>
                    { ["function.name"] = functions[function].Name, ["function.id"] = function.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        NativeProgram optimized = program with
        { Functions = functions, Constants = constants.Values.ToArray(), Allocations = plans.ToArray(), OptimizationLevel = level };
        NativeProgram compact = Compact(optimized, profile);
        return Compact(NativeRegisterAllocation.Run(compact, profile), profile);
    }

    private static NativeProgram Compact(NativeProgram program, NativeOptimizationProfile? profile)
    {
        NativeFunctionCode[] functions = new NativeFunctionCode[program.Functions.Count];
        int[][] positions = new int[functions.Length][];
        bool[][] retained = new bool[functions.Length][];
        for (int index = 0; index < functions.Length; index++)
        {
            NativeFunctionCode original = program.Functions[index];
            profile?.Function(index);
            var started = profile?.Start(NativeOptimizationProfile.Pass.Compact, original) ?? default;
            NativeControlOptimization.Result result = NativeControlOptimization.Compact(original);
            functions[index] = result.Function;
            positions[index] = result.Positions;
            retained[index] = result.Retained;
            profile?.Add(NativeOptimizationProfile.Pass.Compact, started, original, result.Function,
                !original.Instructions.SequenceEqual(result.Function.Instructions));
        }
        NativeAllocationPlan[] allocations = program.Allocations
            .Where(plan => plan.Placement != NativeAllocationPlacement.Eliminated
                && retained[plan.Function][plan.Instruction])
            .Select(plan => plan with { Instruction = positions[plan.Function][plan.Instruction] }).ToArray();
        return program with
        {
            Functions = functions,
            Allocations = allocations,
            ExecutionPlans = program.ExecutionPlans?.Select(plan => plan with
            {
                Start = NativeInlining.Boundary(positions[plan.Function], plan.Start),
                End = NativeInlining.Boundary(positions[plan.Function], plan.End)
            }).ToArray()
        };
    }

    private static NativeFunctionCode OptimizeFunction(NativeProgram program, int index,
        NativeConstantPool constants, List<NativeAllocationPlan> plans, NativeOptimizationProfile? profile, int level)
    {
        NativeFunctionCode original = program.Functions[index];
        if (original.IsTemplate || original.Instructions.Count == 0) return original;
        NativeInstruction[] code = original.Instructions.ToArray();
        NativeFunctionCode function = original with { Instructions = code };
        NativeProgram view = FunctionView(program, index, function);
        bool changed;
        int iteration = 0;
        do
        {
            profile?.Iteration(++iteration);
            var globalStarted = profile?.Start(NativeOptimizationProfile.Pass.GlobalFacts, function) ?? default;
            bool globalChanged = level >= 2 && NativeGlobalFacts.Run(function, code, constants);
            if (level >= 2) profile?.Add(NativeOptimizationProfile.Pass.GlobalFacts, globalStarted, function, globalChanged);
            var started = profile?.Start(NativeOptimizationProfile.Pass.ControlFlow, function) ?? default;
            NativeControlFlow flow = new(code);
            profile?.Add(NativeOptimizationProfile.Pass.ControlFlow, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.EscapeAnalysis, function) ?? default;
            NativeEscapeAnalysis escape = new(view, index);
            profile?.Add(NativeOptimizationProfile.Pass.EscapeAnalysis, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.GlobalMemory, function) ?? default;
            bool memoryChanged = level >= 2 && NativeGlobalMemory.Run(view, index, code, flow, escape);
            if (level >= 2) profile?.Add(NativeOptimizationProfile.Pass.GlobalMemory, started, function, memoryChanged);
            started = profile?.Start(NativeOptimizationProfile.Pass.LocalValues, function) ?? default;
            bool localChanged = new NativeLocalOptimization(view, function, code, constants, escape).Run(flow);
            changed = globalChanged || memoryChanged || localChanged;
            profile?.Add(NativeOptimizationProfile.Pass.LocalValues, started, function, localChanged);
            started = profile?.Start(NativeOptimizationProfile.Pass.EscapeAnalysis, function) ?? default;
            escape = new(view, index);
            profile?.Add(NativeOptimizationProfile.Pass.EscapeAnalysis, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.DeadMemory, function) ?? default;
            bool removedMemory = NativeDeadMemory.Run(view, index, code, escape);
            changed |= removedMemory;
            profile?.Add(NativeOptimizationProfile.Pass.DeadMemory, started, function, removedMemory);
            started = profile?.Start(NativeOptimizationProfile.Pass.ControlFlow, function) ?? default;
            flow = new(code);
            profile?.Add(NativeOptimizationProfile.Pass.ControlFlow, started, function);
            started = profile?.Start(NativeOptimizationProfile.Pass.DeadResults, function) ?? default;
            bool removedResults = RemoveDead(view, index, flow, code, plans);
            changed |= removedResults;
            profile?.Add(NativeOptimizationProfile.Pass.DeadResults, started, function, removedResults);
        } while (changed);

        var finalStarted = profile?.Start(NativeOptimizationProfile.Pass.EscapeAnalysis, function) ?? default;
        NativeEscapeAnalysis finalEscape = new(view, index);
        profile?.Add(NativeOptimizationProfile.Pass.EscapeAnalysis, finalStarted, function);
        finalStarted = profile?.Start(NativeOptimizationProfile.Pass.ControlFlow, function) ?? default;
        function = function with
        {
            Slots = function.Slots.Select((slot, number) =>
            finalEscape.EscapingLocations.Contains(number) ? slot with { Escapes = true } : slot).ToArray()
        };
        view = FunctionView(program, index, function);
        NativeControlFlow finalFlow = new(code);
        profile?.Add(NativeOptimizationProfile.Pass.ControlFlow, finalStarted, function);
        finalStarted = profile?.Start(NativeOptimizationProfile.Pass.LastUseMoves, function) ?? default;
        bool moved = MoveLastUses(view, index, finalFlow, code);
        profile?.Add(NativeOptimizationProfile.Pass.LastUseMoves, finalStarted, function, moved);
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
            foreach (NativeAllocationPlacement placement in Enum.GetValues<NativeAllocationPlacement>())
                profile.Trace.Metric("allocations." + placement, plans.Count(plan => plan.Function == index && plan.Placement == placement));
        }
        return function;
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

    private static bool Discardable(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        if (instruction.Op is NativeOp.Constant or NativeOp.Copy or NativeOp.FrameAddress) return true;
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
