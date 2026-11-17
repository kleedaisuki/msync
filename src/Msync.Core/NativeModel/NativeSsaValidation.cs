using System.Collections;
using Msync.Core.Llvm;

namespace Msync.Core.Native;

internal static class NativeSsaValidation
{
    internal static void ValidateLinks(IReadOnlyList<NativeFunctionCode> functions)
    {
        foreach (NativeFunctionCode function in functions)
        {
            if (function.SsaBody is not { } body) continue;
            foreach (NativeSsaInstruction call in body.Calls)
            {
                if ((uint)call.Target >= (uint)functions.Count
                    || functions[call.Target] is not { CaptureCount: 0, SsaBody: { CanBeCalled: true } callee }
                    || callee.Parameters != call.Arguments!.Length || callee.Result != body.Kinds[call.Out])
                    throw new MsyncException($"Invalid whole-function SSA call target for '{function.Name}'");
                for (int i = 0; i < callee.Parameters; i++)
                    if (callee.Kinds[i] != body.Kinds[call.Arguments[i]])
                        throw new MsyncException($"Invalid whole-function SSA call signature for '{function.Name}'");
            }
        }
    }

    internal static void Validate(NativeSsaBody body, NativeFunctionCode function, IReadOnlyList<NativeCodeType> types)
    {
        void Require(bool condition, string reason)
        {
            if (!condition) throw new MsyncException($"Invalid whole-function SSA for '{function.Name}': {reason}");
        }

        NativeKind? Kind(int id) => (uint)id < (uint)body.Values ? body.Kinds[id] : null;
        NativeKind? SignatureKind(int id) => (uint)id < (uint)types.Count && types[id] is { Pointee: < 0, IsMachine: false } type
            ? type.Kind switch { TypeKind.Number => NativeKind.Number, TypeKind.Bool => NativeKind.Bool, _ => null }
            : null;
        Require(!function.IsTemplate && body.Values > 0 && body.Blocks.Length > 0
            && body.Parameters <= body.Values && body.Parameters == function.ParameterCount
            && body.Parameters == function.Signature.Parameters.Count
            && SignatureKind(function.Signature.Result) == body.Result, "signature");
        Require(body.Kinds.All(kind => kind is NativeKind.Number or NativeKind.Bool), "scalar kinds");

        BitArray entry = new(body.Values);
        int[] owners = new int[body.Values];
        Array.Fill(owners, -1);
        for (int i = 0; i < body.Parameters; i++)
        {
            Require(SignatureKind(function.Signature.Parameters[i]) == Kind(i), "parameter kind");
            Define(i);
            entry[i] = true;
        }
        foreach (NativeSsaLiteral literal in body.Literals)
        {
            Define(literal.Value);
            Require(Kind(literal.Value) != NativeKind.Bool || literal.Bits <= 1, "Boolean literal");
            entry[literal.Value] = true;
        }
        foreach (NativeSsaBlock block in body.Blocks)
        {
            foreach (NativeSsaInstruction op in block.Operations)
            {
                ValidateOperation(op);
                Define(op.Out);
            }
            if (block.True is null) Require(Kind(block.Value) == body.Result && block.False is null, "return kind");
            else if (block.False is not null) Require(Kind(block.Value) == NativeKind.Bool, "branch kind");
        }
        foreach (NativeSsaBlock block in body.Blocks)
        {
            ValidateEdge(block.True);
            ValidateEdge(block.False);
        }

        BitArray?[] incoming = new BitArray?[body.Blocks.Length];
        incoming[0] = entry;
        Queue<int> pending = new();
        bool[] queued = new bool[incoming.Length];
        pending.Enqueue(0);
        while (pending.TryDequeue(out int index))
        {
            queued[index] = false;
            NativeSsaBlock block = body.Blocks[index];
            BitArray ready = After(incoming[index]!, block);
            Merge(ready, block.True);
            Merge(ready, block.False);
        }
        for (int i = 0; i < body.Blocks.Length; i++)
        {
            if (incoming[i] is not { } before) continue;
            NativeSsaBlock block = body.Blocks[i];
            BitArray ready = new(before);
            foreach (NativeSsaInstruction op in block.Operations)
            {
                if (op.Op == LlvmOpcode.Call) Require(op.Arguments!.All(arg => ready[arg]), "undefined call operand");
                else Require(ready[op.A] && (op.B < 0 || ready[op.B]) && (op.C < 0 || ready[op.C]), "undefined operand");
                ready[op.Out] = true;
            }
            if (block.True is null || block.False is not null)
                Require(ready[block.Value], "undefined terminator operand");
            foreach (NativeSsaMove move in block.True?.Moves ?? [])
                Require(ready[move.In], "undefined edge operand");
            foreach (NativeSsaMove move in block.False?.Moves ?? [])
                Require(ready[move.In], "undefined edge operand");
        }

        void Define(int id)
        {
            Require(Kind(id) is not null && owners[id] == -1, "duplicate or invalid definition");
            owners[id] = -2;
        }

        void ValidateEdge(NativeSsaEdge? edge)
        {
            if (edge is null) return;
            Require(edge.Target > 0 && edge.Target < body.Blocks.Length, "edge target");
            HashSet<int> writes = [];
            foreach (NativeSsaMove move in edge.Moves)
            {
                Require(Kind(move.Out) is { } target && Kind(move.In) == target && writes.Add(move.Out), "edge assignment");
                Require(owners[move.Out] == -1 || owners[move.Out] == edge.Target, "edge destination ownership");
                owners[move.Out] = edge.Target;
            }
        }

        void ValidateOperation(NativeSsaInstruction op)
        {
            var (dst, a, b, c) = (Kind(op.Out), Kind(op.A), Kind(op.B), Kind(op.C));
            bool unary = op.B == -1 && op.C == -1;
            bool binary = op.C == -1;
            bool valid = op.Op switch
            {
                LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv =>
                    binary && (dst, a, b) == (NativeKind.Number, NativeKind.Number, NativeKind.Number),
                LlvmOpcode.FNeg => unary && (dst, a) == (NativeKind.Number, NativeKind.Number),
                LlvmOpcode.FCmp => binary && (byte)op.Predicate <= 15
                    && (dst, a, b) == (NativeKind.Bool, NativeKind.Number, NativeKind.Number),
                LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor =>
                    binary && (dst, a, b) == (NativeKind.Bool, NativeKind.Bool, NativeKind.Bool),
                LlvmOpcode.Freeze => unary && dst is not null && a == dst,
                LlvmOpcode.Select => dst is not null && a == NativeKind.Bool && b == dst && c == dst,
                LlvmOpcode.Call => dst is not null && op.A == -1 && unary && op.Arguments is { } args
                    && args.All(id => Kind(id) is not null),
                _ => false
            };
            Require(valid, "operation " + op.Op);
        }

        void Merge(BitArray ready, NativeSsaEdge? edge)
        {
            if (edge is null) return;
            BitArray candidate = new(ready);
            foreach (NativeSsaMove move in edge.Moves) candidate[move.Out] = true;
            if (incoming[edge.Target] is { } old)
            {
                bool changed = false;
                for (int value = 0; value < body.Values; value++)
                {
                    if (!old[value] || candidate[value]) continue;
                    old[value] = false;
                    changed = true;
                }
                if (!changed) return;
            }
            else incoming[edge.Target] = candidate;
            if (queued[edge.Target]) return;
            queued[edge.Target] = true;
            pending.Enqueue(edge.Target);
        }
    }

    private static BitArray After(BitArray before, NativeSsaBlock block)
    {
        BitArray ready = new(before);
        foreach (NativeSsaInstruction op in block.Operations) ready[op.Out] = true;
        return ready;
    }
}
