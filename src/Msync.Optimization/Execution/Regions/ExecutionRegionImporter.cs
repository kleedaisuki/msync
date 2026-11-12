using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Msync.Optimization.Execution;

/// <summary>Projects canonical stack bytecode without interpreting or rewriting it.</summary>
public static class ExecutionRegionImporter
{
    /// <summary>
    /// Imports only reachable blocks of a bound function. A rejected import
    /// leaves the ordinary interpreter and its artifact contract unchanged.
    /// </summary>
    public static bool TryImport(Module image, int functionId, [NotNullWhen(true)] out ExecutionRegion? region,
        out ExecutionRegionFailure failure)
    {
        region = null;
        if (image.Stage != ArtifactStage.Executable)
        {
            failure = new(ExecutionRegionFailureKind.UnboundImage);
            return false;
        }
        if ((uint)functionId >= (uint)image.Functions.Count)
        {
            failure = new(ExecutionRegionFailureKind.InvalidFunction);
            return false;
        }
        var function = image.Functions[functionId];
        if (InvalidTarget(function.Instructions) is int pc)
        {
            failure = new(ExecutionRegionFailureKind.InvalidTarget, pc);
            return false;
        }
        return new Import(image, function, functionId).Run(out region, out failure);
    }

    private static int? InvalidTarget(IReadOnlyList<Instruction> instructions)
    {
        for (int pc = 0; pc < instructions.Count; pc++)
        {
            var instruction = instructions[pc];
            int target = instruction.Op switch
            {
                OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady => instruction.A,
                OpCode.IteratorNext => instruction.B,
                _ => pc
            };
            if ((uint)target > (uint)instructions.Count) return pc;
        }
        return null;
    }

    private sealed class Import(Module module, FunctionCode function, int functionId)
    {
        private readonly ControlFlowGraph graph = new(function);
        private readonly List<ExecutionRegion.StackNode> stack = [default];
        private int values;

        internal bool Run([NotNullWhen(true)] out ExecutionRegion? region, out ExecutionRegionFailure failure)
        {
            region = null;
            failure = default;
            var depths = new int[graph.Blocks.Count];
            Array.Fill(depths, -1);
            var blocks = new ExecutionBlock?[depths.Length];
            if (depths.Length != 0) depths[0] = 0;
            foreach (int block in graph.ReversePostOrder)
            {
                if (!Lower(block, depths[block], out var lowered, out failure)) return false;
                blocks[block] = lowered;
                if (Propagate(block, lowered.Successors, depths) is int conflict)
                {
                    failure = new(ExecutionRegionFailureKind.StackMergeMismatch, conflict);
                    return false;
                }
            }
            region = new(functionId, function.Instructions.Count, values,
                [.. blocks.OfType<ExecutionBlock>()], [.. stack]);
            return true;
        }

        private int? Propagate(int block, ImmutableArray<ExecutionEdge> edges, int[] depths)
        {
            var outgoing = graph.OutgoingEdges(block);
            for (int i = 0; i < outgoing.Length; i++)
            {
                int target = outgoing[i].Target;
                if (target < 0) continue;
                int depth = edges[i].Arguments.Depth;
                if (depths[target] < 0) depths[target] = depth;
                else if (depths[target] != depth) return edges[i].TargetPc;
            }
            return null;
        }

        private bool Lower(int block, int entryDepth, [NotNullWhen(true)] out ExecutionBlock? lowered,
            out ExecutionRegionFailure failure)
        {
            lowered = null;
            failure = default;
            var range = graph.Blocks[block];
            var parameters = ImmutableArray.CreateBuilder<ExecutionValueId>(entryDepth);
            ExecutionStackState current = default;
            for (int i = 0; i < entryDepth; i++)
            {
                ExecutionValueId parameter = new(values++);
                parameters.Add(parameter);
                current = Push(current, parameter);
            }

            var operations = ImmutableArray.CreateBuilder<ExecutionOperation>(range.End - range.Start);
            for (int pc = range.Start; pc < range.End; pc++)
            {
                if (!LowerOperation(pc, ref current, out var operation, out failure)) return false;
                operations.Add(operation);
            }

            var outgoing = graph.OutgoingEdges(block);
            var edges = ImmutableArray.CreateBuilder<ExecutionEdge>(outgoing.Length);
            foreach (var edge in outgoing)
            {
                int targetPc = edge.Target < 0 ? function.Instructions.Count : graph.Blocks[edge.Target].Start;
                // IteratorNext publishes a value on yield only. Both edges
                // still observe the iterator's canonical local and lifetime.
                var arguments = edge.Kind == ControlFlowEdgeKind.IteratorExhausted ? Pop(current) : current;
                edges.Add(new(EdgeKind(edge.Kind), targetPc, arguments));
            }
            lowered = new(range.Start, parameters.MoveToImmutable(), operations.MoveToImmutable(), edges.MoveToImmutable());
            return true;
        }

        private bool LowerOperation(int pc, ref ExecutionStackState current,
            [NotNullWhen(true)] out ExecutionOperation? operation, out ExecutionRegionFailure failure)
        {
            operation = null;
            failure = default;
            var instruction = function.Instructions[pc];
            if (!ExecutionStackEffect.TryGet(module, instruction, current.Depth, out var effect))
            {
                failure = new(ExecutionRegionFailureKind.UnsupportedOperation, pc);
                return false;
            }
            if (effect.Inputs > current.Depth)
            {
                failure = new(ExecutionRegionFailureKind.StackUnderflow, pc);
                return false;
            }

            var before = current;
            var inputs = ReadInputs(ref current, effect.Inputs);
            ExecutionValueId? result = null;
            if (instruction.Op == OpCode.Dup)
            {
                // Reuse the old prefix, then add one occurrence of the value.
                current = Push(before, inputs[0]);
            }
            else if (effect.ProducesValue)
            {
                result = new ExecutionValueId(values++);
                current = Push(current, result.Value);
            }

            // The interpreter can consume the next Pop together with a
            // Transfer, depending on live operands. No independently
            // executable region op may enter that pair.
            bool dispatch = instruction.Op is OpCode.Transfer or OpCode.LocalTransfer;
            operation = new(pc, instruction, inputs, result, before, dispatch)
            { Numeric = ExecutionNumericOperation.Bind(instruction) };
            return true;
        }

        private ImmutableArray<ExecutionValueId> ReadInputs(ref ExecutionStackState current, int count)
        {
            if (count == 0) return [];
            var inputs = ImmutableArray.CreateBuilder<ExecutionValueId>(count);
            inputs.Count = count;
            for (int i = count - 1; i >= 0; i--)
            {
                inputs[i] = stack[current.Node].Value;
                current = Pop(current);
            }
            return inputs.MoveToImmutable();
        }

        private ExecutionStackState Push(ExecutionStackState current, ExecutionValueId value)
        {
            int node = stack.Count;
            stack.Add(new(current.Node, value));
            return new(node, current.Depth + 1);
        }

        private ExecutionStackState Pop(ExecutionStackState current) =>
            new(stack[current.Node].Parent, current.Depth - 1);

        private static ExecutionEdgeKind EdgeKind(ControlFlowEdgeKind edge) => edge switch
        {
            ControlFlowEdgeKind.Jump => ExecutionEdgeKind.Jump,
            ControlFlowEdgeKind.Taken => ExecutionEdgeKind.Taken,
            ControlFlowEdgeKind.Fallthrough => ExecutionEdgeKind.Fallthrough,
            ControlFlowEdgeKind.IteratorExhausted => ExecutionEdgeKind.IteratorExhausted,
            _ => ExecutionEdgeKind.IteratorYield
        };
    }
}
