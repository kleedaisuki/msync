using Msync.Core;
using Msync.Core.Native;
using NativeSignature = Msync.Core.NativeSignature;

namespace Msync.Optimization;

internal static class NativeInlining
{
    internal static NativeProgram Run(NativeProgram program, int level, NativeOptimizationProfile? profile)
    {
        if (level < 2) return program;
        int previousCalls;
        int calls = CountCalls(program);
        int[] budgets = Enumerable.Repeat(level == 2 ? 384 : 1536, program.Functions.Count).ToArray();
        do
        {
            previousCalls = calls;
            program = Round(program, level, profile, budgets);
            calls = CountCalls(program);
        } while (calls < previousCalls);
        return program;
    }

    private static int CountCalls(NativeProgram program) => program.Functions.Sum(function =>
        function.Instructions.Count(instruction => instruction.Op == NativeOp.Call));

    private static NativeProgram Round(NativeProgram program, int level, NativeOptimizationProfile? profile, int[] budgets)
    {
        Builder builder = new(program, level == 2 ? 48 : 144, budgets);
        NativeFunctionCode[] functions = program.Functions.ToArray();
        int[][] positions = new int[functions.Length][];
        for (int index = 0; index < functions.Length; index++)
        {
            profile?.Function(index);
            var started = profile?.Start(NativeOptimizationProfile.Pass.Inlining, program.Functions[index]) ?? default;
            (functions[index], positions[index]) = builder.Function(index);
            profile?.Add(NativeOptimizationProfile.Pass.Inlining, started, program.Functions[index], functions[index],
                !ReferenceEquals(functions[index], program.Functions[index]));
        }
        return program with
        {
            Functions = functions,
            Types = builder.Types.ToArray(),
            Layouts = builder.Layouts.ToArray(),
            Allocations = [],
            ExecutionPlans = program.ExecutionPlans?.Select(plan => plan with
            { Start = Boundary(positions[plan.Function], plan.Start), End = Boundary(positions[plan.Function], plan.End) }).ToArray()
        };
    }

    internal static int Boundary(int[] positions, int pc) => pc < positions.Length
        ? positions[pc] : checked(pc - (positions.Length - 1) + positions[^1]);

    private sealed class Builder(NativeProgram program, int bodyBudget, int[] budgets)
    {
        private readonly Dictionary<int, bool> admitted = [];
        private readonly NativeAddressAnalysis.ProgramFacts facts = new(program);
        internal List<NativeCodeType> Types { get; } = program.Types.ToList();
        internal List<NativeCodeLayout> Layouts { get; } = program.Layouts.ToList();

        internal (NativeFunctionCode Function, int[] Positions) Function(int index)
        {
            NativeFunctionCode function = program.Functions[index];
            if (!function.Instructions.Any(instruction => StaticCall(index, instruction)))
                return (function, Enumerable.Range(0, function.Instructions.Count + 1).ToArray());
            NativeAddressAnalysis provenance = facts.Function(index);
            Dictionary<int, NativeCallableOrigin> known = [];
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                NativeInstruction call = function.Instructions[pc];
                if (call.Op != NativeOp.Call) continue;
                IReadOnlyCollection<NativeCallableOrigin>? targets = provenance.CallablesAt(pc, call.A);
                if (targets is { Count: 1 } && targets.Single() is { NativeName: null } source) known.Add(pc, source);
            }
            Dictionary<int, IReadOnlyList<NativeInstruction>> expansions = [];
            List<NativeSlot> slots = function.Slots.ToList();
            int growth = 0;
            foreach ((int pc, NativeCallableOrigin construction) in known.OrderBy(pair => pair.Key))
            {
                NativeInstruction call = function.Instructions[pc];
                if (!Eligible(index, call, construction)) continue;
                NativeFunctionCode target = program.Functions[call.B];
                int cost = target.Instructions.Count(instruction => instruction.Op != NativeOp.Nop)
                    + target.Instructions.Count(instruction => instruction.Op == NativeOp.Return)
                    + target.ParameterCount + (target.CaptureCount == 0 ? 0 : 1 + target.CaptureCount * 3) - 1;
                if (growth + cost > budgets[index]) continue;
                expansions.Add(pc, Expand(call, target, construction, slots));
                growth += cost;
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
            budgets[index] -= growth;
            List<NativeInstruction> code = new(count);
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                if (expansions.TryGetValue(pc, out IReadOnlyList<NativeInstruction>? expansion))
                    code.AddRange(expansion.Select(instruction => NativeCodeRelocation.Targets(instruction, target => positions[pc] + target)));
                else code.Add(NativeCodeRelocation.Targets(function.Instructions[pc], target => positions[target]));
            }
            return (function with { Slots = slots.ToArray(), Instructions = code.ToArray() }, positions);
        }

