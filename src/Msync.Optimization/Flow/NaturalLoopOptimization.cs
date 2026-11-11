namespace Msync.Optimization;

internal static class NaturalLoopOptimization
{
    private const int Factor = 4;
    private const int MaxBodyInstructions = 24;
    private const double ExactIntegerLimit = 9_007_199_254_740_992d;

    private readonly record struct BodyPlan(bool ReadsCounter, bool CarryOnStack);

    private sealed record Candidate(int Header, int Body, int Update, int Latch, int Exit,
        double Start, double Step, Instruction Bound, double? KnownBound,
        OpCode Comparison, bool KeepOnTrue, BodyPlan Plan)
    {
        internal OpCode ContinueComparison => Comparison is OpCode.LessEqual or OpCode.Greater
            ? OpCode.LessEqual : OpCode.Less;
        internal double SafeBound => ExactIntegerLimit - Factor * Step;
    }

    private sealed class LocalFacts(FunctionCode function, IReadOnlyList<Value> constants, ControlFlowGraph graph)
    {
        private readonly LocalUse[] uses = Scan(function);

        private struct LocalUse
        {
            internal int FirstStore;
            internal int SecondStore;
            internal bool MayShare;
        }

        private static LocalUse[] Scan(FunctionCode function)
        {
            var uses = new LocalUse[function.LocalCount];
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                var instruction = function.Instructions[pc];
                switch (instruction.Op)
                {
                    case OpCode.LoadCell or OpCode.LoadStorage or OpCode.IteratorNext:
                        uses[instruction.A].MayShare = true;
                        break;
                    case OpCode.StoreLocal or OpCode.InitializeLocal:
                        ref var use = ref uses[instruction.A];
                        if (use.FirstStore == 0) use.FirstStore = pc + 1;
                        else if (use.SecondStore == 0) use.SecondStore = pc + 1;
                        else use.MayShare = true;
                        break;
                }
            }
            return uses;
        }

        internal double? PrivateNumericSeed(BasicBlock preheader, int slot, int update)
        {
            if (slot < function.CaptureCount + function.ParameterCount || uses[slot].MayShare) return null;
            int first = uses[slot].FirstStore - 1, second = uses[slot].SecondStore - 1;
            int seed = first == update ? second : second == update ? first : -1;
            // A constant store in the unique preheader owns the slot before
            // either version of the loop; no cell or other writer can replace it.
            return seed > preheader.Start && seed < preheader.End
                && function.Instructions[seed - 1] is { Op: OpCode.Constant, A: var id }
                && constants[id] is NumberValue number ? number.Data : null;
        }

        internal bool TryInvariantBound(BasicBlock preheader, int slot, out double? known)
        {
            known = null;
            if (uses[slot].FirstStore - 1 <= preheader.Start
                || Math.Max(uses[slot].FirstStore, uses[slot].SecondStore) > preheader.End) return false;
            int store = ValueWriter(slot, preheader.End, graph.BlockAt(preheader.Start));
            if (store <= preheader.Start) return false;
            return ProveBoundValue(store - 1, out known);
        }

        private int ValueWriter(int slot, int before, int useBlock)
        {
            var use = uses[slot];
            if (slot < function.CaptureCount + function.ParameterCount || use.MayShare) return -1;
            int first = use.FirstStore - 1, second = use.SecondStore - 1;
            int store = second >= 0 && second < before ? second : first;
            if (store <= 0 || store >= before) return -1;
            int storeBlock = graph.BlockAt(store);
            if (graph.BlockAt(store - 1) != storeBlock) return -1;
            if (second >= 0 && (storeBlock != useBlock || function.Instructions[store].Op != OpCode.InitializeLocal))
                return -1;
            return graph.Dominates(storeBlock, useBlock) ? store : -1;
        }

