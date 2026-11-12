using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

internal sealed partial class NumericLoop
{
    // This is a scratch/compile budget, not a restriction on guest functions.
    private const int MaxRegisters = 64;
    private const int ConstantBase = MaxRegisters;

    internal static NumericLoop? Import(Module image, ExecutionRegion region, int first, int last) =>
        new Importer(image, region, first, last).Import();

    internal static NumericLoop? ImportTail(Module image, ExecutionRegion region, int first, int last) =>
        new Importer(image, region, first, last).ImportTail();

    internal static NumericLoop? ImportSource(Module image, ExecutionRegion region) =>
        new Importer(image, region, 0, region.ExitPc - 1, sourceCall: true).ImportSource();

    private sealed class Importer(Module image, ExecutionRegion region, int first, int last, bool sourceCall = false)
    {
        private readonly List<int> locals = [];
        private readonly List<int> constants = [];
        private readonly List<Operation> output = [];
        private readonly int[] operands = new int[region.ValueCount];
        private readonly Dictionary<int, int> entries = [];
        private readonly Dictionary<int, int> exits = [];
        private int temporaryCount;
        private int nextSource;
        private int blockOutput;
        private ulong written;
        private int prefix;
        private int entryDepth;
        private ulong entryNumbers;
        private Dictionary<int, int>? entryValues;

        internal NumericLoop? Import()
        {
            if (region.BlockAt(first) is not { } entry || entry.StartPc != first
                || region.BlockAt(last) is not { } tail || tail.Operations[^1].Pc != last) return null;
            foreach (var block in region.Blocks)
            {
                if (block.StartPc < first) continue;
                if (block.StartPc > last) break;
                if (!LowerBlock(block)) return null;
            }
            if (written == 0 || output.Count == 0 || output[^1].Kind != Kind.Jump) return null;
            entryDepth = entry.Parameters.Length;
            entryNumbers = entryDepth == MaxRegisters ? ulong.MaxValue : (1UL << entryDepth) - 1;
            return Finish();
        }

        internal NumericLoop? ImportTail()
        {
            if (region.BlockAt(first) is not { } block || block != region.BlockAt(last)) return null;
            var code = block.Operations;
            int start = first - block.StartPc, end = last - block.StartPc;
            var initial = code[start].StackBefore;
            // Keep any untouched outer operands in the managed stack, not in
            // the numeric ABI. A consumed but unused value need not be a Number.
            prefix = initial.Depth;
            for (int i = start; i <= end; i++)
                prefix = Math.Min(prefix, code[i].StackBefore.Depth - code[i].Inputs.Length);
            entryDepth = initial.Depth - prefix;
            if (entryDepth > MaxRegisters) return null;
            entryValues = [];
            region.CopyStack(initial, StackScratch(initial.Depth));
            for (int i = 0; i < entryDepth; i++)
            {
                int value = activeStack[prefix + i].Index;
                if (entryValues.TryAdd(value, i)) operands[value] = Temporary(i);
            }
            nextSource = first;
            for (int i = start; i <= end; i++)
                if (!Lower(block, ref i, default)) return null;

            ExecutionStackState exit;
            if (end + 1 < code.Length) exit = code[end + 1].StackBefore;
            else if (block.Successors.Length == 1 && block.Successors[0].TargetPc == last + 1)
                exit = block.Successors[0].Arguments;
            else return null;
            if (exit.Depth < prefix || exit.Depth - prefix > MaxRegisters) return null;
            Normalize(exit);
            exits.Add(last + 1, exit.Depth - prefix);
            Emit(last, Kind.Jump, target: last + 1);
            return Finish();
        }

        internal NumericLoop? ImportSource()
        {
            foreach (var block in region.Blocks)
                if (!LowerBlock(block)) return null;
            return exits.Count == 1 ? Finish() : null;
        }

        private ExecutionValueId[] activeStack = new ExecutionValueId[MaxRegisters];
        private Span<ExecutionValueId> StackScratch(int depth)
        {
            if (activeStack.Length < depth) activeStack = new ExecutionValueId[depth];
            return activeStack.AsSpan(0, depth);
        }

