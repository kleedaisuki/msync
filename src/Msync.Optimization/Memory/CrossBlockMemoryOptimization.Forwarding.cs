using System.Collections.Immutable;

namespace Msync.Optimization;

internal static partial class CrossBlockMemoryOptimization
{
    private readonly record struct FieldKey(int Origin, int Field);

    private sealed partial class Analysis
    {
        private readonly Dictionary<int, Reference> enumerations = [];

        private static void Remember(Dictionary<int, Reference[]> table, int position, Reference[] values)
        {
            if (!table.TryGetValue(position, out var previous)) { table.Add(position, values); return; }
            for (var index = 0; index < previous.Length; index++) previous[index] = previous[index].Union(values[index]);
        }

        private void RememberStorage(int slot, Reference value)
        {
            stored[slot] = stored.TryGetValue(slot, out var previous) ? previous.Union(value) : value;
            if (value.CellSlot == 0 || value.CellSlot == slot + 1) return;
            var other = value.CellSlot - 1;
            if (!cellAliases.TryGetValue(slot, out var aliases)) cellAliases.Add(slot, aliases = []);
            aliases.Add(other);
            if (!cellAliases.TryGetValue(other, out aliases)) cellAliases.Add(other, aliases = []);
            aliases.Add(slot);
        }

        private Reference StoredValue(Reference reference)
        {
            if (!reference.Cell) return reference;
            if (reference.CellSlot == 0) return Reference.UnknownValue();
            Reference result = default;
            var pending = new Stack<int>(); var seen = new HashSet<int>(); pending.Push(reference.CellSlot - 1);
            while (pending.TryPop(out var slot))
            {
                if (!seen.Add(slot)) continue;
                if (stored.TryGetValue(slot, out var value)) result = result.Union(value with { Cell = false, CellSlot = 0 });
                if (cellAliases.TryGetValue(slot, out var aliases)) foreach (var alias in aliases) pending.Push(alias);
            }
            return result.Kinds == TypeKind.Never ? Reference.UnknownValue() : result;
        }

        private void InvalidateBindings(State state, int except = -1)
        {
            foreach (var slot in cellSlots)
                if (slot != except && state.Locals[slot].Cell)
                    state.Locals[slot] = state.Locals[slot] with { UnknownKinds = TypeKind.Any, Closure = 0, Kinds = TypeKind.Any, Eager = false };
        }

        private Reference Capture(Reference callee, int index)
        {
            if (callee.Closure > 0 && closures.TryGetValue(callee.Closure, out var captures) && index < captures.Length)
                return StoredValue(captures[index]);
            return Reference.UnknownValue();
        }

        private Reference Map(MemoryAccess access, Reference[] arguments)
        {
            var value = access.Root.Kind switch
            {
                MemoryRootKind.Parameter when access.Root.Index + 1 < arguments.Length => arguments[access.Root.Index + 1],
                MemoryRootKind.Capture when arguments.Length > 0 => Capture(arguments[0], access.Root.Index),
                _ => Reference.UnknownValue()
            };
            foreach (var field in access.Fields) value = Child(value, field);
            return value;
        }

        private Reference Child(Reference parent, string field)
        {
            if (parent.Opaque) return Reference.UnknownValue(origins: parent.Origins);
            Reference result = default;
            foreach (var origin in parent.Origins)
            {
                if (!allocations.TryGetValue(origin, out var owner) || !owner.Contents.TryGetValue(field, out var value))
                    return Reference.UnknownValue();
                result = result.Union(value.Cell ? Reference.UnknownValue(origins: value.Origins) : value);
            }
            return result.Kinds == TypeKind.Never ? Reference.UnknownValue() : result;
        }

        private Reference CallResult(int pc, Reference[] arguments)
        {
            if (code[pc].Op == OpCode.Call && code[pc].B < 0)
                return Reference.UnknownValue();
            if (NativeName(arguments[0]) is { } name && NativeSignatures.TryGet(name, out var native))
                return Reference.UnknownValue(native.Result.Kind,
                    eager: EagerNative(name, native, arguments.Skip(1).ToArray()));
            if (!Target(pc, arguments, out var target)) return Reference.UnknownValue();
            var type = module.Functions[target].ReturnType.Kind;
            if (summaries is null || summaries[target].UnknownReturnAlias) return Reference.UnknownValue(type);
            Reference result = default;
            foreach (var alias in summaries[target].ReturnAliases)
            {
                if (alias.Root.Kind == MemoryRootKind.Allocation) return Reference.UnknownValue(type);
                result = result.Union(Map(alias, arguments));
            }
            return result.Kinds == TypeKind.Never
                ? Reference.UnknownValue(type, eager: summaries[target].ReturnsCallbackFreeRange)
                : result with
                {
                    Kinds = result.Kinds | type,
                    UnknownKinds = result.UnknownKinds | (type & ~TypeKind.Scope),
                    Eager = result.Eager || summaries[target].ReturnsCallbackFreeRange
                };
        }