        private bool ProveBoundValue(int pc, out double? known)
        {
            known = null;
            var code = function.Instructions;
            bool castInput = false;
            while (pc >= 0)
            {
                var producer = code[pc];
                switch (producer.Op)
                {
                    case OpCode.LoadLocal:
                        // The inliner can initialize an argument from an
                        // earlier private slot, even the same compacted slot.
                        pc = ValueWriter(producer.A, pc, graph.BlockAt(pc)) - 1;
                        break;
                    case OpCode.Constant when constants[producer.A] is NumberValue number:
                        if (!castInput) known = number.Data;
                        return true;
                    case OpCode.Constant when castInput:
                        return constants[producer.A] is StringValue or BoolValue or NullValue or PathValue;
                    case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder or OpCode.Negate:
                        return true;
                    case OpCode.Convert when castInput:
                        return true;
                    case OpCode.NativeCall when castInput && producer is { A: 0, Text: "io.read_line" }:
                        return true;
                    case OpCode.NativeCall when producer is { A: 2, Text: "cast" } && pc >= 2
                        && graph.BlockAt(pc - 2) == graph.BlockAt(pc)
                        && code[pc - 1] is { Op: OpCode.Constant, A: var type }
                        && constants[type] is TypeValue target && target.Data == TypeRef.Number:
                        // A cast can return its input as-is. Its Number type
                        // alone is not evidence that the input was not a cell.
                        castInput = true;
                        pc -= 2;
                        break;
                    default:
                        return false;
                }
            }
            return false;
        }
    }

    internal static FunctionCode Optimize(FunctionCode function, List<Value> constants, ConstantInterner interner,
        int level, FunctionAnalysisContext? analysis = null)
    {
        var code = function.Instructions;
        if (level < 2 || !HasScalarBackedge(code)) return function;
        var graph = analysis?.ControlFlow(function) ?? new ControlFlowGraph(function);
        LocalFacts? locals = null;
        List<Candidate> selected = [];
        foreach (var loop in NaturalLoopAnalysis.Discover(graph, maximumBlocks: 2))
        {
            if (loop.Blocks.Count != 2) continue;
            var candidate = Recognize(code, constants, graph, loop, locals ??= new(function, constants, graph));
            if (candidate is not null) selected.Add(candidate);
        }
        return selected.Count == 0 ? function : Emit(function, selected, interner);
    }

    private static bool HasScalarBackedge(IReadOnlyList<Instruction> code)
    {
        for (int pc = 4; pc < code.Count; pc++)
            if (code[pc] is { Op: OpCode.Jump, A: var target } && target >= 0 && target < pc
                && target + 3 < pc
                && code[target + 2].Op is OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual
                && code[target + 3].Op is OpCode.JumpIfFalse or OpCode.JumpIfTrue)
                return true;
        return false;
    }

    private static Candidate? Recognize(IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants,
        ControlFlowGraph graph, NaturalLoop loop, LocalFacts locals)
    {
        var header = graph.Blocks[loop.Header];
        var latch = graph.Blocks[loop.Latch];
        int test = header.Start;
        int update = latch.End - 5;
        if (header.End != test + 4 || update <= latch.Start || update - latch.Start > MaxBodyInstructions
            || code[test] is not { Op: OpCode.LoadLocal, A: var counter }
            || code[test + 3] is not { Op: OpCode.JumpIfFalse or OpCode.JumpIfTrue } branch
            || code[latch.End - 1] is not { Op: OpCode.Jump, A: var destination } || destination != test)
            return null;

        bool bodyIsTarget = branch.A == latch.Start;
        if (bodyIsTarget == (header.End == latch.Start)) return null;
        bool keepOnTrue = bodyIsTarget == (branch.Op == OpCode.JumpIfTrue);
        var comparison = code[test + 2].Op;
        if (keepOnTrue ? comparison is not (OpCode.Less or OpCode.LessEqual)
            : comparison is not (OpCode.Greater or OpCode.GreaterEqual)) return null;
        if (code[update] is not { Op: OpCode.LoadLocal, A: var induction } || induction != counter
            || code[update + 1] is not { Op: OpCode.Constant, A: var stepId }
            || constants[stepId] is not NumberValue { Data: var step } || !double.IsInteger(step)
            || step is < 1 or > ExactIntegerLimit / Factor
            || code[update + 2].Op != OpCode.Add
            || code[update + 3] is not { Op: OpCode.StoreLocal, A: var target } || target != counter)
            return null;
        var preheader = graph.Blocks[loop.Preheader];
        double? start = locals.PrivateNumericSeed(preheader, counter, update + 3);
        if (start is not double initial || !double.IsInteger(initial) || initial < -ExactIntegerLimit
            || initial > ExactIntegerLimit - Factor * step) return null;
        var bound = code[test + 1];
        if (bound.Op is not (OpCode.Constant or OpCode.LoadLocal)
            || bound.Op == OpCode.LoadLocal && bound.A == counter) return null;
        double? knownBound;
        if (bound.Op == OpCode.Constant)
        {
            if (constants[bound.A] is not NumberValue number) return null;
            knownBound = number.Data;
        }
        else if (!locals.TryInvariantBound(preheader, bound.A, out knownBound)) return null;
        if (AnalyzeBody(code, constants, preheader, latch.Start, update, counter, bound, locals) is not { } plan)
            return null;
        var candidate = new Candidate(test, latch.Start, update, latch.End - 1, bodyIsTarget ? header.End : branch.A,
            initial, step, bound, knownBound, comparison, keepOnTrue, plan);
        return knownBound is double known && !(known <= candidate.SafeBound)
            && CountSmall(candidate) is null ? null : candidate;
    }

    private static BodyPlan? AnalyzeBody(IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants, BasicBlock preheader,
        int start, int end, int counter, Instruction bound, LocalFacts locals)
    {
        int depth = 0, stores = 0;
        bool readsCounter = false;
        for (int pc = start; pc < end; pc++)
        {
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Constant when constants[instruction.A] is NumberValue:
                    depth++;
                    break;
                case OpCode.LoadLocal:
                    readsCounter |= instruction.A == counter;
                    depth++;
                    break;
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder when depth >= 2:
                    depth--;
                    break;
                case OpCode.Negate when depth >= 1:
                    break;
                case OpCode.StoreLocal when depth >= 1 && instruction.A != counter
                    && (bound.Op != OpCode.LoadLocal || instruction.A != bound.A)
                    && IsNonCellProducer(code[pc - 1].Op)
                    && locals.PrivateNumericSeed(preheader, instruction.A, pc) is not null:
                    stores++;
                    depth--;
                    break;
                default:
                    return null;
            }
        }
        int carrySlot = code[end - 1].A;
        bool carry = stores == 1 && code[end - 1].Op == OpCode.StoreLocal
            && code[start] is { Op: OpCode.LoadLocal, A: var source } && source == carrySlot
            && !UsesLocal(code, start + 1, end - 1, carrySlot);
        return depth == 0 && stores != 0 ? new(readsCounter, carry) : null;
    }

    private static bool IsNonCellProducer(OpCode operation) => operation is OpCode.Constant or OpCode.Add
        or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder or OpCode.Negate;

    private static bool UsesLocal(IReadOnlyList<Instruction> code, int start, int end, int slot)
    {
        for (int pc = start; pc < end; pc++)
            if (code[pc] is { Op: OpCode.LoadLocal, A: var local } && local == slot) return true;
        return false;
    }

    private static int? CountSmall(Candidate candidate)
    {
        if (candidate.KnownBound is not double bound) return null;
        double current = candidate.Start;
        for (int count = 0; count <= Factor; count++, current += candidate.Step)
        {
            bool compare = candidate.Comparison switch
            {
                OpCode.Less => current < bound,
                OpCode.LessEqual => current <= bound,
                OpCode.Greater => current > bound,
                _ => current >= bound
            };
            if (compare != candidate.KeepOnTrue) return count;
        }
        return null;
    }

    private static FunctionCode Emit(FunctionCode function, List<Candidate> candidates, ConstantInterner constants)
    {
        var code = function.Instructions;
        var headers = candidates.ToDictionary(loop => loop.Header);
        var latches = candidates.ToDictionary(loop => loop.Latch, loop => loop.Header);
        var positions = new int[code.Count + 1];
        var fallbacks = new Dictionary<int, int>();
        List<(int Index, int Target)> exits = [];
        List<(int Original, int Emitted)> branches = [];
        List<Instruction> output = new(code.Count);
        for (int pc = 0; pc < code.Count; pc++)
        {
            positions[pc] = output.Count;
            if (headers.TryGetValue(pc, out var loop))
                EmitLoop(code, output, loop, constants, fallbacks, exits);
            if (code[pc].Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext)
                branches.Add((pc, output.Count));
            output.Add(code[pc]);
        }
        positions[^1] = output.Count;
        foreach (var (original, emitted) in branches)
        {
            var instruction = output[emitted];
            output[emitted] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                : instruction with { A = latches.TryGetValue(original, out int header) ? fallbacks[header] : positions[instruction.A] };
        }
        foreach (var (index, target) in exits)
            output[index] = output[index] with { A = positions[target] };
        return function with { Instructions = output };
    }

    private static void EmitLoop(IReadOnlyList<Instruction> code, List<Instruction> output, Candidate loop,
        ConstantInterner constants, Dictionary<int, int> fallbacks, List<(int, int)> exits)
    {
        var compare = code[loop.Header + 2];
        var branch = code[loop.Header + 3];
        if (CountSmall(loop) is int count)
        {
            EmitIterations(code, output, loop, count, constants);
            exits.Add((output.Count, loop.Exit));
            output.Add(new(OpCode.Jump, Span: branch.Span));
            fallbacks.Add(loop.Header, output.Count);
            return;
        }
        int boundCheck = -1;
        if (loop.KnownBound is null)
        {
            // Positive integral induction stays exact through every grouped
            // update below this limit. NaN and large bounds use the original
            // comparison, including its possibly nonterminating behavior.
            output.Add(new(OpCode.Constant, constants.Intern(new NumberValue(loop.SafeBound)), Span: compare.Span));
            output.Add(loop.Bound);
            output.Add(compare with { Op = OpCode.GreaterEqual });
            boundCheck = output.Count;
            output.Add(branch with { Op = OpCode.JumpIfFalse });
        }
        int chunk = output.Count;
        // Check the last iteration of the group. Monotonicity proves the
        // preceding iterations enter; the original header handles the tail.
        output.Add(code[loop.Header]);
        output.Add(new(OpCode.Constant, constants.Intern(new NumberValue((Factor - 1) * loop.Step)), Span: compare.Span));
        output.Add(new(OpCode.Add, Span: compare.Span));
        output.Add(loop.Bound);
        output.Add(compare with { Op = loop.ContinueComparison });
        int tailCheck = output.Count;
        output.Add(branch with { Op = OpCode.JumpIfFalse });
        EmitIterations(code, output, loop, Factor, constants);
        output.Add(code[loop.Latch] with { A = chunk });
        fallbacks.Add(loop.Header, output.Count);
        if (boundCheck >= 0) output[boundCheck] = output[boundCheck] with { A = output.Count };
        output[tailCheck] = output[tailCheck] with { A = output.Count };
    }

    private static void EmitIterations(IReadOnlyList<Instruction> code, List<Instruction> output, Candidate loop,
        int count, ConstantInterner constants)
    {
        if (count == 0) return;
        for (int copy = 0; copy < count; copy++)
        {
            // A private, single-store recurrence does not need to publish
            // intermediate values. Its arithmetic stays in original order.
            int start = loop.Body + (loop.Plan.CarryOnStack && copy != 0 ? 1 : 0);
            int end = loop.Update - (loop.Plan.CarryOnStack && copy != count - 1 ? 1 : 0);
            for (int pc = start; pc < end; pc++) output.Add(code[pc]);
            // Preserve the original value and rounding at each observation of
            // the counter. The update is balanced even above a carried value.
            if (loop.Plan.ReadsCounter) EmitUpdate(code, output, loop, 1, constants);
        }
        if (!loop.Plan.ReadsCounter) EmitUpdate(code, output, loop, count, constants);
    }

    private static void EmitUpdate(IReadOnlyList<Instruction> code, List<Instruction> output, Candidate loop,
        int count, ConstantInterner constants)
    {
        output.Add(code[loop.Update]);
        var step = code[loop.Update + 1];
        output.Add(count == 1 ? step : step with { A = constants.Intern(new NumberValue(count * loop.Step)) });
        output.Add(code[loop.Update + 2]);
        output.Add(code[loop.Update + 3]);
    }
}
