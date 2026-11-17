using Msync.Core.Llvm;

namespace Msync.Core.Native;

internal sealed record NativeSsaBody(NativeKind[] Kinds, int Parameters, NativeKind Result,
    NativeSsaLiteral[] Literals, NativeSsaBlock[] Blocks)
{
    internal const int MaxValues = 256, MaxBlocks = 128;
    internal int Values => Kinds.Length;
    internal IEnumerable<NativeSsaInstruction> Calls => Blocks.SelectMany(block => block.Operations)
        .Where(operation => operation.Op == LlvmOpcode.Call);
    internal bool CanBeCalled
    {
        get
        {
            for (int i = 0; i < Blocks.Length; i++)
            {
                NativeSsaBlock block = Blocks[i];
                if (block.True is { } yes && yes.Target <= i || block.False is { } no && no.Target <= i
                    || block.Operations.Any(op => op.Op == LlvmOpcode.Call)) return false;
            }
            return true;
        }
    }
}

internal readonly record struct NativeSsaLiteral(int Value, ulong Bits);
internal readonly record struct NativeSsaInstruction(LlvmOpcode Op, int Out, int A, int B,
    int C = -1, LlvmCmpPredicate Predicate = default, int Target = -1, int[]? Arguments = null);
internal readonly record struct NativeSsaMove(int Out, int In);
internal sealed record NativeSsaEdge(int Target, NativeSsaMove[] Moves);
internal sealed record NativeSsaBlock(NativeSsaInstruction[] Operations, int Value,
    NativeSsaEdge? True = null, NativeSsaEdge? False = null);