        private NumericLoop? Finish()
        {
            int stackBase = locals.Count + constants.Count;
            int registers = stackBase + temporaryCount;
            if (registers > MaxRegisters) return null;

            for (int i = 0; i < output.Count; i++)
            {
                var op = output[i];
                if (op.Kind is Kind.Branch or Kind.Jump)
                {
                    if (entries.TryGetValue(op.Target, out int target)) op = op with { Target = target };
                    else if (exits.ContainsKey(op.Target)) op = op with { Target = ~op.Target };
                    else return null;
                }
                output[i] = op with
                {
                    Left = Register(op.Left, stackBase), Right = Register(op.Right, stackBase),
                    Output = op.Kind is Kind.Branch or Kind.Jump ? op.Output : Register(op.Output, stackBase)
                };
            }
            return new(locals.ToArray(), constants.Select(i => ((NumberValue)image.Constants[i]).Data).ToArray(),
                output.ToArray(), registers, written, prefix, entryDepth, entryNumbers, exits);
        }

        private bool LowerBlock(ExecutionBlock block)
        {
            if (block.Parameters.Length > MaxRegisters) return false;
            var end = block.Operations[^1];
            var edges = block.Successors;
            bool returns = sourceCall && end.Instruction.Op == OpCode.Return;
            if (edges.Length == 0 && !returns) return false;
            var arguments = returns ? end.StackBefore : edges[0].Arguments;
            if (returns)
            {
                if (arguments.Depth != 1) return false;
                exits[region.ExitPc] = 1;
            }
            foreach (var edge in edges)
            {
                // An implicit bytecode return has no explicit native exit.
                if (sourceCall && edge.TargetPc == region.ExitPc) return false;
                if (edge.Arguments != arguments || edge.Arguments.Depth > MaxRegisters) return false;
                if (edge.TargetPc >= first && edge.TargetPc <= last) continue;
                if (exits.TryGetValue(edge.TargetPc, out int depth) && depth != arguments.Depth) return false;
                exits[edge.TargetPc] = arguments.Depth;
            }

            blockOutput = output.Count;
            entries.Add(block.StartPc, blockOutput);
            nextSource = block.StartPc;
            for (int i = 0; i < block.Parameters.Length; i++)
                operands[block.Parameters[i].Index] = Temporary(i);

            for (int i = 0; i < block.Operations.Length; i++)
                if (!Lower(block, ref i, arguments)) return false;
            if (!returns && end.Instruction.Op is not (OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue))
            {
                Normalize(arguments);
                if (nextSource <= end.Pc) Emit(end.Pc, Kind.Nop);
            }
            return true;
        }

        private bool Lower(ExecutionBlock block, ref int index, ExecutionStackState arguments)
        {
            var operation = block.Operations[index];
            var instruction = operation.Instruction;
            if (operation.StackBefore.Depth - prefix > MaxRegisters || operation.RequiresCanonicalDispatch) return false;
            switch (instruction.Op)
            {
                case OpCode.Nop or OpCode.Dup or OpCode.Pop:
                    return true;
                case OpCode.LoadLocal when locals.Count < MaxRegisters:
                    Assign(operation, Intern(locals, instruction.A));
                    return true;
                case OpCode.Constant when image.Constants[instruction.A] is NumberValue:
                    Assign(operation, ConstantBase + Intern(constants, instruction.A));
                    return true;
                case OpCode.StoreLocal or OpCode.InitializeLocal when locals.Count < MaxRegisters:
                    Store(operation, Intern(locals, instruction.A));
                    return true;
                case OpCode.Negate:
                    Calculate(operation, Kind.Negate, Input(operation, 0), 0);
                    return true;
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder:
                    Calculate(operation, Kind.Binary, Input(operation, 0), Input(operation, 1));
                    return true;
                case OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual:
                    return Branch(block, ref index, operation, arguments);
                case OpCode.Return when sourceCall:
                    Normalize(arguments);
                    Emit(operation.Pc, Kind.Jump, target: region.ExitPc);
                    return true;
                case OpCode.Jump:
                    Normalize(arguments);
                    Emit(operation.Pc, Kind.Jump, target: instruction.A);
                    return true;
                default:
                    return false;
            }
        }

