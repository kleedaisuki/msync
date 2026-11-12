using System.Collections.Immutable;

namespace Msync.Optimization.Execution;

/// <summary>An explicit value in a bound execution region, not a guest local slot.</summary>
public readonly record struct ExecutionValueId(int Index);

/// <summary>A region-local handle to the original stack, in evaluation order.</summary>
public readonly record struct ExecutionStackState
{
    internal ExecutionStackState(int node, int depth) => (Node, Depth) = (node, depth);
    internal int Node { get; }
    public int Depth { get; }
}

public enum ExecutionEdgeKind : byte
{
    Jump,
    Taken,
    Fallthrough,
    IteratorExhausted,
    IteratorYield
}

/// <summary>
/// Arguments feed the target block's parameters in order. TargetPc equal to
/// the region's ExitPc means implicit return of the top argument, or Null.
/// </summary>
public readonly record struct ExecutionEdge(ExecutionEdgeKind Kind, int TargetPc, ExecutionStackState Arguments);

/// <summary>
/// Inputs are in original push order, not pop order. Local slots, captures,
/// and plan state remain canonical. An IteratorNext result exists only on its
/// yield edge. Dup aliases its input and has no separately defined result.
/// </summary>
public sealed record ExecutionOperation(int Pc, Instruction Instruction, ImmutableArray<ExecutionValueId> Inputs,
    ExecutionValueId? Result, ExecutionStackState StackBefore, bool RequiresCanonicalDispatch)
{
    public InstructionEffect Effects => InstructionEffects.Effects(Instruction);
    public ExecutionNumericOperation? Numeric { get; init; }
}

public sealed record ExecutionBlock(int StartPc, ImmutableArray<ExecutionValueId> Parameters,
    ImmutableArray<ExecutionOperation> Operations, ImmutableArray<ExecutionEdge> Successors);

/// <summary>
/// An immutable projection of one function in a specific bound code image.
/// It describes stack/value flow, not a proof of purity, types, or ownership.
/// </summary>
public sealed class ExecutionRegion
{
    internal readonly record struct StackNode(int Parent, ExecutionValueId Value);
    private readonly ImmutableArray<StackNode> stack;

    internal ExecutionRegion(int functionId, int exitPc, int valueCount, ImmutableArray<ExecutionBlock> blocks,
        ImmutableArray<StackNode> stack)
        => (FunctionId, ExitPc, ValueCount, Blocks, this.stack) = (functionId, exitPc, valueCount, blocks, stack);

    public int FunctionId { get; }
    public int ExitPc { get; }
    public int ValueCount { get; }
    public ImmutableArray<ExecutionBlock> Blocks { get; }

    public ExecutionBlock? BlockAt(int pc)
    {
        int low = 0, high = Blocks.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            var block = Blocks[middle];
            if (pc < block.StartPc) high = middle;
            else if (pc - block.StartPc >= block.Operations.Length) low = middle + 1;
            else return block;
        }
        return null;
    }

    public ExecutionOperation? OperationAt(int pc) =>
        BlockAt(pc) is { } block ? block.Operations[pc - block.StartPc] : null;

    /// <summary>Copy a stack recipe from this region, bottom first.</summary>
    public void CopyStack(ExecutionStackState state, Span<ExecutionValueId> destination)
    {
        if (destination.Length != state.Depth)
            throw new ArgumentException("The destination must match the stack recipe depth.", nameof(destination));
        int node = state.Node;
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            var entry = stack[node];
            destination[i] = entry.Value;
            node = entry.Parent;
        }
    }
}

public enum ExecutionRegionFailureKind : byte
{
    None,
    UnboundImage,
    InvalidFunction,
    InvalidTarget,
    UnsupportedOperation,
    StackUnderflow,
    StackMergeMismatch
}

public readonly record struct ExecutionRegionFailure(ExecutionRegionFailureKind Kind, int Pc = -1);
