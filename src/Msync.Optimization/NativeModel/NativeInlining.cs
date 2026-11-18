using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;
using NativeSignature = Msync.Core.NativeSignature;

namespace Msync.Optimization;

internal static class NativeInlining
{
    internal static NativeProgram Run(NativeProgram program, int level, NativeOptimizationProfile? profile, int[] budgets)
    {
        NativeAddressAnalysis.ProgramFacts? facts = null;
        return Run(program, level, profile, budgets, ref facts);
    }

    internal static NativeProgram Run(NativeProgram program, int level, NativeOptimizationProfile? profile, int[] budgets,
        ref NativeAddressAnalysis.ProgramFacts? sharedFacts, bool executable = false,
        CancellationToken cancellationToken = default)
    {
        if (level < 2) return program;
        bool changed;
        do { program = Round(program, level, profile, budgets, ref sharedFacts, executable, cancellationToken, out changed); } while (changed);
        return program;
    }

    private static NativeProgram Round(NativeProgram program, int level, NativeOptimizationProfile? profile, int[] budgets,
        ref NativeAddressAnalysis.ProgramFacts? sharedFacts, bool executable, CancellationToken cancellationToken, out bool changed)
    {
        Builder builder = new(program, level == 2 ? 48 : 144, budgets, profile is not null, sharedFacts, executable);
        NativeFunctionCode[] functions = program.Functions.ToArray();
        int[][] positions = new int[functions.Length][];
        List<int>? modified = null;
        for (int index = 0; index < functions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            profile?.Function(index);
            var started = profile?.Start(NativeOptimizationProfile.Pass.Inlining, program.Functions[index]) ?? default;
            (functions[index], positions[index]) = builder.Function(index);
            bool fnChanged = !ReferenceEquals(functions[index], program.Functions[index]);
            if (fnChanged) (modified ??= []).Add(index);
            profile?.Add(NativeOptimizationProfile.Pass.Inlining, started, program.Functions[index], functions[index],
                fnChanged, builder.Statistics);
        }
        changed = builder.Changed;
        sharedFacts = builder.FactsIfCreated;
        if (!changed) return program;
        NativeProgram next = program with
        {
            Functions = functions,
            Types = builder.Types.ToArray(),
            Layouts = builder.Layouts.ToArray(),
            Allocations = executable && modified is not null ? RelocateAllocations(program, functions, positions, modified)
                : program.Allocations.Where(plan => ReferenceEquals(functions[plan.Function], program.Functions[plan.Function])).ToArray(),
            ExecutionPlans = program.ExecutionPlans?.Select(plan => plan with
            { Start = Boundary(positions[plan.Function], plan.Start), End = Boundary(positions[plan.Function], plan.End) }).ToArray()
        };
        if (sharedFacts is not null && modified is not null)
        {
            sharedFacts.Invalidate(next, CollectionsMarshal.AsSpan(modified));
            profile?.Trace.Metric("program_facts.inlining_invalidated", modified.Count);
        }
        return next;
    }

    private static NativeAllocationPlan[] RelocateAllocations(NativeProgram program, NativeFunctionCode[] functions,
        int[][] positions, List<int> modified)
    {
        List<NativeAllocationPlan> plans = program.Allocations.Select(plan =>
            plan with { Instruction = positions[plan.Function][plan.Instruction] }).ToList();
        HashSet<(int Function, int Instruction)> existing = plans.Select(plan => (plan.Function, plan.Instruction)).ToHashSet();
        foreach (int index in modified)
        {
            var instructions = functions[index].Instructions;
            for (int pc = 0; pc < instructions.Count; pc++)
                if (instructions[pc].Op == NativeOp.Allocate && existing.Add((index, pc)))
                    plans.Add(new(pc, instructions[pc].A, Function: index));
        }
        return plans.ToArray();
    }

    internal static int Boundary(int[] positions, int pc) => pc < positions.Length
        ? positions[pc] : checked(pc - (positions.Length - 1) + positions[^1]);

    private enum Decision
    {
        Allowed, Dynamic, Recursive, Arity, Template, Size, Effects, EscapingLocation, CaptureAbi, Budget, UnknownOrigin,
        ReferenceAbi, ReadyScalarControlFlow
    }