        private bool Branch(ExecutionBlock block, ref int index, ExecutionOperation comparison, ExecutionStackState arguments)
        {
            if (index + 1 != block.Operations.Length - 1) return false;
            var branch = block.Operations[index + 1];
            if (branch.Instruction.Op is not (OpCode.JumpIfTrue or OpCode.JumpIfFalse)
                || branch.Inputs[0] != comparison.Result) return false;
            Normalize(arguments);
            Emit(branch.Pc, Kind.Branch, comparison.Instruction.Op, Input(comparison, 0), Input(comparison, 1),
                branch.Instruction.Op == OpCode.JumpIfTrue ? 1 : 0, branch.Instruction.A);
            index++;
            return true;
        }

        private void Store(ExecutionOperation operation, int local)
        {
            int input = Input(operation, 0);
            int live = operation.StackBefore.Depth - prefix - 1;
            written |= 1UL << local;
            if (live == 0 && input < 0 && nextSource == operation.Pc
                && output.Count > blockOutput
                && output[^1] is { Kind: Kind.Binary or Kind.Negate, Output: var previous } && previous == input)
            {
                output[^1] = output[^1] with { Output = local, LastPc = operation.Pc };
                nextSource = operation.Pc + 1;
                return;
            }
            region.CopyStack(operation.StackBefore, StackScratch(operation.StackBefore.Depth));
            for (int i = 0; i < live; i++)
            {
                int value = activeStack[prefix + i].Index;
                if (operands[value] != local) continue;
                int saved = Temporary(i);
                Copy(local, saved);
                operands[value] = saved;
            }
            Emit(operation.Pc, Kind.Move, left: input, result: local);
        }

        private void Normalize(ExecutionStackState arguments)
        {
            region.CopyStack(arguments, StackScratch(arguments.Depth));
            // A temporary resides no higher than the first stack occurrence
            // of its value. High-to-low edge copies preserve duplicated values.
            for (int i = arguments.Depth - prefix - 1; i >= 0; i--)
                Copy(Use(activeStack[prefix + i]), Temporary(i));
        }

        private void Copy(int source, int target)
        {
            if (source != target)
                output.Add(new(Kind.Move, OpCode.Nop, source, 0, target, 0, nextSource, nextSource - 1));
        }

        private void Calculate(ExecutionOperation operation, Kind kind, int left, int right)
        {
            int temporary = Temporary(operation.StackBefore.Depth - prefix - operation.Inputs.Length);
            Assign(operation, temporary);
            Emit(operation.Pc, kind, operation.Instruction.Op, left, right, temporary);
        }

        private int Temporary(int depth)
        {
            temporaryCount = Math.Max(temporaryCount, depth + 1);
            return ~depth;
        }

        private void Assign(ExecutionOperation operation, int operand) => operands[operation.Result!.Value.Index] = operand;
        private int Input(ExecutionOperation operation, int index) => Use(operation.Inputs[index]);

        private int Use(ExecutionValueId value)
        {
            if (entryValues is not null && entryValues.TryGetValue(value.Index, out int slot))
                entryNumbers |= 1UL << slot;
            return operands[value.Index];
        }

        private void Emit(int pc, Kind kind, OpCode opcode = OpCode.Nop, int left = 0, int right = 0, int result = 0, int target = 0)
        {
            output.Add(new(kind, opcode, left, right, result, target, nextSource, pc));
            nextSource = pc + 1;
        }

        private int Register(int id, int stackBase) => id < 0 ? stackBase + ~id
            : id >= ConstantBase ? locals.Count + id - ConstantBase : id;

        private static int Intern(List<int> ids, int id)
        {
            int index = ids.IndexOf(id);
            if (index >= 0) return index;
            ids.Add(id);
            return ids.Count - 1;
        }
    }
}
