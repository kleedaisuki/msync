using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private void SelectSnapshotRangeConsumers(RangeFields fields, List<LlvmCallInstruction> consumers)
        {
            if (fields.Declarations == 0) return;
            SnapshotFacts? facts = null;
            foreach (var call in consumers)
            {
                LlvmFunction target = call.CalledFunction!;
                if (emptyRangeConsumers?.Contains(target) == true) continue;
                string? name = nativeCalls.GetValueOrDefault(target);
                int first = name == "process.spawn" ? 1 : 0;
                int end = first + (name is "ranges.concat" or "ranges.zip" ? 2 : 1);
                bool followsLocal = SnapshotPolicyConsumer(name) || iteratorCalls.ContainsKey(target);
                for (int index = first; index < end; ++index)
                {
                    var input = call.GetArgument(index);
                    if (fields.IsFieldLoad(input) || followsLocal && input is not LlvmCallInstruction)
                        (facts ??= new(this, fields)).Observe(call, index, input);
                }
            }
            if (facts is null) return;
            facts.Solve();

            var selected = new Dictionary<(LlvmFunction, LlvmSnapshotOperands), LlvmFunction>();
            int reads = 0, operands = 0;
            foreach (var (call, carrier) in facts.Selected())
            {
                var old = call.CalledFunction!;
                if (!selected.TryGetValue((old, carrier), out var target))
                {
                    string name = nativeCalls.GetValueOrDefault(old) ?? "iterator.open";
                    target = LlvmFunction.Create(old.FunctionType, LlvmLinkage.ExternalLinkage, "msync.snapshot." + name, module);
                    if (iteratorCalls.TryGetValue(old, out var iterator)) iteratorCalls.Add(target, iterator);
                    else nativeCalls.Add(target, name);
                    selected.Add((old, carrier), target);
                    (snapshotRangeConsumers ??= []).Add(target, carrier);
                }
                // The target carries the chosen geometry across LLVM rewrites.
                // The actual load and its owner are still ordinary operands.
                call.CalledOperand = target;
                ++reads;
                operands += (carrier.First.HasValue ? 1 : 0) + (carrier.Second.HasValue ? 1 : 0);
            }
            if (trace?.Detail is not { } detail) return;
            detail.Metric("range.fields.snapshot_consumers", reads);
            detail.Metric("range.fields.snapshot_operands", operands);
            detail.Metric("range.fields.snapshot_nodes", facts.Nodes);
            detail.Metric("range.fields.snapshot_edges", facts.Edges);
            detail.Metric("range.fields.snapshot_conflicts", facts.Conflicts);
            detail.Metric("range.fields.snapshot_shape_conflicts", facts.ShapeConflicts);
            detail.Metric("range.fields.snapshot_seedless", facts.Seedless);
            detail.Metric("range.fields.snapshot_transform_sources", facts.TransformSources);
        }

        private sealed partial class RangeFields
        {
            internal bool IsFieldLoad(LlvmValue value) => value is LlvmLoadInstruction load
                && Lanes(load.PointerOperand) is { Length: > 0 };

            internal LlvmFieldCarrier[]? ReadFields(LlvmValue address) => Lanes(address);

            internal List<LlvmValue>? FieldWrites(LlvmFieldCarrier field) =>
                IsGuestField(field) && owner.IsRangeType(owner.metadata.Layouts[field.Layout].Fields[field.Ordinal].Type)
                    ? writes.GetValueOrDefault(field) : null;
        }

        private sealed partial class SnapshotFacts(Lowering owner, RangeFields fields)
        {
            private enum Kind : byte { NoSeed, Known, Conflict }
            private readonly record struct Fact(Kind Kind, LlvmSnapshotCarrier Carrier = default, bool NeedsMaterialize = false);
            private sealed class Node
            {
                internal Fact Fact;
                internal bool ForcesEmpty;
                internal bool ProducesPlan;
                internal bool Materializes;
                internal bool AddsText;
                internal bool MapsEmpty;
                internal List<Node> Users { get; } = [];
            }

            private readonly Dictionary<LlvmValue, Node> values = [];
            private readonly Dictionary<LlvmFieldCarrier, Node> lanes = [];
            private readonly Dictionary<LlvmAllocaInstruction, Node> locals = [];
            private readonly Dictionary<LlvmCallInstruction, (Node? First, Node? Second)> observations = [];
            private readonly Queue<(LlvmValue Value, Node Node)> unbuilt = [];
            private readonly Queue<Node> changed = [];
            private HashSet<LlvmFunction>? indirect;
            private Dictionary<LlvmFunction, LlvmCallInstruction[]>? direct;
            internal int Nodes => values.Count + lanes.Count + locals.Count;
            internal int Edges { get; private set; }
            internal int Conflicts { get; private set; }
            internal int ShapeConflicts { get; private set; }
            internal int Seedless { get; private set; }
            internal int TransformSources { get; private set; }

            internal void Observe(LlvmCallInstruction call, int index, LlvmValue input)
            {
                var sources = observations.GetValueOrDefault(call);
                if (index == 0) sources.First = Value(input);
                else sources.Second = Value(input);
                observations[call] = sources;
            }

            internal IEnumerable<(LlvmCallInstruction, LlvmSnapshotOperands)> Selected()
            {
                foreach (var (call, inputs) in observations)
                {
                    LlvmSnapshotOperands carriers = new(Published(inputs.First), Published(inputs.Second));
                    if (carriers != default) yield return (call, carriers);
                }

                static LlvmSnapshotCarrier? Published(Node? input) =>
                    input?.Fact is { Kind: Kind.Known, NeedsMaterialize: false } fact ? fact.Carrier : null;
            }

            internal void Solve()
            {
                while (unbuilt.TryDequeue(out var item)) Build(item.Value, item.Node);
                Propagate();
                // An unseeded component is not evidence. Excluding it only
                // after the fixed point lets seeded scope clone cycles close.
                foreach (Node node in values.Values.Concat(lanes.Values).Concat(locals.Values))
                {
                    if (node.Fact.Kind != Kind.NoSeed) continue;
                    Merge(node, new(Kind.Conflict));
                    ++Seedless;
                }
                Propagate();
            }

            private Node Value(LlvmValue value)
            {
                if (values.TryGetValue(value, out var node)) return node;
                values.Add(value, node = new());
                unbuilt.Enqueue((value, node));
                return node;
            }

            private Node Lane(LlvmFieldCarrier field)
            {
                if (lanes.TryGetValue(field, out var node)) return node;
                lanes.Add(field, node = new());
                Inputs(node, fields.FieldWrites(field));
                return node;
            }

            private Node Local(LlvmAllocaInstruction alloca)
            {
                if (locals.TryGetValue(alloca, out var node)) return node;
                locals.Add(alloca, node = new());
                bool hasStore = false;
                foreach (var user in alloca.Users)
                {
                    if (user is LlvmLoadInstruction load && load.PointerOperand == alloca) continue;
                    if (user is not LlvmStoreInstruction store || store.PointerOperand != alloca)
                    {
                        Merge(node, new(Kind.Conflict));
                        return node;
                    }
                    Link(Value(store.ValueOperand), node);
                    hasStore = true;
                }
                if (!hasStore) Merge(node, new(Kind.Conflict));
                return node;
            }

            private void Link(Node source, Node target)
            {
                source.Users.Add(target);
                ++Edges;
            }

            private void Inputs(Node node, IEnumerable<LlvmValue>? inputs)
            {
                bool hasInput = false;
                if (inputs is not null)
                    foreach (var value in inputs)
                    {
                        Link(Value(value), node);
                        hasInput = true;
                    }
                if (!hasInput) Merge(node, new(Kind.Conflict));
            }

            private void Propagate()
            {
                while (changed.TryDequeue(out var node))
                    foreach (var target in node.Users) Merge(target, node.Fact);
            }

            private void Merge(Node node, Fact incoming)
            {
                if (node.Fact.Kind == Kind.Conflict) return;
                if (node.Materializes)
                    incoming = incoming with { NeedsMaterialize = false };
                else if (node.ProducesPlan)
                    incoming = incoming with { NeedsMaterialize = true };
                if (incoming.Kind == Kind.Known && node.ForcesEmpty)
                    incoming = incoming with { Carrier = incoming.Carrier with { Empty = true } };
                if (incoming.Kind == Kind.Known && node.AddsText)
                    incoming = NormalizeAppendedText(incoming);
                if (incoming.Kind == Kind.Known && node.MapsEmpty)
                    incoming = incoming.Carrier.Empty ? new(Kind.Known, new(owner.NullType, Empty: true)) : new(Kind.Conflict);
                Fact next = Combine(node.Fact, incoming);
                if (node.Fact == next) return;
                node.Fact = next;
                if (node.Fact.Kind == Kind.Conflict) ++Conflicts;
                changed.Enqueue(node);
            }

            private Fact Combine(Fact current, Fact incoming)
            {
                if (current.Kind == Kind.NoSeed) return incoming;
                if (incoming.Kind != Kind.Known) return new(Kind.Conflict);
                bool needsMaterialize = current.NeedsMaterialize || incoming.NeedsMaterialize;
                if (incoming.Carrier.Empty || current.Carrier == incoming.Carrier)
                    return current with { NeedsMaterialize = needsMaterialize };
                if (current.Carrier.Empty) return incoming with { NeedsMaterialize = needsMaterialize };
                ++ShapeConflicts;
                return new(Kind.Conflict);
            }

            private void Build(LlvmValue value, Node node)
            {
                switch (value)
                {
                    case LlvmLoadInstruction { PointerOperand: LlvmAllocaInstruction alloca }:
                        Link(Local(alloca), node);
                        return;
                    case LlvmLoadInstruction load when fields.ReadFields(load.PointerOperand) is { Length: > 0 } source:
                        foreach (var field in source) Link(Lane(field), node);
                        return;
                    case LlvmPhiInstruction phi:
                        Inputs(node, Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue));
                        return;
                    case LlvmSelectInstruction select:
                        Link(Value(select.TrueValue), node);
                        Link(Value(select.FalseValue), node);
                        return;
                    case LlvmArgument argument:
                        Inputs(node, ArgumentSources(argument));
                        return;
                    case LlvmCallInstruction { CalledFunction: { } target } call when BuildCall(call, target, node):
                        return;
                    default:
                        Merge(node, new(Kind.Conflict));
                        return;
                }
            }

            private IEnumerable<LlvmValue>? ArgumentSources(LlvmArgument argument)
            {
                if (argument.Parent is not { } function || !owner.sourceFunctions.TryGetValue(function, out var source)
                    || argument.ArgNo < source.CaptureCount) return null;
                // Native callback adaptations as well as indirect source
                // invocations can pass values outside the direct call users.
                indirect ??= owner.callableCalls.Where(pair => pair.Key.Uses.Any()
                        && pair.Value.Operation is LlvmCallableRuntimeOperation.Invoke or LlvmCallableRuntimeOperation.Adapt)
                    .SelectMany(pair => pair.Value.Targets.Values).ToHashSet();
                if (indirect.Contains(function)) return null;
                direct ??= [];
                if (!direct.TryGetValue(function, out var callers))
                    direct.Add(function, callers = function.Users.OfType<LlvmCallInstruction>()
                        .Where(call => call.CalledFunction == function).ToArray());
                return callers.Select(call => call.GetArgument((int)argument.ArgNo));
            }

            private bool BuildCall(LlvmCallInstruction call, LlvmFunction target, Node node)
            {
                if (!target.IsDeclaration)
                {
                    Inputs(node, target.BasicBlocks.Select(block => block.LastInstruction).OfType<LlvmReturnInstruction>()
                        .Select(result => result.ReturnValue!));
                    return true;
                }
                if (owner.constructors.GetValueOrDefault(target) == NativeKind.Range)
                {
                    bool empty = call.ArgumentCount == 0;
                    var element = empty ? owner.NullType : call.GetArgument(0).Type;
                    if (call.Arguments.Any(argument => argument.Type != element)) return false;
                    Merge(node, new(Kind.Known, new(element, empty, owner.completionVectors?.Contains(target) == true)));
                    return true;
                }
                string? name = owner.nativeCalls.GetValueOrDefault(target);
                if (SnapshotAppendOrConcat(call, node, name)) return true;
                if (name is "ranges.sort_with" or "ranges.filter")
                {
                    node.ProducesPlan = true;
                    Link(Value(call.GetArgument(0)), node);
                    return true;
                }
                if (name == "ranges.sequence")
                {
                    Merge(node, new(Kind.Known, new(owner.context.GetPrimitiveType(LlvmTypeId.DoubleTyID)!, Empty: false),
                        NeedsMaterialize: true));
                    return true;
                }
                if (name == "ranges.empty_of")
                {
                    Merge(node, new(Kind.Known, new(owner.NullType, Empty: true)));
                    return true;
                }
                if (name == "ranges.transform" && call.GetArgument(1) is LlvmCallInstruction { CalledFunction: { } adapt }
                    && owner.callableCalls.GetValueOrDefault(adapt) is
                        { Operation: LlvmCallableRuntimeOperation.Adapt, SessionSlot: < 0, Targets.Count: 1 } adapter
                    && adapter.Targets.Values.First().ReturnType is { } result && result.IsDoubleTy())
                {
                    // This plan yields Number but does not own snapshot bytes.
                    // The closed materialize call publishes that same item ABI.
                    Merge(node, new(Kind.Known, new(result, Empty: false), NeedsMaterialize: true));
                    ++TransformSources;
                    return true;
                }
                if (name == "ranges.transform")
                {
                    node.MapsEmpty = true;
                    Link(Value(call.GetArgument(0)), node);
                    return true;
                }
                node.Materializes = name == "ranges.materialize";
                if (name == "ranges.materialize"
                    || owner.runtimeConversions.GetValueOrDefault(target)?.Operation == LlvmRuntimeConversionOperation.Represent
                        && (call.Type == call.GetArgument(0).Type || AdmitsRange(call) && AdmitsRange(call.GetArgument(0))))
                {
                    Link(Value(call.GetArgument(0)), node);
                    return true;
                }
                if (name == "ranges.reverse")
                {
                    Link(Value(call.GetArgument(0)), node);
                    return true;
                }
                if (name is not ("ranges.take" or "ranges.drop") || !call.GetArgument(1).Type.IsDoubleTy()) return false;
                node.ForcesEmpty = name == "ranges.take" && call.GetArgument(1) is LlvmConstantFP limit
                    && (limit.Value.BitcastToAPInt().GetZExtValue() & long.MaxValue) == 0;
                Link(Value(call.GetArgument(0)), node);
                return true;
            }

            private bool AdmitsRange(LlvmValue value)
            {
                TypeKind domain = owner.UnionDomain(value.Type) ?? (value.LogicalType < 0
                    ? TypeKind.Never : owner.metadata.Types[value.LogicalType].Kind);
                return (domain & TypeKind.Range) != 0;
            }
        }
    }

}
