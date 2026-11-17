using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeSsaCode(NativeCodePage page, int bytes, int registerEntry,
    int callSites, int registerCallSites, int callSpills) : IDisposable
{
    private const int RegisterSlots = 14;
    private readonly NativeCodePage codePage = page;
    private readonly int registerEntryOffset = registerEntry;
    internal int Bytes { get; } = bytes;
    internal int CallSites { get; } = callSites;
    internal int RegisterCallSites { get; } = registerCallSites;
    internal int CallSpills { get; } = callSpills;
    internal bool HasRegisterEntry => registerEntryOffset != 0;

    internal static NativeSsaCode? Compile(NativeSsaBody body, Func<int, (NativeSsaBody Body, NativeSsaCode Code)?> resolve)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        Dictionary<int, CallTarget> targets = [];
        List<NativeCodePage> retained = [];
        bool transferred = false;
        try
        {
            int callSites = 0;
            foreach (NativeSsaInstruction call in body.Calls)
            {
                callSites++;
                if (targets.ContainsKey(call.Target)) continue;
                if (resolve(call.Target) is not { } target) return null;
                nint entry = target.Code.codePage.Retain();
                retained.Add(target.Code.codePage);
                targets.Add(call.Target, new(target.Body, entry + target.Code.registerEntryOffset,
                    target.Code.HasRegisterEntry));
            }
            X64 emitter = new(body, targets);
            byte[] code = emitter.Emit();
            if (NativeCodePage.Create(code, retained) is not { } page) return null;
            transferred = true;
            return new(page, code.Length, emitter.RegisterEntry, callSites, emitter.RegisterCallSites, emitter.CallSpills);
        }
        finally
        {
            if (!transferred)
                foreach (NativeCodePage dependency in retained) dependency.DangerousRelease();
        }
    }

    internal NativeCodePage.Entry Open() => codePage.OpenEntry();
    public void Dispose() => codePage.Dispose();

    private readonly record struct CallTarget(NativeSsaBody Body, nint Entry, bool RegisterAbi);

    private sealed class X64(NativeSsaBody body, IReadOnlyDictionary<int, CallTarget> targets)
    {
        private readonly List<byte> code = [];
        private readonly int[] labels = new int[body.Blocks.Length];
        private readonly List<(int At, int Block)> edges = [];
        private readonly int registers = Math.Min(body.Values, RegisterSlots);
        internal int RegisterEntry { get; private set; }
        internal int RegisterCallSites { get; private set; }
        internal int CallSpills { get; private set; }

        internal byte[] Emit()
        {
            int[][]? liveAtCalls = targets.Count == 0 ? null : LiveAtCalls(body, registers);
            if (body.Values <= RegisterSlots && body.CanBeCalled) EmitRegisterEntry();
            else EmitStateEntry();
            for (int i = 0; i < body.Blocks.Length; i++)
            {
                labels[i] = code.Count;
                NativeSsaBlock block = body.Blocks[i];
                for (int j = 0; j < block.Operations.Length; j++)
                {
                    NativeSsaInstruction op = block.Operations[j];
                    if (op.Op == LlvmOpcode.Call) Call(op, liveAtCalls![i][j]);
                    else Operation(op);
                }
                if (block.True is null)
                {
                    Load(block.Value, 0);
                    if (RegisterEntry == 0) StateReturn();
                    else Bytes(0xc3);
                    continue;
                }
                if (block.False is null) { Edge(i, block.True); continue; }
                Test(block.Value);
                int otherwise = Local(0x84);
                Edge(i, block.True);
                Patch(otherwise, code.Count);
                Edge(i, block.False);
            }
            foreach (var (at, block) in edges) Patch(at, labels[block]);
            return code.ToArray();
        }

        private void EmitStateEntry()
        {
            if (targets.Count != 0) Bytes(0x49, 0x89, 0xc8); // r8: optional native call counters.
            for (int i = 0; i < registers; i++) Memory(0xf3, 0x7e, i + 2, i);
            for (int i = 1; i < body.Blocks.Length; i++)
            {
                Bytes(0x81, 0xfe); Int(i); // esi: resume block; zero enters the body.
                Branch(0x84, i);
            }
        }

        private void EmitRegisterEntry()
        {
            // The host adapter calls the same body used by guest calls.
            for (int i = 0; i < body.Parameters; i++) Memory(0xf3, 0x7e, i + 2, i);
            Bytes(0x57, 0xe8); int call = code.Count; Int(0);
            Bytes(0x5f);
            StateReturn();
            RegisterEntry = code.Count;
            Patch(call, RegisterEntry);
            foreach (NativeSsaLiteral literal in body.Literals)
            {
                LoadLiteral(literal.Bits);
                int reg = literal.Value + 2;
                Bytes(0x66, reg < 8 ? (byte)0x48 : (byte)0x4c, 0x0f, 0x6e, (byte)(0xc0 | ((reg & 7) << 3)));
            }
        }

        private void StateReturn()
        {
            Memory(0x66, 0xd6, 0, 0);
            Bytes(0x31, 0xc0, 0xc3);
        }

        private void Operation(NativeSsaInstruction op)
        {
            if (op.Op == LlvmOpcode.Select)
            {
                Test(op.A);
                int otherwise = Local(0x84);
                Load(op.B, 0);
                int done = Jump();
                Patch(otherwise, code.Count);
                Load(op.C, 0);
                Patch(done, code.Count);
                Write(op.Out, 0);
                return;
            }
            Load(op.A, 0);
            if (op.Op == LlvmOpcode.Freeze) { Write(op.Out, 0); return; }
            if (op.Op == LlvmOpcode.FNeg)
            {
                Bytes(0x48, 0xb8);
                for (int i = 0; i < 7; i++) code.Add(0);
                code.Add(0x80);
                Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc8, 0x66, 0x0f, 0xef, 0xc1);
            }
            else
            {
                Load(op.B, 1);
                switch (op.Op)
                {
                    case LlvmOpcode.FCmp: Compare(op.Predicate); break;
                    case LlvmOpcode.FAdd: Binary(0xf2, 0x58); break;
                    case LlvmOpcode.FSub: Binary(0xf2, 0x5c); break;
                    case LlvmOpcode.FMul: Binary(0xf2, 0x59); break;
                    case LlvmOpcode.FDiv: Binary(0xf2, 0x5e); break;
                    case LlvmOpcode.And: Binary(0x66, 0xdb); break;
                    case LlvmOpcode.Or: Binary(0x66, 0xeb); break;
                    case LlvmOpcode.Xor: Binary(0x66, 0xef); break;
                }
            }
            Write(op.Out, 0);
        }

        private void Call(NativeSsaInstruction op, int live)
        {
            CallTarget target = targets[op.Target];
            int saves = live & ((1 << Math.Min(target.Body.Values, RegisterSlots)) - 1);
            CallSpills += BitOperations.PopCount((uint)saves);
            Bytes(0x4d, 0x85, 0xc0);
            int unobserved = Local(0x84);
            if (target.RegisterAbi) Bytes(0x49, 0xff, 0x40, 0x08);
            else Bytes(0x49, 0xff, 0x00);
            Patch(unobserved, code.Count);
            SaveCallRegisters(saves, 0x66, 0xd6);
            if (target.RegisterAbi)
            {
                RegisterCallSites++;
                MoveArguments(op.Arguments!);
                // Private leaf ABI: rax, rcx and the leaf's XMM lanes are volatile.
                JumpToCall(target.Entry);
            }
            else CallWithState(target, op.Arguments!);
            SaveCallRegisters(saves, 0xf3, 0x7e);
            Write(op.Out, 0);
        }

        private void SaveCallRegisters(int saves, byte prefix, byte op)
        {
            for (uint mask = (uint)saves; mask != 0; mask &= mask - 1)
            {
                int i = BitOperations.TrailingZeroCount(mask);
                Memory(prefix, op, i + 2, i);
            }
        }

        private void MoveArguments(int[] arguments)
        {
            Span<int> sources = stackalloc int[RegisterSlots];
            int pending = 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                sources[i] = arguments[i];
                if (sources[i] != i) pending |= 1 << i;
            }
            while (pending != 0)
            {
                int reads = 0;
                for (uint mask = (uint)pending; mask != 0; mask &= mask - 1)
                    reads |= RegisterBit(sources[BitOperations.TrailingZeroCount(mask)], registers);
                int ready = pending & ~reads;
                if (ready == 0)
                {
                    // Break a parallel-copy cycle without routing it through the stack.
                    int cycle = BitOperations.TrailingZeroCount((uint)pending);
                    Register(1, cycle + 2);
                    for (int i = 0; i < arguments.Length; i++)
                        if (sources[i] == cycle) sources[i] = -1;
                    continue;
                }
                int dst = BitOperations.TrailingZeroCount((uint)ready);
                if (sources[dst] == -1) Register(dst + 2, 1);
                else Load(sources[dst], dst + 2);
                pending &= ~(1 << dst);
            }
        }

        private void CallWithState(CallTarget target, int[] arguments)
        {
            NativeSsaBody callee = target.Body;
            // Preserve caller registers and align the System V stack at the call.
            Bytes(0x57, 0x52, 0x41, 0x50);
            int size = (callee.Values * 8 + 15) & ~15;
            Bytes(0x48, 0x81, 0xec); Int(size);
            for (int i = 0; i < callee.Parameters; i++)
            {
                Load(arguments[i], 0);
                Bytes(0x66, 0x0f, 0xd6, 0x84, 0x24); Int(i * 8);
            }
            foreach (NativeSsaLiteral literal in callee.Literals)
            {
                LoadLiteral(literal.Bits);
                Bytes(0x48, 0x89, 0x84, 0x24); Int(literal.Value * 8);
            }
            // Leaf targets have no backedges and cannot return a resume block.
            Bytes(0x48, 0x89, 0xe7, 0x31, 0xf6, 0x31, 0xd2, 0x31, 0xc9);
            JumpToCall(target.Entry);
            Bytes(0xf3, 0x0f, 0x7e, 0x04, 0x24);
            Bytes(0x48, 0x81, 0xc4); Int(size);
            Bytes(0x41, 0x58, 0x5a, 0x5f);
        }

        private void LoadLiteral(ulong bits) { Bytes(0x48, 0xb8); Long(bits); }
        private void JumpToCall(nint address) { LoadLiteral(unchecked((ulong)address)); Bytes(0xff, 0xd0); }

        private void Compare(LlvmCmpPredicate predicate)
        {
            Binary(0x66, 0x2e); // ucomisd
            switch (predicate)
            {
                case LlvmCmpPredicate.FCmpFalse: Bytes(0x31, 0xc0); break;
                case LlvmCmpPredicate.FCmpTrue: Bytes(0xb8, 1, 0, 0, 0); break;
                case LlvmCmpPredicate.FCmpOeq: Set(0x94, 0x9b, 0x20); break;
                case LlvmCmpPredicate.FCmpOgt: Set(0x97); break;
                case LlvmCmpPredicate.FCmpOge: Set(0x93); break;
                case LlvmCmpPredicate.FCmpOlt: Set(0x92, 0x9b, 0x20); break;
                case LlvmCmpPredicate.FCmpOle: Set(0x96, 0x9b, 0x20); break;
                case LlvmCmpPredicate.FCmpOne: Set(0x95); break;
                case LlvmCmpPredicate.FCmpOrd: Set(0x9b); break;
                case LlvmCmpPredicate.FCmpUno: Set(0x9a); break;
                case LlvmCmpPredicate.FCmpUeq: Set(0x94); break;
                case LlvmCmpPredicate.FCmpUgt: Set(0x97, 0x9a, 0x08); break;
                case LlvmCmpPredicate.FCmpUge: Set(0x93, 0x9a, 0x08); break;
                case LlvmCmpPredicate.FCmpUlt: Set(0x92); break;
                case LlvmCmpPredicate.FCmpUle: Set(0x96); break;
                case LlvmCmpPredicate.FCmpUne: Set(0x95, 0x9a, 0x08); break;
            }
            Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc0);
        }

        private void Set(byte condition, byte extra = 0, byte combine = 0)
        {
            Bytes(0x0f, condition, 0xc0);
            if (extra != 0) Bytes(0x0f, extra, 0xc1, combine, 0xc8);
            Bytes(0x0f, 0xb6, 0xc0);
        }

        private void Edge(int block, NativeSsaEdge target)
        {
            if (target.Moves.Any(move => target.Moves.Any(other => move.Out == other.In)))
            {
                // Preserve simultaneous phi assignments only when moves interfere.
                foreach (NativeSsaMove move in target.Moves)
                {
                    Load(move.In, 0);
                    Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0, 0x50);
                }
                for (int i = target.Moves.Length - 1; i >= 0; i--)
                {
                    Bytes(0x58, 0x66, 0x48, 0x0f, 0x6e, 0xc0);
                    Write(target.Moves[i].Out, 0);
                }
            }
            else
                foreach (NativeSsaMove move in target.Moves)
                {
                    Load(move.In, 0);
                    Write(move.Out, 0);
                }
            if (target.Target > block)
            {
                edges.Add((Jump(), target.Target));
                return;
            }
            Bytes(0x83, 0xea, 1); // edx: remaining backedges before a cancellation poll.
            Branch(0x85, target.Target);
            for (int i = 0; i < registers; i++) Memory(0x66, 0xd6, i + 2, i);
            Bytes(0xb8); Int(target.Target + 1); Bytes(0xc3);
        }

        private void Test(int index)
        {
            Load(index, 0);
            Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0, 0x85, 0xc0);
        }

        private void Load(int value, int reg)
        {
            if (value < registers) Register(reg, value + 2);
            else Memory(0xf3, 0x7e, reg, value);
        }

        private void Write(int value, int reg)
        {
            if (value < registers) Register(value + 2, reg);
            else Memory(0x66, 0xd6, reg, value);
        }

        private void Register(int dst, int src)
        {
            Bytes(0x66);
            int rex = (dst >= 8 ? 4 : 0) | (src >= 8 ? 1 : 0);
            if (rex != 0) Bytes((byte)(0x40 | rex));
            Bytes(0x0f, 0x28, (byte)(0xc0 | ((dst & 7) << 3) | (src & 7)));
        }

        private void Memory(byte prefix, byte op, int reg, int value)
        {
            Bytes(prefix);
            if (reg >= 8) Bytes(0x44);
            Bytes(0x0f, op, (byte)(0x87 | ((reg & 7) << 3)));
            Int(value * sizeof(long));
        }

        private void Binary(byte prefix, byte op) => Bytes(prefix, 0x0f, op, 0xc1);
        private void Branch(byte condition, int target) => edges.Add((Local(condition), target));
        private int Local(byte condition) { Bytes(0x0f, condition); int at = code.Count; Int(0); return at; }
        private int Jump() { Bytes(0xe9); int at = code.Count; Int(0); return at; }
        private void Bytes(params byte[] bytes) => code.AddRange(bytes);
        private void Int(int value)
        {
            for (int i = 0; i < 4; i++) code.Add((byte)(value >> (8 * i)));
        }
        private void Long(ulong value)
        {
            for (int i = 0; i < 8; i++) code.Add((byte)(value >> (8 * i)));
        }
        private void Patch(int at, int target) => BinaryPrimitives.WriteInt32LittleEndian(
            CollectionsMarshal.AsSpan(code).Slice(at, 4), target - at - 4);
    }

    private static int RegisterBit(int value, int registers) => (uint)value < (uint)registers ? 1 << value : 0;

    private static int[][] LiveAtCalls(NativeSsaBody body, int registers)
    {
        int[] incoming = new int[body.Blocks.Length];
        int[][] calls = new int[incoming.Length][];
        for (int i = 0; i < calls.Length; i++)
        {
            NativeSsaInstruction[] ops = body.Blocks[i].Operations;
            calls[i] = ops.Any(op => op.Op == LlvmOpcode.Call) ? new int[ops.Length] : [];
        }
        bool changed;
        do
        {
            changed = false;
            for (int i = incoming.Length - 1; i >= 0; i--)
            {
                NativeSsaBlock block = body.Blocks[i];
                int live = Across(block.True) | Across(block.False);
                if (block.True is null || block.False is not null)
                    live |= RegisterBit(block.Value, registers);
                for (int j = block.Operations.Length - 1; j >= 0; j--)
                {
                    NativeSsaInstruction op = block.Operations[j];
                    live &= ~RegisterBit(op.Out, registers);
                    if (op.Op == LlvmOpcode.Call)
                    {
                        calls[i][j] = live;
                        foreach (int arg in op.Arguments!) live |= RegisterBit(arg, registers);
                    }
                    else live |= RegisterBit(op.A, registers) | RegisterBit(op.B, registers) | RegisterBit(op.C, registers);
                }
                changed |= live != incoming[i];
                incoming[i] = live;
            }
        } while (changed);
        return calls;

        int Across(NativeSsaEdge? edge)
        {
            if (edge is null) return 0;
            int live = incoming[edge.Target];
            // Emission reads every phi source before the edge's parallel definitions take effect.
            foreach (NativeSsaMove move in edge.Moves) live &= ~RegisterBit(move.Out, registers);
            foreach (NativeSsaMove move in edge.Moves) live |= RegisterBit(move.In, registers);
            return live;
        }
    }
}