        private bool MayRebind(int pc, Reference[] arguments)
        {
            if (code[pc].Op == OpCode.Call && code[pc].B < 0) return true;
            if (NativeName(arguments[0]) is { } name) return NativeInvokes(name, arguments.Skip(1).ToArray());
            if (summaries is null || !Target(pc, arguments, out var target)) return true;
            var effects = summaries[target];
            return effects.UnknownWrites || effects.Writes.Any(access => access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0)
                || effects.Enumerates.Any(access => MayEnumerate(Map(access, arguments)));
        }

        private static bool MayEnumerate(Reference value) => (value.Kinds & TypeKind.Range) != 0 && !value.Eager;

        private string? NativeName(Reference value) => value.Closure < 0
            && module.Constants[-value.Closure - 1] is FunctionValue function ? function.NativeName : null;

        private bool Target(int pc, Reference[] arguments, out int target)
        {
            if (code[pc].Op == OpCode.Call && code[pc].B < 0) { target = -1; return false; }
            if (targets.TryGetValue(pc, out target)) return true;
            var callee = arguments[0];
            if (callee.Closure == 0 || callee.Function < 0 || code[pc].Op != OpCode.Call) return false;
            if (callee.Closure < 0 && module.Constants[-callee.Closure - 1] is FunctionValue { NativeName: not null }) return false;
            var instruction = code[pc];
            target = instruction.B > 0 ? instruction.B - 1 : instruction.Text is { } dispatch
                ? FunctionDispatch.Targets(dispatch).GetValueOrDefault(callee.Function, callee.Function) : callee.Function;
            return true;
        }

        private static bool NativeInvokes(string? name, Reference[] arguments)
        {
            if ((InstructionEffects.NativeEffects(name) & InstructionEffect.Invoke) == 0) return false;
            if (name is null || !NativeSignatures.TryGet(name, out var signature) || !name.StartsWith("ranges.", StringComparison.Ordinal)) return true;
            return signature.Parameters.Any(type => (type.Kind & TypeKind.Function) != 0)
                || arguments.Any(MayEnumerate);
        }

        private static bool EagerNative(string? name, NativeSignature? signature, Reference[] arguments)
        {
            if (signature?.Result.Kind != TypeKind.Range || NativeInvokes(name, arguments)) return false;
            if (name is "ranges.filter" or "ranges.transform" or "ranges.take_while" or "ranges.drop_while") return false;
            return !arguments.Any(MayEnumerate);
        }

        private void Enumeration(int pc, Reference value) => enumerations[pc] = enumerations.TryGetValue(pc, out var previous) ? previous.Union(value) : value;

