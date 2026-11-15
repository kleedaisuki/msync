using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal readonly record struct PhysicalMember(LlvmStructType Struct, int Layout, int Ordinal, int FieldType);
        private Dictionary<string, List<PhysicalMember>>? physicalMemberLayouts;
        private readonly Dictionary<(string Name, TypeKind Kinds), PhysicalMember[]> memberCases = [];
        private readonly Dictionary<int, StorageRecord> builtinRecords = [];
        private HashSet<TypeKind>? declaredCarriers;

        internal PhysicalMember[] MemberLayouts(string name, TypeKind kinds)
        {
            if (memberCases.TryGetValue((name, kinds), out var existing)) return existing;
            physicalMemberLayouts ??= trace?.Detail is { } detail
                ? detail.Measure("compiler.llvm.lower.member_catalogue", this, static state => state.BuildMemberCatalogue())
                : BuildMemberCatalogue();
            PhysicalMember[] result = physicalMemberLayouts.TryGetValue(name, out var candidates)
                ? candidates.Where(member => (metadata.Types[member.FieldType].Kind & kinds) != TypeKind.Never).ToArray() : [];
            memberCases.Add((name, kinds), result);
            trace?.Detail?.Metric("catalogue.candidates", result.Length);
            return result;
        }

        internal PhysicalMember SourceMember(int shape, string name)
        {
            var fields = hir.ScopeLayouts[shape].Fields;
            int ordinal = fields.Select((field, index) => (field, index)).First(item => item.field.Name == name).index;
            int layout = metadata.Layout(shape);
            return new(Layout(shape), layout, ordinal, metadata.Layouts[layout].Fields[ordinal].Type);
        }

        private Dictionary<string, List<PhysicalMember>> BuildMemberCatalogue()
        {
            Dictionary<string, List<PhysicalMember>> result = new(StringComparer.Ordinal);
            HashSet<HirNodeId> visited = [];
            HashSet<int> shapes = session is null ? [] : new(session.ScopeShapes);
            HashSet<int> nativeLayouts = [];
            Stack<HirNodeId> pending = new(hir.Functions.Select(function => function.Body).Where(body => !body.IsNone));
            int fields = 0;
            foreach (int shape in shapes) fields += AddSourceMembers(result, shape);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id)) continue;
                var node = hir.Node(id);
                if (node.Kind is HirKind.Scope or HirKind.AllocateScope)
                {
                    int shape = hir.Scopes[node.Data].Layout;
                    if (shapes.Add(shape)) fields += AddSourceMembers(result, shape);
                }
                if (node.Kind == HirKind.Call)
                    foreach (var schema in BuiltinRecordSchemas(node))
                        fields += AddBuiltinMembers(result, schema, nativeLayouts);
                foreach (var child in hir.Children(node)) pending.Push(child);
                if (node.Kind == HirKind.Match)
                    foreach (var arm in hir.Matches[node.Data].Arms) pending.Push(arm.Value);
            }
            trace?.Detail?.Metric("nodes", visited.Count);
            trace?.Detail?.Metric("layouts", shapes.Count + nativeLayouts.Count);
            trace?.Detail?.Metric("builtin.layouts", nativeLayouts.Count);
            trace?.Detail?.Metric("fields", fields);
            return result;
        }

        private int AddSourceMembers(Dictionary<string, List<PhysicalMember>> result, int shape)
        {
            var fields = hir.ScopeLayouts[shape].Fields;
            int layout = metadata.Layout(shape);
            var structure = Layout(shape);
            for (int index = 0; index < fields.Length; index++)
                AddMember(result, fields[index].Name, new(structure, layout, index, metadata.Layouts[layout].Fields[index].Type));
            return fields.Length;
        }

        private int AddBuiltinMembers(Dictionary<string, List<PhysicalMember>> result, NativeBuiltinRecordSchema schema, HashSet<int> declared)
        {
            var fields = schema.Fields.Select(field => new NativeCodeField(field.Name, metadata.RuntimeType(field.Type))).ToArray();
            int layout = metadata.AddLayout(fields);
            if (!declared.Add(layout)) return 0;
            if (!builtinRecords.TryGetValue(layout, out var storage))
            {
                storage = Storage(schema.Fields.Select(field => RepresentationType(field.Type.Kind)).ToArray(), fields);
                builtinRecords.Add(layout, storage);
            }
            for (int index = 0; index < fields.Length; index++)
                AddMember(result, fields[index].Name, new(storage.Type, layout, index, fields[index].Type));
            return fields.Length;
        }

        private static void AddMember(Dictionary<string, List<PhysicalMember>> result, string name, PhysicalMember member)
        {
            if (!result.TryGetValue(name, out var members)) result.Add(name, members = []);
            members.Add(member);
        }

        private IEnumerable<NativeBuiltinRecordSchema> BuiltinRecordSchemas(HirNode node)
        {
            var call = hir.Calls[node.Data];
            if (call.Kind != HirCallKind.Native) yield break;
            switch (call.NativeName)
            {
                case "ranges.enumerate": case "ranges.enumerate_from":
                    foreach (var carrier in DeclaredCarriers(ItemKinds(node, 1))) yield return NativeBuiltinLayouts.Enumerate(carrier);
                    break;
                case "ranges.zip":
                    foreach (var first in DeclaredCarriers(ItemKinds(node, 1)))
                    foreach (var second in DeclaredCarriers(ItemKinds(node, 2))) yield return NativeBuiltinLayouts.Zip(first, second);
                    break;
                case "environment.variables": yield return NativeBuiltinLayouts.EnvironmentVariable; break;
                case "shell.process_table": yield return NativeBuiltinLayouts.Process; break;
                case "regex.find": case "regex.matches":
                    yield return NativeBuiltinLayouts.RegexMatch;
                    yield return NativeBuiltinLayouts.RegexGroup;
                    break;
                case "meta.signature_of": yield return NativeBuiltinLayouts.Signature; break;
                case "meta.members_of": yield return NativeBuiltinLayouts.Member; break;
            }
        }

        private TypeKind ItemKinds(HirNode node, int index)
        {
            var range = hir.Types[hir.Node(hir.Children(node)[index]).Type.Value];
            return CarrierKinds(range.Element >= 0 ? hir.Types[range.Element].RuntimeType.Kind : range.RuntimeType.Element?.Kind ?? TypeKind.Any);
        }

        private IEnumerable<TypeRef> DeclaredCarriers(TypeKind admitted)
        {
            declaredCarriers ??= hir.Types.Select(type => CarrierKinds(type.RuntimeType.Kind | (type.Nullable ? TypeKind.Null : TypeKind.Never))).ToHashSet();
            yield return new(admitted);
            foreach (var carrier in declaredCarriers)
                if (carrier != admitted && carrier != TypeKind.Never && (carrier & admitted) == carrier) yield return new(carrier);
        }

        private static TypeKind CarrierKinds(TypeKind kinds) => (kinds & TypeKind.Storage) != 0
            ? kinds & ~TypeKind.Storage | TypeKind.Scope : kinds;
    }

    private sealed partial class Body
    {
        private long directMemberCount, dispatchedMemberCount, memberLayoutTestCount, processMemberCount;

        private LlvmValue Member(HirNode node)
        {
            if (owner.trace?.Detail is not { } detail) return EmitMember(node);
            return detail.MeasureDetailed("compiler.llvm.lower.member", (Body: this, Node: node),
                static state => state.Body.EmitMember(state.Node), attributes: HirTelemetry.Source(Hir.Sources, node.Source));
        }

        private LlvmValue EmitMember(HirNode node)
        {
            var receiverNode = Hir.Node(Hir.Children(node)[0]);
            var receiver = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            LlvmValue? assigned = node.Kind == HirKind.MemberAssign ? Child(node, 1) : null;
            if (block.HasTerminator) return owner.Null;
            current = node;
            var type = Hir.Types[receiverNode.Type.Value];
            if (type.RuntimeType.WithoutNull.Kind == TypeKind.Process)
            {
                processMemberCount++;
                owner.trace?.Detail?.Metric("process", 1);
                return NativeBuiltin(node, "process." + node.Text, [receiver]);
            }
            var domain = type.ScopeDomain;
            owner.trace?.Detail?.Metric("physical.layouts", domain.Count);
            if (domain.Count == 1)
            {
                directMemberCount++;
                return MemberAt(node, receiver, assigned, owner.SourceMember(domain[0], node.Text!));
            }
            var cases = domain.Count == 0
                ? owner.MemberLayouts(node.Text!, Hir.Types[(assigned is null ? node.Type : Hir.Node(Hir.Children(node)[1]).Type).Value].RuntimeType.Kind)
                : Enumerable.Range(0, domain.Count).Select(index => owner.SourceMember(domain[index], node.Text!)).ToArray();
            if (cases.Length == 0) throw new LlvmHirLoweringException(node.Source, "member without a declared physical object layout");
            if (cases.Length == 1)
            {
                directMemberCount++;
                return MemberAt(node, receiver, assigned, cases[0]);
            }
            dispatchedMemberCount++;
            owner.trace?.Detail?.Metric("layout.tests", cases.Length - 1);
            return DispatchMember(node, receiver, assigned, cases);
        }

        private LlvmValue MemberAt(HirNode node, LlvmValue receiver, LlvmValue? assigned, Lowering.PhysicalMember member)
        {
            var address = FieldAddress(member.Struct, receiver, member.Ordinal);
            var type = owner.RepresentationType(owner.metadata.Types[member.FieldType].Kind);
            if (assigned is not null)
            {
                var value = ReferenceEquals(assigned.Type, type) ? assigned :
                    ConversionCall(LlvmRuntimeConversionOperation.Represent, type, member.FieldType, [assigned]);
                Add(new LlvmStoreInstruction(value, address));
                return owner.Null;
            }
            var loaded = Add(new LlvmLoadInstruction(type, address) { LogicalType = member.FieldType });
            return Represent(loaded, node.Type);
        }

        private LlvmValue DispatchMember(HirNode node, LlvmValue receiver, LlvmValue? assigned, IReadOnlyList<Lowering.PhysicalMember> cases)
        {
            var done = new LlvmBasicBlock(owner.context, "member.end", function);
            var incoming = new List<(LlvmValue Value, LlvmBasicBlock Block)>();
            for (int index = 0; index < cases.Count; index++)
            {
                if (index + 1 < cases.Count)
                {
                    var selected = new LlvmBasicBlock(owner.context, "member.layout", function);
                    var next = new LlvmBasicBlock(owner.context, "member.next", function);
                    Add(new LlvmCondBrInstruction(HasLayout(receiver, cases[index].Layout), selected, next));
                    block = selected;
                    EmitCase(cases[index]);
                    block = next;
                }
                else EmitCase(cases[index]);
            }
            block = done;
            if (assigned is not null) return owner.Null;
            var result = Add(new LlvmPhiInstruction(owner.Type(node.Type), incoming.Count) { LogicalType = owner.metadata.Type(node.Type) });
            foreach (var item in incoming) result.AddIncoming(item.Value, item.Block);
            return result;

            void EmitCase(Lowering.PhysicalMember member)
            {
                var value = MemberAt(node, receiver, assigned, member);
                incoming.Add((value, block));
                Add(new LlvmUncondBrInstruction(done));
            }
        }

        private LlvmValue HasLayout(LlvmValue receiver, int layout)
        {
            var target = owner.OperationBinding(new(LlvmRuntimeValueOperation.HasLayout, layout),
                LlvmFunctionType.Get(LlvmIntegerType.Get(owner.context, 1), [receiver.Type]));
            memberLayoutTestCount++;
            return Add(new LlvmCallInstruction(target.FunctionType, target, [receiver]) { LogicalType = owner.metadata.RuntimeType(TypeRef.Bool) });
        }
    }
}
