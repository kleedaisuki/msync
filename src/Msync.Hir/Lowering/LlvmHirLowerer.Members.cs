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
        internal readonly Dictionary<TypeKind, int> enumeratedLayouts = [];
        internal (int Match, int Group)? RegexFindLayouts { get; private set; }
        internal readonly Dictionary<(TypeKind First, TypeKind Second), int> zippedLayouts = [];
        internal ulong MemberOffset(PhysicalMember member) =>
            module.DataLayout.GetStructLayout(member.Struct).GetElementOffset((uint)member.Ordinal).GetFixedValue();
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

        internal bool TrySourceMember(int shape, string name, int ordinal, out PhysicalMember member)
        {
            var fields = hir.ScopeLayouts[shape].Fields;
            if ((uint)ordinal >= (uint)fields.Length || fields[ordinal].Name != name)
            {
                ordinal = 0;
                while (ordinal < fields.Length && fields[ordinal].Name != name) ordinal++;
                if (ordinal == fields.Length) { member = default; return false; }
            }
            int layout = metadata.Layout(shape);
            member = new(Layout(shape), layout, ordinal, metadata.Layouts[layout].Fields[ordinal].Type);
            return true;
        }

        internal readonly Dictionary<int, LlvmStructType> scopeLayoutsByPhysicalId = [];

        internal bool TryAllocatedMember(int layout, string name, int ordinalHint, out PhysicalMember member)
        {
            member = default;
            if (!scopeLayoutsByPhysicalId.TryGetValue(layout, out var structure)) return false;
            var fields = metadata.Layouts[layout].Fields;
            if ((uint)ordinalHint < (uint)fields.Count && fields[ordinalHint].Name == name)
            {
                member = new(structure, layout, ordinalHint, fields[ordinalHint].Type);
                return true;
            }
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Name == name)
                {
                    member = new(structure, layout, i, fields[i].Type);
                    return true;
                }
            return false;
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

        internal void RegisterEnumeration(HirNode node)
        {
            var payload = ItemKinds(node, 1);
            if (enumeratedLayouts.ContainsKey(payload)) return;
            var storage = BuiltinStorage(NativeBuiltinLayouts.Enumerate(new TypeRef(payload)));
            enumeratedLayouts.Add(payload, storage.Layout);
        }

        internal void RegisterRegexFind() => RegexFindLayouts ??=
            (BuiltinStorage(NativeBuiltinLayouts.RegexMatch).Layout, BuiltinStorage(NativeBuiltinLayouts.RegexGroup).Layout);

        internal void RegisterZip(HirNode node)
        {
            var kinds = (ItemKinds(node, 1), ItemKinds(node, 2));
            if (zippedLayouts.ContainsKey(kinds)) return;
            var storage = BuiltinStorage(NativeBuiltinLayouts.Zip(new TypeRef(kinds.Item1), new TypeRef(kinds.Item2)));
            zippedLayouts.Add(kinds, storage.Layout);
        }

        private StorageRecord BuiltinStorage(NativeBuiltinRecordSchema schema)
        {
            var fields = schema.Fields.Select(field => new NativeCodeField(field.Name, metadata.RuntimeType(field.Type))).ToArray();
            int layout = metadata.AddLayout(fields);
            if (builtinRecords.TryGetValue(layout, out var known)) return known;
            var storage = Storage(schema.Fields.Select(field => RepresentationType(field.Type.Kind)).ToArray(), fields);
            builtinRecords.Add(layout, storage);
            return storage;
        }

        private int AddBuiltinMembers(Dictionary<string, List<PhysicalMember>> result, NativeBuiltinRecordSchema schema, HashSet<int> declared)
        {
            var storage = BuiltinStorage(schema);
            if (!declared.Add(storage.Layout)) return 0;
            var fields = metadata.Layouts[storage.Layout].Fields;
            for (int index = 0; index < fields.Count; index++)
                AddMember(result, fields[index].Name, new(storage.Type, storage.Layout, index, fields[index].Type));
            return fields.Count;
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
            bool checkedView = (node.Effects & HirEffects.MayFail) != 0;
            owner.trace?.Detail?.Metric("physical.layouts", domain.Count);
            if (domain.Count == 1)
            {
                if (!owner.TrySourceMember(domain[0], node.Text!, node.Data, out var member))
                    return MissingMember(node, checkedView);
                directMemberCount++;
                owner.trace?.Detail?.Metric("layout.fixed_offset", 1);
                return MemberAt(node, receiver, assigned, member);
            }
            if (receiver is LlvmHeapAllocInst allocation
                && owner.TryAllocatedMember(allocation.LogicalLayout, node.Text!, node.Data, out var allocated))
            {
                directMemberCount++;
                owner.trace?.Detail?.Metric("layout.known_allocation", 1);
                owner.trace?.Detail?.Metric("layout.fixed_offset", 1);
                return MemberAt(node, receiver, assigned, allocated);
            }
            Lowering.PhysicalMember[] cases;
            bool exhaustive = true;
            if (domain.Count == 0)
            {
                cases = owner.MemberLayouts(node.Text!, Hir.Types[(assigned is null ? node.Type : Hir.Node(Hir.Children(node)[1]).Type).Value].RuntimeType.Kind);
                exhaustive = !checkedView;
            }
            else
            {
                List<Lowering.PhysicalMember> known = new(domain.Count);
                for (int index = 0; index < domain.Count; index++)
                {
                    if (owner.TrySourceMember(domain[index], node.Text!, node.Data, out var member)) known.Add(member);
                    else if (!checkedView) return MissingMember(node, false);
                }
                cases = known.ToArray();
                exhaustive = cases.Length == domain.Count;
            }
            if (cases.Length == 0) return MissingMember(node, checkedView);
            if (!exhaustive)
            {
                dispatchedMemberCount++;
                owner.trace?.Detail?.Metric("layout.checked_view_tests", cases.Length);
                return DispatchMember(node, receiver, assigned, cases, exhaustive: false);
            }
            if (cases.Length == 1)
            {
                directMemberCount++;
                return MemberAt(node, receiver, assigned, cases[0]);
            }
            if (UniformMember(cases))
            {
                var projection = new NativeProjection(cases.Select(member => new NativeProjectionCase(member.Layout, member.Ordinal))
                    .Distinct().ToArray());
                int fieldType = cases[0].FieldType;
                var operation = new LlvmRuntimeValueBinding(LlvmRuntimeValueOperation.ProjectAddress) { Projection = projection };
                var binding = owner.OperationBinding(operation, LlvmFunctionType.Get(receiver.Type, [receiver.Type]));
                var address = Add(new LlvmCallInstruction(binding.FunctionType, binding, [receiver])
                    { LogicalType = owner.metadata.Pointer(fieldType) });
                directMemberCount++;
                owner.trace?.Detail?.Metric("layout.uniform_projection", 1);
                owner.trace?.Detail?.Metric("layout.tests_eliminated", cases.Length - 1);
                return MemberAccess(node, address, assigned, fieldType);
            }
            dispatchedMemberCount++;
            owner.trace?.Detail?.Metric("layout.tests", cases.Length - 1);
            return DispatchMember(node, receiver, assigned, cases);
        }

        private LlvmValue MemberAt(HirNode node, LlvmValue receiver, LlvmValue? assigned, Lowering.PhysicalMember member)
            => MemberAccess(node, FieldAddress(member.Struct, receiver, member.Ordinal), assigned, member.FieldType);

        private bool UniformMember(Lowering.PhysicalMember[] cases)
        {
            var first = cases[0];
            ulong offset = owner.MemberOffset(first);
            for (int index = 1; index < cases.Length; index++)
                if (cases[index].FieldType != first.FieldType || owner.MemberOffset(cases[index]) != offset)
                    return false;
            return true;
        }

        private LlvmValue MemberAccess(HirNode node, LlvmValue address, LlvmValue? assigned, int fieldType)
        {
            var actual = owner.metadata.Types[fieldType].Kind;
            var type = owner.RepresentationType(actual);
            if (assigned is not null)
            {
                var supplied = Hir.Types[Hir.Node(Hir.Children(node)[1]).Type.Value].RuntimeType.Kind;
                if ((supplied & actual) == TypeKind.Never) return MissingCheckedMember(node);
                if ((supplied & ~actual) != TypeKind.Never)
                    assigned = CheckedMemberValue(assigned, type, fieldType);
                var value = ReferenceEquals(assigned.Type, type) ? assigned :
                    ConversionCall(LlvmRuntimeConversionOperation.Represent, type, fieldType, [assigned]);
                Add(new LlvmStoreInstruction(value, address));
                return owner.Null;
            }
            var expected = Hir.Types[node.Type.Value].RuntimeType.Kind;
            bool checkedView = (node.Effects & HirEffects.MayFail) != 0;
            if (checkedView && (actual & expected) == TypeKind.Never) return MissingCheckedMember(node);
            var loaded = Add(new LlvmLoadInstruction(type, address) { LogicalType = fieldType });
            if (checkedView && (actual & ~expected) != TypeKind.Never)
                return CheckedMemberValue(loaded, owner.Type(node.Type), owner.metadata.Type(node.Type));
            return Represent(loaded, node.Type);
        }

        private LlvmValue CheckedMemberValue(LlvmValue value, LlvmType type, int target)
        {
            owner.trace?.Detail?.Metric("member.checked_view_narrows", 1);
            return ConversionCall(LlvmRuntimeConversionOperation.Narrow, type, target, [value], target);
        }

        private LlvmValue MissingCheckedMember(HirNode node)
        {
            owner.trace?.Detail?.Metric("member.checked_view_rejects", 1);
            // Bare LLVM unreachable lets the optimizer discard the incoming edge.
            OperationCall(new(LlvmRuntimeValueOperation.FailMember, Symbol: $"Object member '{node.Text}' is outside its structural view."),
                LlvmType.GetVoidTy(owner.context), -1, []);
            Add(new LlvmUnreachableInstruction(owner.context));
            return owner.Null;
        }

        private LlvmValue MissingMember(HirNode node, bool checkedView) => checkedView
            ? MissingCheckedMember(node)
            : throw new LlvmHirLoweringException(node.Source, "member without a declared physical object layout");

        private LlvmValue DispatchMember(HirNode node, LlvmValue receiver, LlvmValue? assigned,
            IReadOnlyList<Lowering.PhysicalMember> cases, bool exhaustive = true)
        {
            var done = new LlvmBasicBlock(owner.context, "member.end", function);
            var incoming = new List<(LlvmValue Value, LlvmBasicBlock Block)>();
            for (int index = 0; index < cases.Count; index++)
            {
                if (!exhaustive || index + 1 < cases.Count)
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
            if (!exhaustive) MissingCheckedMember(node);
            block = done;
            if (incoming.Count == 0) return MissingCheckedMember(node);
            if (assigned is not null) return owner.Null;
            var result = Add(new LlvmPhiInstruction(owner.Type(node.Type), incoming.Count) { LogicalType = owner.metadata.Type(node.Type) });
            foreach (var item in incoming) result.AddIncoming(item.Value, item.Block);
            return result;

            void EmitCase(Lowering.PhysicalMember member)
            {
                var value = MemberAt(node, receiver, assigned, member);
                if (block.HasTerminator) return;
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