        private Dictionary<int, int> DiscoverForwarding()
        {
            var candidates = allocations.Values.Where(allocation => !allocation.SharedFields).ToArray();
            if (candidates.All(allocation => allocation.Promoted)) return [];
            var escaped = candidates.Where(allocation => allocation.Escaped)
                .SelectMany(allocation => Enumerable.Range(0, allocation.Names.Length).Select(field => new FieldKey(allocation.Position, field)))
                .ToImmutableHashSet();
            var entries = new ImmutableHashSet<FieldKey>?[blocks.Length]; entries[0] = ImmutableHashSet<FieldKey>.Empty;
            var pending = new Queue<int>(); var waiting = new bool[blocks.Length];
            var validReads = new Dictionary<int, bool>();
            void Queue(int block) { if (!waiting[block]) { waiting[block] = true; pending.Enqueue(block); } }
            void MergeFields(int block, ImmutableHashSet<FieldKey> incoming)
            {
                var previous = entries[block];
                var joined = previous is null ? incoming : previous.Intersect(incoming);
                if (previous is not null && previous.Count == joined.Count) return;
                entries[block] = joined; Queue(block);
            }
            Queue(0);
            while (pending.TryDequeue(out var block))
            {
                waiting[block] = false;
                var known = entries[block]!;
                for (var pc = blocks[block].Start; pc < blocks[block].End; pc++)
                {
                    var instruction = code[pc];
                    if (allocations.TryGetValue(pc, out var allocation) && !allocation.SharedFields)
                    {
                        for (var field = 0; field < allocation.Names.Length; field++) known = known.Add(new(pc, field));
                        continue;
                    }
                    if (receivers.TryGetValue(pc, out var receiver))
                    {
                        if (!allocations.TryGetValue(receiver.Single, out var owner) || owner.SharedFields
                            || ScopeOperands.Ordinal(module, instruction, owner.Layout) is not (>= 0 and var field))
                        {
                            if (instruction.Op == OpCode.StoreField) known = known.Except(escaped);
                            continue;
                        }
                        var key = new FieldKey(owner.Position, field);
                        if (instruction.Op == OpCode.StoreField) known = NeedsStorageCheck(instruction) ? known.Remove(key) : known.Add(key);
                        else validReads[pc] = known.Contains(key) && (!validReads.TryGetValue(pc, out var previous) || previous);
                        continue;
                    }
                    if (instruction.Op == OpCode.Call && calls.TryGetValue(pc, out var arguments))
                        known = Clobber(pc, arguments, known, escaped);
                    else if (instruction.Op == OpCode.StoreStaticField) known = known.Except(escaped);
                    else if (instruction.Op == OpCode.NativeCall && calls.TryGetValue(pc, out var nativeArguments)
                        && NativeInvokes(instruction.Text, nativeArguments)) known = known.Except(escaped);
                    else if (instruction.Op == OpCode.ProcessCall && calls.TryGetValue(pc, out var processArguments)
                        && (processArguments[0].Kinds & ~(TypeKind.Path | TypeKind.String)) != 0) known = known.Except(escaped);
                    else if (enumerations.TryGetValue(pc, out var enumeration) && MayEnumerate(enumeration)) known = known.Except(escaped);
                }
                foreach (var successor in blocks[block].Successors) MergeFields(successor, known);
            }
            return validReads.Where(pair => pair.Value).ToDictionary(pair => pair.Key, pair => receivers[pair.Key].Single);
        }

        private ImmutableHashSet<FieldKey> Clobber(int pc, Reference[] arguments, ImmutableHashSet<FieldKey> known,
            ImmutableHashSet<FieldKey> escaped)
        {
            if (NativeName(arguments[0]) is { } name)
                return NativeInvokes(name, arguments.Skip(1).ToArray()) ? known.Except(escaped) : known;
            if (summaries is null || !Target(pc, arguments, out var target)) return known.Except(escaped);
            var effects = summaries[target];
            if (effects.UnknownWrites || effects.Enumerates.Any(access => MayEnumerate(Map(access, arguments)))) return known.Except(escaped);
            foreach (var access in effects.Writes)
            {
                if (access.Root.Kind == MemoryRootKind.Allocation) continue;
                if (access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0 && !access.Descendants) continue;
                var parent = access.Fields.Count == 0 ? access : new MemoryAccess(access.Root, access.Fields.Take(access.Fields.Count - 1).ToArray());
                var receiver = Map(parent, arguments);
                // A scalar alternative of a captured location cannot alias the
                // receiver of a successful Scope field write.
                var unknown = access.Fields.Count == 0 ? receiver.UnknownKinds : receiver.UnknownKinds & TypeKind.Scope;
                if (unknown != TypeKind.Never) { known = known.Except(escaped); continue; }
                foreach (var origin in receiver.Origins)
                {
                    if (!allocations.TryGetValue(origin, out var owner)) continue;
                    if (access.Descendants || access.Fields.Count == 0)
                    {
                        foreach (var key in ReachableFields(owner)) known = known.Remove(key);
                    }
                    else if (owner.Fields.TryGetValue(access.Fields[^1], out var field)) known = known.Remove(new(origin, field));
                }
            }
            return known;
        }

        private IEnumerable<FieldKey> ReachableFields(Allocation root)
        {
            var seen = new HashSet<int>(); var pending = new Stack<Allocation>(); pending.Push(root);
            while (pending.TryPop(out var owner))
            {
                if (!seen.Add(owner.Position)) continue;
                for (var field = 0; field < owner.Names.Length; field++) yield return new(owner.Position, field);
                foreach (var value in owner.Contents.Values)
                    foreach (var origin in value.Origins)
                        if (allocations.TryGetValue(origin, out var child)) pending.Push(child);
            }
        }
    }
}