        private bool Eligible(int caller, NativeInstruction call, NativeCallableOrigin construction)
        {
            if (!StaticCall(caller, call)) return false;
            NativeFunctionCode function = program.Functions[call.B];
            if (function.CaptureCount != program.Functions[construction.Function].CaptureCount) return false;
            if (!CapturesMatch(function, construction)) return false;
            return true;
        }

        private bool StaticCall(int caller, NativeInstruction instruction)
        {
            if (instruction.Op != NativeOp.Call || instruction.B < 0 || instruction.C >= 0 || instruction.B == caller) return false;
            NativeFunctionCode function = program.Functions[instruction.B];
            if (function.ParameterCount != (instruction.Inputs?.Count ?? 0)) return false;
            if (admitted.TryGetValue(instruction.B, out bool allowed)) return allowed;
            allowed = !function.IsTemplate && function.Instructions.Count != 0
                && function.Instructions.Count(instruction => instruction.Op != NativeOp.Nop) <= bodyBudget
                && function.Instructions.All(Transparent)
                && new NativeEscapeAnalysis(program, instruction.B, facts).EscapingLocations.Count == 0;
            admitted.Add(instruction.B, allowed);
            return allowed;
        }

        private bool CapturesMatch(NativeFunctionCode function, NativeCallableOrigin construction)
        {
            for (int index = 0; index < function.CaptureCount; index++)
            {
                NativeCaptureContract expected = function.Signature.Captures[index];
                if (construction.Environment < 0)
                {
                    NativeCaptureContract actual = program.Functions[construction.Function].Signature.Captures[index];
                    if (actual != expected) return false;
                    continue;
                }
                int type = program.Layouts[construction.Environment].Fields[index].Type;
                if (expected.Mode == NativeCaptureMode.Location)
                {
                    if (program.Types[type].Pointee != expected.Type) return false;
                }
                else if (type != expected.Type) return false;
            }
            return true;
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

        private IReadOnlyList<NativeInstruction> Expand(NativeInstruction call, NativeFunctionCode function,
            NativeCallableOrigin construction, List<NativeSlot> slots)
        {
            int first = slots.Count;
            slots.AddRange(function.Slots.Select(slot => slot with { Role = NativeSlotRole.Temporary, Name = null }));
            List<NativeInstruction> code = [];
            Capture(call, function, construction, slots, code, first);
            for (int parameter = 0; parameter < function.ParameterCount; parameter++)
                code.Add(new(NativeOp.Copy, first + function.CaptureCount + parameter, call.Inputs![parameter], Source: call.Source));
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
                    code.Add(new(NativeOp.Copy, call.Result, first + instruction.A, Source: instruction.Source ?? call.Source));
                    code.Add(new(NativeOp.Jump, A: count, Source: instruction.Source ?? call.Source));
                    continue;
                }
                NativeInstruction renamed = NativeCodeRelocation.Registers(instruction, slot => first + slot);
                code.Add(NativeCodeRelocation.Targets(renamed, target => positions[target]));
            }
            return code;
        }

        private void Capture(NativeInstruction call, NativeFunctionCode function, NativeCallableOrigin construction,
            List<NativeSlot> slots, List<NativeInstruction> code, int first)
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
                    code.Add(new(NativeOp.BindLocation, A: first + capture, B: location, Source: call.Source));
                }
                else
                {
                    int value = Register(slots, fieldType);
                    code.Add(new(NativeOp.Load, value, address, Type: fieldType, Source: call.Source));
                    code.Add(new(NativeOp.Copy, first + capture, value, Source: call.Source));
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
