using System.Collections.Immutable;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class FunctionLowering
    {
        private int Call(HirNode node)
        {
            var call = Hir.Calls[node.Data];
            int callee = Emit(Child(node, 0));
            int[] arguments = new int[node.Children.Count - 1];
            for (int i = 0; i < arguments.Length; i++) arguments[i] = Emit(Child(node, i + 1));
            int logicalType = owner.Type(node.Type);
            int resultType = owner.Union(owner.CallResultTypes(call).Append(logicalType));
            bool emptyHistoricalRange = owner.HasHistoricalBindings && call.NativeName == "ranges.empty_of";
            int result = Register(emptyHistoricalRange ? owner.HistoricalRangeAbi(resultType) : resultType);
            if (call.Kind == HirCallKind.Native && call.SessionSlot < 0)
            {
                if (emptyHistoricalRange)
                    Add(new(NativeOp.MakeRange, result, C: arguments[0], Type: slots[result].Type, Inputs: [], Source: Source(node)));
                else if (owner.HistoricalFunction(call.NativeName, out int target))
                    Add(new(NativeOp.Call, result, callee, target, Inputs: arguments, Source: Source(node)));
                else
                {
                    int metadata = HistoricalRangeMetadata(node);
                    Add(new(NativeOp.CallNative, result, C: metadata, Symbol: call.NativeName, Inputs: arguments, Source: Source(node)));
                }
            }
            else if (call.Kind == HirCallKind.Path)
                Add(new(NativeOp.ProcessCall, result, callee, call.Scheduling, Inputs: arguments, Source: Source(node)));
            else
                Add(new(NativeOp.Call, result, callee,
                    call.Kind == HirCallKind.Function && call.SessionSlot < 0 ? owner.Function(call.Function) : -1,
                    call.SessionSlot, Symbol: Dispatch(call.Targets), Inputs: arguments, Source: Source(node)));
            return Narrow(result, logicalType, Source(node));
        }

        private string? Dispatch(ImmutableArray<HirDispatchTarget> targets) => targets.IsDefaultOrEmpty ? null :
            FunctionDispatch.Encode(targets.Select(target =>
                KeyValuePair.Create(owner.Callable(target.Identity), owner.Function(target.Target))));

        private int Capture(HirCapture capture) => capture.Mode == HirCaptureMode.Location
            ? Address(capture.SourceLocation) : Emit(capture.SourceValue);

        private int Closure(HirFunctionId target, ImmutableArray<HirCapture> captures, HirNode node) =>
            MakeClosure(owner.Function(target), captures, node);

        private int TemplateClosure(HirNode node)
        {
            var closure = Hir.TemplateClosures[node.Data];
            return MakeClosure(owner.TemplateFunction(closure.Template), closure.Captures, node);
        }

        private int MakeClosure(int target, ImmutableArray<HirCapture> captures, HirNode node)
        {
            int[] values = new int[captures.Length];
            for (int i = 0; i < values.Length; i++) values[i] = Capture(captures[i]);
            int layout = owner.EnvironmentLayout(values.Select(value => slots[value].Type));
            int result = Register(node.Type);
            Add(new(NativeOp.MakeFunction, result, target, layout, Inputs: values, Source: Source(node)));
            return result;
        }

        private int Adapt(HirNode node)
        {
            int original = Emit(Child(node, 0)); var adaptation = Hir.Adaptations[node.Data];
            int result = Register(node.Type);
            int direct = adaptation.SessionSlot < 0 && adaptation.Targets.Length == 1
                ? owner.Function(adaptation.Targets[0].Target) : -1;
            Add(new(NativeOp.AdaptFunction, result, original, direct, adaptation.SessionSlot,
                Symbol: Dispatch(adaptation.Targets), Source: Source(node))); return result;
        }

        private int Scope(HirNode node)
        {
            var construction = Hir.Scopes[node.Data]; int layout = owner.ScopeLayout(node.Data);
            int result = Register(node.Type);
            int branch = -1, ready = -1;
            int declaration = construction.Singleton >= 0 ? owner.Static(node.Data) : -1;
            if (declaration >= 0)
            {
                ready = Register(owner.Type(TypeRef.Bool));
                Add(new(NativeOp.TestStatic, ready, declaration, Source: Source(node)));
                branch = Add(new(NativeOp.Branch, A: ready));
                reachable = true;
                Add(new(NativeOp.StaticAddress, result, declaration, Source: Source(node)));
            }
            else
            {
                int instruction = Add(new(NativeOp.Allocate, result, layout, owner.Site(node.Data), Source: Source(node)));
                owner.Allocation(owner.Function(id), instruction, layout);
            }
            for (int ordinal = 0; ordinal < construction.Fields.Length; ordinal++)
            {
                var field = construction.Fields[ordinal];
                if (owner.NeedsFieldBinding(field))
                {
                    int address = Register(owner.Pointer(owner.LocationType(field)));
                    Add(new(NativeOp.AddressOffset, address, result, layout, ordinal, Source: Source(node)));
                    Add(new(NativeOp.BindLocation, A: owner.Slot(id, field), B: address, Source: Source(node)));
                }
                boundFields[field] = node.Kind == HirKind.Scope ? new BoundField(result, layout, ordinal) : null;
            }
            foreach (var initializer in Hir.Children(node))
            {
                if (Hir.Node(initializer).Kind == HirKind.Declare) Initialize(Hir.Node(initializer));
                else { int value = Emit(initializer); Add(new(NativeOp.Drop, A: value)); }
            }
            if (node.Kind == HirKind.Scope)
                foreach (var field in construction.Fields) boundFields.Remove(field);
            if (declaration >= 0)
            {
                Add(new(NativeOp.PublishStatic, A: declaration, Source: Source(node)));
                int done = Add(new(NativeOp.Jump));
                int existing = code.Count;
                reachable = true;
                Add(new(NativeOp.StaticAddress, result, declaration, Source: Source(node)));
                PatchBranch(branch, existing, branch + 1);
                if (done >= 0) PatchJump(done, code.Count);
            }
            if (construction.Self is { } self) Store(self, result, true, Source(node));
            return result;
        }

        private int Field(HirNode node, bool write)
        {
            var receiver = Child(node, 0); int value = Emit(receiver);
            if (!reachable) return Register(node.Type);
            if (owner.Runtime(Hir.Node(receiver).Type).WithoutNull.Kind == TypeKind.Process)
            {
                int member = Register(node.Type);
                Add(new(NativeOp.CallNative, member, Symbol: "process." + node.Text,
                    Inputs: [value], Source: Source(node))); return member;
            }
            int assigned = write ? Emit(Child(node, 1)) : -1;
            if (!reachable) return Register(node.Type);
            var projection = write ? owner.WriteProjection(node, receiver, slots[assigned].Type) : owner.Projection(node, receiver);
            if (projection.Projection < 0)
            {
                Add(new(NativeOp.Unreachable, Symbol: $"No physical namespace layout satisfies member '{node.Text}'.", Source: Source(node)));
                return Register(node.Type);
            }
            value = Narrow(value, owner.ProjectionReceiverType(projection.Projection, slots[value].Type), Source(node));
            if (write && owner.ProjectionCases(projection.Projection)
                .Any(item => !owner.StorageAccepts(owner.ProjectedType(item), slots[assigned].Type)))
                return CheckedFieldStore(node, value, assigned, projection.Projection);
            int address = Register(owner.Pointer(projection.Type));
            Add(new(NativeOp.ProjectAddress, address, value, projection.Projection, Source: Source(node)));
            if (write)
            {
                Add(new(NativeOp.Store, A: address, B: assigned, C: 0, Type: projection.Type, Source: Source(node))); return Null();
            }
            int loaded = Register(projection.Type);
            Add(new(NativeOp.Load, loaded, address, Type: projection.Type, Source: Source(node)));
            return Narrow(loaded, owner.Type(node.Type), Source(node));
        }

        private int CheckedFieldStore(HirNode node, int receiver, int assigned, int projection)
        {
            var groups = owner.ProjectionCases(projection).GroupBy(owner.ProjectedType)
                .Select(group => (Type: group.Key, Cases: group.ToArray())).ToArray();
            List<(int Branch, int Group)> selected = [];
            for (int group = 0; group < groups.Length; group++)
                foreach (NativeProjectionCase alternative in groups[group].Cases)
                {
                    int test = Register(owner.Type(TypeRef.Bool));
                    Add(new(NativeOp.Unary, test, receiver, alternative.Layout, Immediate: (long)NativeUnary.HasLayout, Source: Source(node)));
                    int branch = Add(new(NativeOp.Branch, A: test));
                    selected.Add((branch, group));
                    reachable = true;
                    code[branch] = code[branch] with { C = code.Count };
                }
            Add(new(NativeOp.Unreachable, Symbol: "The selected namespace is outside its writable layout contract.", Source: Source(node)));
            List<int> exits = [];
            for (int group = 0; group < groups.Length; group++)
            {
                foreach (var choice in selected.Where(choice => choice.Group == group))
                    code[choice.Branch] = code[choice.Branch] with { B = code.Count };
                reachable = true;
                int selectedProjection = owner.ProjectCases(groups[group].Cases);
                int narrowed = Narrow(receiver, owner.ProjectionReceiverType(selectedProjection, slots[receiver].Type), Source(node));
                int address = Register(owner.Pointer(groups[group].Type));
                Add(new(NativeOp.ProjectAddress, address, narrowed, selectedProjection, Source: Source(node)));
                int value = assigned;
                if (!owner.StorageAccepts(groups[group].Type, slots[value].Type))
                {
                    int admission = owner.StorageAdmissionType(groups[group].Type);
                    value = AdmitFieldValue(assigned, admission, Source(node));
                }
                Add(new(NativeOp.Store, A: address, B: value, C: 0, Type: groups[group].Type, Source: Source(node)));
                exits.Add(Add(new(NativeOp.Jump)));
            }
            foreach (int exit in exits) PatchJump(exit, code.Count);
            reachable = exits.Count != 0;
            return Null(node.Source);
        }

        private int AdmitFieldValue(int value, int type, SourceSpan? source)
        {
            if (owner.StoreConverts(type, slots[value].Type))
            {
                int result = Register(type);
                Add(new(NativeOp.Convert, result, value, Type: type, Source: source));
                return result;
            }
            int union = owner.Union([slots[value].Type, type]);
            if (union != slots[value].Type)
            {
                int widened = Register(union);
                Copy(widened, value, source);
                value = widened;
            }
            return Narrow(value, type, source);
        }

        private int Range(HirNode node)
        {
            int[] values = Children(node); int result = Register(node.Type);
            Add(new(NativeOp.MakeRange, result, C: HistoricalRangeMetadata(node),
                Type: owner.Type(node.Type), Inputs: values, Source: Source(node)));
            return result;
        }

        private int HistoricalRangeMetadata(HirNode node) => owner.HistoricalElement(node.Type) is { } element
            ? Literal(new(HirLiteralKind.Type, Type: element), Source(node)) : -1;
    }
}
