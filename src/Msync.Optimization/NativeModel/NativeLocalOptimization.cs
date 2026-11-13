using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeLocalOptimization(NativeProgram program, NativeFunctionCode function,
    NativeInstruction[] code, NativeConstantPool constants, NativeEscapeAnalysis escape)
{
    private readonly record struct Address(int Allocation, int Slot, int Field, int Layout);
    private sealed record Memory(int Source, NativeConstant? Constant, int Type, int Store);
    private readonly Dictionary<int, int> aliases = [];
    private readonly Dictionary<int, NativeConstant> known = [];
    private readonly Dictionary<int, Address> addresses = [];
    private readonly Dictionary<int, Address> bindings = [];
    private readonly Dictionary<Address, Memory> memory = [];
    private readonly HashSet<int> bound = code.Where(item => item.Op == NativeOp.BindLocation).Select(item => item.A).ToHashSet();
    private readonly HashSet<int> addressed = code.Where(item => item.Op == NativeOp.FrameAddress).Select(item => item.A)
        .Concat(code.Where(item => item.Op == NativeOp.BindLocation).Select(item => item.A)).ToHashSet();
    private NativeInstruction? previous;
    private NativeFrameCells frameCells = null!;
    private readonly HashSet<int> initialized = [];
    private int[] stableParameters = [];
    private int revisions;
    private HashSet<int> unusedBindings = [];

    internal bool Run(NativeControlFlow flow)
    {
        frameCells = new(program, function, escape);
        HashSet<int> used = code.SelectMany(instruction => NativeInstructionFacts.Reads(instruction)
            .Concat(instruction.Op == NativeOp.BindLocation ? [] : NativeInstructionFacts.Definitions(instruction))).ToHashSet();
        unusedBindings = bound.Where(slot => !used.Contains(slot) && !function.Slots[slot].Escapes
            && function.Slots[slot].Role != NativeSlotRole.Capture
            && !escape.EscapingLocations.Contains(slot)
            && !(program.PreserveLocals && function.Slots[slot].Role != NativeSlotRole.Temporary)
            && program.SessionRoots?.Contains(slot) != true).ToHashSet();
        HashSet<int> consumed = code.Where(instruction => instruction.Op is NativeOp.Move or NativeOp.Drop)
            .Select(instruction => instruction.A).ToHashSet();
        stableParameters = Enumerable.Range(function.CaptureCount, function.ParameterCount)
            .Where(slot => !consumed.Contains(slot)).ToArray();
        bool changed = false;
        foreach (NativeControlFlow.Block block in flow.Blocks)
        {
            Clear();
            for (int pc = block.Start; pc < block.End; pc++) changed |= Step(pc);
        }
        return changed;
    }

    private bool Step(int pc)
    {
        int previousRevisions = revisions;
        NativeInstruction original = code[pc];
        NativeInstruction instruction = NativeInstructionFacts.RewriteValues(original, Resolve);
        instruction = FrameAccess(instruction);
        instruction = Simplify(instruction);
        if (instruction.Op == NativeOp.Load && addresses.TryGetValue(instruction.A, out Address address)
            && memory.TryGetValue(address, out Memory? stored) && stored.Type == instruction.Type)
            instruction = stored.Constant is { } constant
                ? Constant(instruction, constant) : instruction with { Op = NativeOp.Copy, A = stored.Source, B = -1 };
        ObserveReads(instruction);
        foreach (int result in NativeInstructionFacts.Definitions(instruction)) Invalidate(result, instruction.Op == NativeOp.BindLocation);
        if (NativeInstructionFacts.MemoryBarrier(instruction) && !PrivateDrop(instruction)) Barrier();
        instruction = Track(pc, instruction);
        if (instruction != original) revisions++;
        code[pc] = instruction;
        if (instruction.Op == NativeOp.Drop || instruction.Op == NativeOp.Move && instruction.A != instruction.Result)
            initialized.Remove(instruction.A);
        if (instruction.Result >= 0 && instruction.Op != NativeOp.NextIterator) initialized.Add(instruction.Result);
        previous = instruction;
        return revisions != previousRevisions;
    }

    private NativeInstruction FrameAccess(NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.FrameAddress && bindings.TryGetValue(instruction.A, out Address binding)
            && binding.Field >= 0)
            foreach ((int source, Address target) in addresses)
                if (target == binding && initialized.Contains(source) && frameCells.IsPrivate(source)
                    && function.Slots[source].Type == function.Slots[instruction.Result].Type)
                    return instruction with { Op = NativeOp.Copy, A = source, B = -1, C = -1 };
        // Copy is discardable; checked memory reads may only become Copy when
        // the actual private cell cannot be cleared through another alias.
        if (previous is not { Op: NativeOp.FrameAddress } address
            || instruction.A != address.Result || address.A == address.Result
            || !frameCells.IsPrivate(address.A))
            return instruction;
        NativeSlot pointer = function.Slots[address.Result];
        if (pointer.Role != NativeSlotRole.Temporary || pointer.Escapes || addressed.Contains(address.Result))
            return instruction;
        int type = function.Slots[address.A].Type;
        if (instruction.Op == NativeOp.Load && instruction.Result != address.A
            && initialized.Contains(address.A) && instruction.Type == type && function.Slots[instruction.Result].Type == type)
            return instruction with { Op = NativeOp.Copy, A = address.A, B = -1, C = -1 };
        if (instruction.Op == NativeOp.Store && instruction.B != address.A
            && initialized.Contains(instruction.B) && frameCells.IsPrivate(instruction.B)
            && instruction.Type == type && function.Slots[instruction.B].Type == type)
            return instruction with { Op = NativeOp.Copy, Result = address.A, A = instruction.B, B = -1, C = -1 };
        return instruction;
    }

    private NativeInstruction Simplify(NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.BindLocation && unusedBindings.Contains(instruction.A)
            && initialized.Contains(instruction.B) && frameCells.IsPrivate(instruction.B)
            && addresses.TryGetValue(instruction.B, out Address binding) && binding.Field >= 0)
            return Nop(instruction);
        if (instruction.Op == NativeOp.Copy && instruction.Result == instruction.A) return Nop(instruction);
        if (instruction.Op == NativeOp.Copy && known.TryGetValue(instruction.A, out NativeConstant? value))
            return Constant(instruction, value);
        if (instruction.Op == NativeOp.Allocate && instruction.C >= 0
            && known.TryGetValue(instruction.C, out NativeConstant? count)
            && count is { Kind: NativeKind.Number, Number: 1 }) return instruction with { C = -1 };
        if (instruction.Op == NativeOp.Branch && known.TryGetValue(instruction.A, out NativeConstant? condition)
            && condition.Kind == NativeKind.Bool)
            return instruction with { Op = NativeOp.Jump, A = condition.Boolean ? instruction.B : instruction.C, B = -1, C = -1 };
        if (instruction.Op == NativeOp.Narrow && known.TryGetValue(instruction.A, out NativeConstant? supplied)
            && program.Types[instruction.Type] is { Pointee: < 0 } target && Accepts(target.Kind, supplied.Kind))
            return Constant(instruction, supplied);
        if (instruction.Op == NativeOp.Narrow && function.Slots[instruction.A].Type == instruction.Type)
            return instruction with { Op = NativeOp.Copy, B = -1, C = -1 };
        NativeConstant? typed = TypedUnary(instruction);
        if (typed is not null) return Constant(instruction, typed);
        NativeConstant? folded = NativeConstantFolding.Fold(instruction, known);
        return folded is null ? instruction : Constant(instruction, folded);
    }

    private NativeConstant? TypedUnary(NativeInstruction instruction)
    {
        if (instruction.Op != NativeOp.Unary) return null;
        NativeCodeType source = program.Types[function.Slots[instruction.A].Type];
        if (source.Pointee >= 0 || source.Kind == TypeKind.Never) return null;
        if ((NativeUnary)instruction.Immediate == NativeUnary.IsNull)
        {
            if (source.Kind == TypeKind.Null) return new(NativeKind.Bool, Boolean: true);
            return (source.Kind & TypeKind.Null) == 0 ? new(NativeKind.Bool, Boolean: false) : null;
        }
        if ((NativeUnary)instruction.Immediate != NativeUnary.IsType
            || !known.TryGetValue(instruction.B, out NativeConstant? target)
            || target is not { Kind: NativeKind.Type, Type: not null }) return null;
        if ((source.Kind & TypeKind.Type) != 0) return null;
        if (AcceptsStatic(target.Type, function.Slots[instruction.A].Type)) return new(NativeKind.Bool, Boolean: true);
        return (source.Kind & target.Type.Kind) == 0 ? new(NativeKind.Bool, Boolean: false) : null;
    }

    private bool AcceptsStatic(TypeRef expected, int type)
    {
        while (true)
        {
            NativeCodeType source = program.Types[type];
            if (source.Pointee >= 0 || source.Kind == TypeKind.Never || (expected.Kind & source.Kind) != source.Kind) return false;
            if (expected.Element is null) return true;
            if (source.Element < 0) return false;
            expected = expected.Element; type = source.Element;
        }
    }

    private NativeInstruction Track(int pc, NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.Constant) known[instruction.Result] = constants[instruction.A];
        if (instruction.Op == NativeOp.Copy)
        {
            if (function.Slots[instruction.Result].Type == function.Slots[instruction.A].Type
                && !addressed.Contains(instruction.A) && !function.Slots[instruction.A].Escapes
                && function.Slots[instruction.A].Role != NativeSlotRole.Capture) aliases[instruction.Result] = instruction.A;
            if (addresses.TryGetValue(instruction.A, out Address source)) addresses[instruction.Result] = source;
        }
        if (instruction.Op == NativeOp.Allocate)
            addresses[instruction.Result] = new(pc, -1, -1, instruction.A);
        if (instruction.Op == NativeOp.FrameAddress) FrameAddress(instruction);
        if (instruction.Op == NativeOp.BindLocation) Bind(instruction);
        if (instruction.Op is NativeOp.AddressOffset or NativeOp.ProjectAddress) Project(instruction);
        return instruction.Op == NativeOp.Store ? Store(pc, instruction) : instruction;
    }

    private void FrameAddress(NativeInstruction instruction)
    {
        if (bindings.TryGetValue(instruction.A, out Address binding)) addresses[instruction.Result] = binding;
        else if (!bound.Contains(instruction.A) && function.Slots[instruction.A].Role != NativeSlotRole.Capture)
            addresses[instruction.Result] = new(-1, instruction.A, -1, -1);
    }

    private void Bind(NativeInstruction instruction)
    {
        bindings.Remove(instruction.A);
        if (addresses.TryGetValue(instruction.B, out Address address)) bindings[instruction.A] = address;
    }

    private bool PrivateDrop(NativeInstruction instruction) => instruction.Op == NativeOp.Drop
        && function.Slots[instruction.A].Role == NativeSlotRole.Temporary
        && !function.Slots[instruction.A].Escapes && !addressed.Contains(instruction.A);

    private void Project(NativeInstruction instruction)
    {
        if (!addresses.TryGetValue(instruction.A, out Address source) || source.Allocation < 0 || source.Field >= 0) return;
        int field = instruction.Op == NativeOp.AddressOffset && instruction.B == source.Layout ? instruction.C : -1;
        if (instruction.Op == NativeOp.ProjectAddress)
            foreach (NativeProjectionCase alternative in program.Projections[instruction.B].Cases)
                if (alternative.Layout == source.Layout) { field = alternative.Field; break; }
        if (field >= 0) addresses[instruction.Result] = source with { Field = field };
    }

    private NativeInstruction Store(int pc, NativeInstruction instruction)
    {
        if (!addresses.TryGetValue(instruction.A, out Address address)) { Clear(); return instruction; }
        NativeConstant? value = known.GetValueOrDefault(instruction.B);
        if (memory.TryGetValue(address, out Memory? previous) && previous.Type == instruction.Type)
        {
            bool same = value is not null && previous.Constant is not null
                ? NativeConstantPool.Same(value, previous.Constant) : previous.Source == instruction.B;
            if (same && instruction.C == 0) return Nop(instruction);
            if (Private(address) && previous.Store >= 0 && frameCells.IsPrivate(previous.Source))
            {
                NativeInstruction old = code[previous.Store];
                code[previous.Store] = Nop(old);
                revisions++;
                if (old.C != 0) instruction = instruction with { C = old.C };
            }
        }
        if (address.Slot >= 0) Invalidate(address.Slot);
        foreach (int slot in bindings.Where(pair => pair.Value == address).Select(pair => pair.Key).ToArray()) Invalidate(slot);
        memory[address] = new(instruction.B, value, instruction.Type, pc);
        return instruction;
    }

    private bool Private(Address address) => address.Allocation >= 0
        ? !escape.EscapingAllocations.Contains(address.Allocation)
        : !function.Slots[address.Slot].Escapes && !escape.EscapingLocations.Contains(address.Slot)
            && function.Slots[address.Slot].Role != NativeSlotRole.Capture;

    private void ObserveReads(NativeInstruction instruction)
    {
        foreach (int source in NativeInstructionFacts.ValueReads(instruction))
        {
            if (bindings.TryGetValue(source, out Address binding) && memory.TryGetValue(binding, out Memory? value))
                memory[binding] = value with { Store = -1 };
            Address frame = new(-1, source, -1, -1);
            if (memory.TryGetValue(frame, out Memory? stored)) memory[frame] = stored with { Store = -1 };
        }
    }

    private int Resolve(int value) => aliases.GetValueOrDefault(value, value);

    private void Invalidate(int result, bool rebind = false)
    {
        aliases.Remove(result); known.Remove(result); addresses.Remove(result);
        foreach (int alias in aliases.Where(pair => pair.Value == result).Select(pair => pair.Key).ToArray()) aliases.Remove(alias);
        foreach (Address address in memory.Where(pair => !rebind && pair.Key.Slot == result
            || pair.Value.Source == result && pair.Value.Constant is null).Select(pair => pair.Key).ToArray()) memory.Remove(address);
        if (!rebind && bindings.TryGetValue(result, out Address binding)) memory.Remove(binding);
        if (!rebind && bound.Contains(result) && !bindings.ContainsKey(result)) memory.Clear();
    }

    private void Clear()
    {
        aliases.Clear(); known.Clear(); addresses.Clear(); bindings.Clear(); memory.Clear(); previous = null;
        initialized.Clear(); initialized.UnionWith(stableParameters);
    }

    private void Barrier()
    {
        memory.Clear();
        foreach (int slot in Enumerable.Range(0, function.Slots.Count).Where(ExternallyWritable))
        { aliases.Remove(slot); known.Remove(slot); addresses.Remove(slot); }
        foreach (int alias in aliases.Where(pair => ExternallyWritable(pair.Value)).Select(pair => pair.Key).ToArray()) aliases.Remove(alias);
    }

    private bool ExternallyWritable(int slot) => addressed.Contains(slot) || function.Slots[slot].Escapes
        || function.Slots[slot].Role == NativeSlotRole.Capture;

    private NativeInstruction Constant(NativeInstruction instruction, NativeConstant value) =>
        new(NativeOp.Constant, instruction.Result, constants.Add(value), Type: instruction.Type, Source: instruction.Source);

    internal static NativeInstruction Nop(NativeInstruction instruction) => new(NativeOp.Nop, Source: instruction.Source);

    private static bool Accepts(TypeKind type, NativeKind value) => (type & NativeValue.TypeOfKind(value).Kind) != 0;

}