    private sealed class Builder(NativeProgram program, int bodyBudget, int[] budgets, bool observed,
        NativeAddressAnalysis.ProgramFacts? sharedFacts, bool executable)
    {
        private readonly Dictionary<int, Decision> admitted = [];
        private readonly bool[] transparent = TransparentFunctions(program);
        private readonly bool?[] usesCaptureCache = new bool?[program.Functions.Count];
        internal bool Changed { get; private set; }
        internal Dictionary<string, ulong>? Statistics { get; private set; }
        internal NativeAddressAnalysis.ProgramFacts? FactsIfCreated => facts;
        private NativeAddressAnalysis.ProgramFacts Facts => facts ??= new(program);
        private NativeAddressAnalysis.ProgramFacts? facts = sharedFacts;
        private void Record(Decision decision)
        {
            if (Statistics is not { } values) return;
            string key = decision == Decision.Allowed ? "inline.expanded" : "inline.rejected." + decision;
            values[key] = values.GetValueOrDefault(key) + 1;
        }
        internal List<NativeCodeType> Types { get; } = program.Types.ToList();
        internal List<NativeCodeLayout> Layouts { get; } = program.Layouts.ToList();

        internal (NativeFunctionCode Function, int[] Positions) Function(int index)
        {
            NativeFunctionCode function = program.Functions[index];
            Statistics = observed ? [] : null;
            List<int>? candidates = null;
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                NativeInstruction call = function.Instructions[pc];
                if (call.Op != NativeOp.Call) continue;
                Decision decision = Admission(index, call);
                if (decision == Decision.Allowed) (candidates ??= []).Add(pc);
                else Record(decision);
            }
            if (candidates is null) return (function, Enumerable.Range(0, function.Instructions.Count + 1).ToArray());
            NativeAddressAnalysis provenance = Facts.Function(index);
            bool[] privateInputs = NativeCallFacts.PrivateSlots(program, index, provenance);
            Dictionary<int, IReadOnlyList<NativeInstruction>> expansions = [];
            List<NativeSlot> slots = function.Slots.ToList();
            int growth = 0;
            foreach (int pc in candidates)
            {
                NativeInstruction call = function.Instructions[pc];
                NativeFunctionCode target = program.Functions[call.B];
                bool usesCaptures = UsesAnyCapture(call.B, target);
                IReadOnlyCollection<NativeCallableOrigin>? targets = provenance.CallablesAt(pc, call.A);
                NativeCallableOrigin? construction = targets is { Count: 1 } single ? single.First() : null;
                if (executable && construction is null)
                {
                    construction = SplitDescriptor(function, target, provenance, privateInputs, pc, call.A);
                    if (construction is not null) Count("inline.split_descriptor_proved");
                }
                if (construction is { NativeName: not null } || executable && construction is null)
                { Record(Decision.UnknownOrigin); continue; }
                if (usesCaptures || executable)
                {
                    if (construction is null) { Record(Decision.UnknownOrigin); continue; }
                    if (target.CaptureCount != program.Functions[construction.Function].CaptureCount || !CapturesMatch(target, construction))
                    { Record(Decision.CaptureAbi); continue; }
                }
                else
                {
                    if (targets is not null && targets.Any(origin => origin.NativeName is not null))
                    { Record(Decision.UnknownOrigin); continue; }
                }
                int cost = target.Instructions.Count(instruction => instruction.Op != NativeOp.Nop)
                    + target.Instructions.Count(instruction => instruction.Op == NativeOp.Return)
                    + target.ParameterCount + (!usesCaptures || target.CaptureCount == 0 ? 0 : 1 + target.CaptureCount * 3) - 1;
                if (growth + cost > budgets[index]) { Record(Decision.Budget); continue; }
                expansions.Add(pc, Expand(call, target, construction, usesCaptures, slots, privateInputs));
                growth += cost;
                if (!usesCaptures && construction is null && Statistics is not null)
                    Statistics["inline.captureless_direct"] = Statistics.GetValueOrDefault("inline.captureless_direct") + 1;
                if (usesCaptures)
                    Count("inline.location_captures", target.Signature.Captures.Count(capture => capture.Mode == NativeCaptureMode.Location));
                Record(Decision.Allowed);
            }
            int[] positions = new int[function.Instructions.Count + 1];
            if (expansions.Count == 0)
            {
                for (int pc = 0; pc < positions.Length; pc++) positions[pc] = pc;
                return (function, positions);
            }
            int count = 0;
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                positions[pc] = count;
                count += expansions.TryGetValue(pc, out IReadOnlyList<NativeInstruction>? expansion) ? expansion.Count : 1;
            }
            positions[^1] = count;
            Changed = true;
            budgets[index] -= growth;
            List<NativeInstruction> code = new(count);
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                if (expansions.TryGetValue(pc, out IReadOnlyList<NativeInstruction>? expansion))
                    code.AddRange(expansion.Select(instruction => NativeCodeRelocation.Targets(instruction, target => positions[pc] + target)));
                else code.Add(NativeCodeRelocation.Targets(function.Instructions[pc], target => positions[target]));
            }
            return (function with { Slots = slots.ToArray(), Instructions = code.ToArray(), Storage = null }, positions);
        }

        private void Count(string key, int count = 1)
        {
            if (Statistics is { } metrics)
                metrics[key] = metrics.GetValueOrDefault(key) + (uint)count;
        }

        private NativeCallableOrigin? SplitDescriptor(NativeFunctionCode caller, NativeFunctionCode target,
            NativeAddressAnalysis provenance, bool[] privateSlots, int pc, int slot)
        {
            int compose = Definition(pc, slot);
            if (compose < 0 || caller.Instructions[compose] is not { Op: NativeOp.ComposeFunction, Inputs: [int code, int environment, int name] }
                || Literal(compose, name)?.Kind != NativeKind.Null
                || Literal(compose, code) is not { Kind: NativeKind.MachineInteger } literal) return null;
            ulong tag = literal.ToMachineValue().AsMachineInteger().GetZExtValue();
            if (tag == 0 || tag > (ulong)program.Functions.Count) return null;
            int identity = (int)(tag - 1);
            if (Literal(compose, environment)?.Kind == NativeKind.Null)
                return target.CaptureCount == 0 ? new(identity, -1, true, -1, compose, null, 0) : null;
            // Layout proof alone does not prove that the descriptor is ready.
            // A local allocation supplies both the physical schema and readiness.
            int producer = Definition(compose, environment);
            if (producer < 0 || caller.Instructions[producer] is not { Op: NativeOp.Allocate, A: int layout }) return null;
            return Layouts[layout].Fields.Count == target.CaptureCount
                ? new(identity, layout, false, -1, compose, null, target.CaptureCount) : null;

            int Definition(int at, int input) => privateSlots[input] ? provenance.SingleDefinitionAt(at, input) : -1;
            NativeConstant? Literal(int at, int input)
            {
                int definition = Definition(at, input);
                return definition >= 0 && caller.Instructions[definition] is { Op: NativeOp.Constant } constant
                    ? program.Constants[constant.A] : null;
            }
        }

        private Decision Admission(int caller, NativeInstruction instruction)
        {
            if (instruction.B < 0 || instruction.C >= 0) return Decision.Dynamic;
            if (executable && program.Types[program.Functions[caller].Slots[instruction.A].Type].Kind != TypeKind.Function)
                return Decision.UnknownOrigin;
            if (instruction.B == caller) return Decision.Recursive;
            NativeFunctionCode function = program.Functions[instruction.B];
            if (function.ParameterCount != (instruction.Inputs?.Count ?? 0)) return Decision.Arity;
            if (admitted.TryGetValue(instruction.B, out Decision result)) return result;
            result = function.IsTemplate || function.Instructions.Count == 0 ? Decision.Template
                : executable && !ScalarAbi(function.Signature) ? Decision.ReferenceAbi
                : executable && function.SsaBody is { Blocks.Length: > 1 } ? Decision.ReadyScalarControlFlow
                : function.Instructions.Count(instruction => instruction.Op is not (NativeOp.Nop or NativeOp.FrameAddress
                    or NativeOp.Copy or NativeOp.Move or NativeOp.Drop or NativeOp.Narrow)) > bodyBudget ? Decision.Size
                : !transparent[instruction.B] ? Decision.Effects
                : new NativeEscapeAnalysis(program, instruction.B, Facts).EscapingLocations.Count != 0 ? Decision.EscapingLocation
                : Decision.Allowed;
            admitted.Add(instruction.B, result);
            return result;
        }

        // Leave reference-carrier helpers independently tierable. Their fixed
        // offsets and bridge groups are more valuable than removing a frame.
        private bool ScalarAbi(Msync.Core.Native.NativeSignature signature) =>
            Scalar(signature.Result) && signature.Parameters.All(Scalar) && signature.Captures.All(capture => Scalar(capture.Type));

        private bool Scalar(int type) => program.Types[type] is { Pointee: < 0, Kind: TypeKind.Number or TypeKind.Bool or TypeKind.Null };

        private bool CapturesMatch(NativeFunctionCode function, NativeCallableOrigin construction)
        {
            for (int index = 0; index < function.CaptureCount; index++)
            {
                NativeCaptureContract expected = function.Signature.Captures[index];
                if (construction.Environment < 0)
                {
                    NativeCaptureContract actual = program.Functions[construction.Function].Signature.Captures[index];
                    if (actual.Mode != expected.Mode || !NativeTypeRelations.Same(program.Types, actual.Type, expected.Type)) return false;
                    continue;
                }
                int type = program.Layouts[construction.Environment].Fields[index].Type;
                if (expected.Mode == NativeCaptureMode.Location)
                {
                    int pointee = program.Types[type].Pointee;
                    if (pointee < 0 || !NativeTypeRelations.Same(program.Types, pointee, expected.Type)) return false;
                }
                else if (!NativeTypeRelations.Same(program.Types, type, expected.Type)) return false;
            }
            return true;
        }

        private static bool[] TransparentFunctions(NativeProgram program)
        {
            List<int>[] callers = Enumerable.Range(0, program.Functions.Count).Select(_ => new List<int>()).ToArray();
            bool[] blocked = new bool[program.Functions.Count];
            Queue<int> pending = new();
            for (int index = 0; index < blocked.Length; index++)
            {
                foreach (NativeInstruction instruction in program.Functions[index].Instructions)
                {
                    if (instruction.Op == NativeOp.Call && instruction.B >= 0 && instruction.C < 0)
                        callers[instruction.B].Add(index);
                    else if (!Transparent(instruction)) blocked[index] = true;
                }
                if (blocked[index]) pending.Enqueue(index);
            }
            while (pending.TryDequeue(out int callee))
                foreach (int caller in callers[callee])
                    if (!blocked[caller]) { blocked[caller] = true; pending.Enqueue(caller); }
            return blocked.Select(value => !value).ToArray();
        }

        private static bool Transparent(NativeInstruction instruction)
        {
            if (instruction.Op == NativeOp.CallNative)
            {
                NativeSignature signature = NativeSignatures.Get(instruction.Symbol!);
                NativeBehavior behavior = signature.Behavior ?? NativeBehavior.None;
                return signature.Effect == NativeEffect.Pure
                    && (behavior & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.Deferred
                        | NativeBehavior.DeferredScalar | NativeBehavior.EagerRange)) == 0;
            }
            return instruction.Op is not (NativeOp.Call or NativeOp.Transfer or NativeOp.ProcessCall
                or NativeOp.OpenIterator or NativeOp.NextIterator or NativeOp.CloseIterator or NativeOp.SelectPath
                or NativeOp.Barrier or NativeOp.Acquire or NativeOp.ExecutePlan or NativeOp.PlanResult
                or NativeOp.TestStatic or NativeOp.StaticAddress or NativeOp.PublishStatic);
        }

        private bool UsesAnyCapture(int id, NativeFunctionCode function)
        {
            if (usesCaptureCache[id] is { } cached) return cached;
            bool used = ComputeUsesAnyCapture(function);
            usesCaptureCache[id] = used;
            return used;
        }

        private static bool ComputeUsesAnyCapture(NativeFunctionCode function)
        {
            int captures = function.CaptureCount;
            if (captures <= 0) return false;
            foreach (NativeInstruction instruction in function.Instructions)
            {
                if (instruction.Result >= 0 && instruction.Result < captures) return true;
                if (instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation or NativeOp.Move or NativeOp.Drop
                    && instruction.A >= 0 && instruction.A < captures) return true;
                NativeOperands fields = NativeInstructionFacts.ValueOperands(instruction);
                if ((fields & NativeOperands.A) != 0 && instruction.A >= 0 && instruction.A < captures) return true;
                if ((fields & NativeOperands.B) != 0 && instruction.B >= 0 && instruction.B < captures) return true;
                if ((fields & NativeOperands.C) != 0 && instruction.C >= 0 && instruction.C < captures) return true;
                if (instruction.Inputs is { } inputs)
                    for (int i = 0; i < inputs.Count; i++)
                        if (inputs[i] >= 0 && inputs[i] < captures) return true;
            }
            return false;
        }

        private IReadOnlyList<NativeInstruction> Expand(NativeInstruction call, NativeFunctionCode function,
            NativeCallableOrigin? construction, bool usesCaptures, List<NativeSlot> slots, bool[] privateInputs)
        {
            bool[] readOnly = NativeCallFacts.ReadOnlyParameters(function, Facts.Function(call.B));
            int[] map = new int[function.Slots.Count];
            for (int index = 0; index < map.Length; index++)
            {
                if (!usesCaptures && index < function.CaptureCount) { map[index] = -1; continue; }
                int parameter = index - function.CaptureCount;
                if (readOnly[index] && privateInputs[call.Inputs![parameter]]
                    && NativeTypeRelations.Same(program.Types, slots[call.Inputs[parameter]].Type, function.Slots[index].Type)) map[index] = call.Inputs[parameter];
                else { map[index] = slots.Count; slots.Add(function.Slots[index] with { Role = NativeSlotRole.Temporary, Name = null }); }
            }
            List<NativeInstruction> code = [];
            if (usesCaptures && construction is not null) Capture(call, function, construction, slots, code, map);
            for (int parameter = 0; parameter < function.ParameterCount; parameter++)
                if (map[function.CaptureCount + parameter] != call.Inputs![parameter])
                    code.Add(new(NativeOp.Copy, map[function.CaptureCount + parameter], call.Inputs[parameter], Source: call.Source));
            int prefix = code.Count;
            int[] positions = new int[function.Instructions.Count + 1];
            int count = prefix;
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                positions[pc] = count;
                count += function.Instructions[pc].Op == NativeOp.Return ? 2 : 1;
            }
            positions[^1] = count;
            foreach (NativeInstruction instruction in function.Instructions)
            {
                if (instruction.Op == NativeOp.Return)
                {
                    code.Add(new(NativeOp.Copy, call.Result, map[instruction.A], Source: instruction.Source ?? call.Source));
                    code.Add(new(NativeOp.Jump, A: count, Source: instruction.Source ?? call.Source));
                    continue;
                }
                NativeInstruction renamed = NativeCodeRelocation.Registers(instruction, slot => map[slot]);
                code.Add(NativeCodeRelocation.Targets(renamed, target => positions[target]));
            }
            return code;
        }

        private void Capture(NativeInstruction call, NativeFunctionCode function, NativeCallableOrigin construction,
            List<NativeSlot> slots, List<NativeInstruction> code, int[] map)
        {
            if (function.CaptureCount == 0) return;
            int layout = construction.Environment >= 0 ? construction.Environment : Environment(construction.Function);
            int environment = Register(slots, Namespace(layout));
            code.Add(new(NativeOp.EnvironmentAddress, environment, call.A, layout, Source: call.Source));
            for (int capture = 0; capture < function.CaptureCount; capture++)
            {
                int fieldType = Layouts[layout].Fields[capture].Type;
                int address = Register(slots, Pointer(fieldType));
                code.Add(new(NativeOp.AddressOffset, address, environment, layout, capture, Source: call.Source));
                if (function.Signature.Captures[capture].Mode == NativeCaptureMode.Location)
                {
                    int location = Register(slots, fieldType);
                    code.Add(new(NativeOp.Load, location, address, Type: fieldType, Source: call.Source));
                    code.Add(new(NativeOp.BindLocation, A: map[capture], B: location, Source: call.Source));
                }
                else
                {
                    code.Add(new(NativeOp.Load, map[capture], address, Type: fieldType, Source: call.Source));
                }
            }
        }

        private int Environment(int function)
        {
            NativeCaptureContract[] captures = program.Functions[function].Signature.Captures.ToArray();
            int layout = Layouts.Count;
            Layouts.Add(new(captures.Select((capture, index) => new NativeCodeField(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                capture.Mode == NativeCaptureMode.Location ? Pointer(capture.Type) : capture.Type)).ToArray()));
            return layout;
        }

        private int Pointer(int type)
        {
            int found = Types.FindIndex(candidate => candidate.Pointee == type);
            if (found >= 0) return found;
            int index = Types.Count; Types.Add(new(TypeKind.Never, Pointee: type)); return index;
        }

        private int Namespace(int layout)
        {
            int index = Types.Count; Types.Add(new(TypeKind.Scope, Layouts: [layout])); return index;
        }

        private static int Register(List<NativeSlot> slots, int type)
        {
            int index = slots.Count; slots.Add(new(type)); return index;
        }
    }
}