internal sealed class NativeConstantPool
{
    private readonly record struct Key(NativeKind Kind, string? Text, ulong NumberBits,
        bool Boolean, TypeRef? Type, ConnectionKind ConnectionKind, NativePath? Path, int OriginId, int OriginOffset);
    private readonly List<NativeConstant> values;
    private readonly Dictionary<Key, int> indices = [];
    internal NativeConstantPool(IEnumerable<NativeConstant> initial)
    {
        values = initial.ToList();
        for (int index = 0; index < values.Count; index++) indices.TryAdd(Identity(values[index]), index);
    }
    internal NativeConstant this[int index] => values[index];
    internal IReadOnlyList<NativeConstant> Values => values;
    internal int Add(NativeConstant value)
    {
        Key key = Identity(value);
        if (indices.TryGetValue(key, out int found)) return found;
        int index = values.Count;
        values.Add(value); indices.Add(key, index); return index;
    }
    internal static bool Same(NativeConstant left, NativeConstant right) => left.Kind == right.Kind
        && left.Text == right.Text && left.Boolean == right.Boolean && left.Type == right.Type
        && left.ConnectionKind == right.ConnectionKind && left.Path == right.Path
        && left.OriginId == right.OriginId && left.OriginOffset == right.OriginOffset
        && BitConverter.DoubleToUInt64Bits(left.Number) == BitConverter.DoubleToUInt64Bits(right.Number);
    private static Key Identity(NativeConstant value) => new(value.Kind, value.Text,
        BitConverter.DoubleToUInt64Bits(value.Number), value.Boolean, value.Type, value.ConnectionKind, value.Path, value.OriginId, value.OriginOffset);
}
