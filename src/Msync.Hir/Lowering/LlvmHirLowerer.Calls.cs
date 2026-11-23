using System.Collections.Immutable;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal readonly Dictionary<LlvmFunction, LlvmCallableRuntimeBinding> callableCalls = [];
        private readonly Dictionary<int, LlvmFunction> templates = [];
        private readonly Dictionary<string, StorageRecord> environments = [];
        private readonly Dictionary<int, StorageRecord> cells = [];
        private readonly Dictionary<LlvmFunction, List<LlvmValue[]>> captureInstances = [];
        internal readonly record struct StorageRecord(LlvmStructType Type, int Layout, int LogicalType);

        internal void RecordCaptureSources(LlvmFunction target, LlvmValue[] addresses)
        {
            if (addresses.Length == 0) return;
            if (!captureInstances.TryGetValue(target, out var sites)) captureInstances.Add(target, sites = []);
            sites.Add(addresses);
        }

        private Dictionary<LlvmArgument, List<LlvmValue>> CaptureSourceAddresses()
        {
            Dictionary<LlvmArgument, List<LlvmValue>> origins = [];
            foreach (var (function, source) in sourceFunctions)
            {
                if (source.CaptureCount == 0) continue;
                if (captureInstances.TryGetValue(function, out var direct)) Record(function, source.CaptureCount, direct);
                if (source.SourceFunction is { } id && hir.Functions[id.Value].Template is { } recipe
                    && templates.TryGetValue(recipe.Value, out var template)
                    && captureInstances.TryGetValue(template, out var instances))
                    Record(function, source.CaptureCount, instances);
            }
            return origins;

            void Record(LlvmFunction function, int count, List<LlvmValue[]> sites)
            {
                if (sites.Any(addresses => addresses.Length != count)) return;
                for (int index = 0; index < count; ++index)
                {
                    var argument = function.Arguments[index];
                    if (!origins.TryGetValue(argument, out var values)) origins.Add(argument, values = []);
                    foreach (var addresses in sites) values.Add(addresses[index]);
                }
            }
        }

        private static HashSet<HirLocationId> Captured(HirModule module) => module.Closures
            .SelectMany(closure => closure.Captures).Concat(module.TemplateClosures.SelectMany(closure => closure.Captures))
            .Where(capture => capture.SourceLocation.Value >= 0).Select(capture => capture.SourceLocation).ToHashSet();

        internal LlvmFunction Template(HirTemplateId id)
        {
            if (templates.TryGetValue(id.Value, out var existing)) return existing;
            var template = hir.Templates[id.Value];
            var result = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)),
                LlvmLinkage.ExternalLinkage, "msync.template." + id.Value, module);
            templates.Add(id.Value, result);
            templateFunctions.Add(result, new(template.Parameters.Select(parameter => metadata.Type(hir.Locations[parameter.Value].StorageType, hir.Locations[parameter.Value].AdmitsNull)).ToArray(),
                template.Captures.Select(capture => metadata.Type(hir.Locations[capture.Target.Value].StorageType, hir.Locations[capture.Target.Value].AdmitsNull)).ToArray())
                { SourceTemplate = id, SourceName = template.Name });
            return result;
        }

        internal IReadOnlyDictionary<LlvmFunction, LlvmFunction> Dispatch(ImmutableArray<HirDispatchTarget> targets) =>
            targets.IsDefaultOrEmpty ? new Dictionary<LlvmFunction, LlvmFunction>() : targets.ToDictionary(
                target => target.Identity.IsTemplate ? Template(new(target.Identity.Value)) : Reserve(new(target.Identity.Value)),
                target => Reserve(target.Target));

        internal StorageRecord Cell(HirTypeId type, bool admitsNull = false)
        {
            int logicalType = metadata.Type(type, admitsNull);
            if (cells.TryGetValue(logicalType, out var existing)) return existing;
            var result = Storage([Type(type, admitsNull)], [new("value", logicalType)]);
            cells.Add(logicalType, result);
            return result;
        }

        internal StorageRecord Environment(IReadOnlyList<int> pointees)
        {
            string key = string.Join(',', pointees);
            if (environments.TryGetValue(key, out var existing)) return existing;
            var result = Storage(pointees.Select(_ => (LlvmType)DataPointer).ToArray(),
                pointees.Select((pointee, index) => new NativeCodeField("capture." + index, metadata.Pointer(pointee))).ToArray());
            environments.Add(key, result);
            return result;
        }

        private StorageRecord Storage(IReadOnlyList<LlvmType> fields, IReadOnlyList<NativeCodeField> logicalFields)
        {
            int layout = metadata.AddLayout(logicalFields);
            if (physicalStructs.TryGetValue(layout, out var type)) ++reusedPhysicalStructs;
            else
            {
                type = NewPhysicalStruct(layout);
                type.SetBody(fields);
            }
            return new(type, layout, metadata.Scope(layout));
        }
    }

    private sealed partial class Body
    {
        private long closureCount;
        private long sourceCallCount;
        private long captureCellCount;
        private long dynamicCallCount;
        private long adaptationCount;

        private LlvmValue Allocate(Lowering.StorageRecord record)
        {
            return Add(new LlvmHeapAllocInst(owner.Int(Math.Max(1UL, owner.module.DataLayout.GetTypeAllocSize(record.Type).GetFixedValue())),
                owner.module.DataLayout.GetABITypeAlign(record.Type), owner.DataPointer.AddressSpace)
                { LogicalType = record.LogicalType, LogicalLayout = record.Layout });
        }

        private LlvmValue FreshLocation(HirLocationId location)
        {
            if (owner.session?.Roots.ContainsKey(location) == true) return Address(location);
            var storage = owner.Cell(Hir.Locations[location.Value].StorageType, Hir.Locations[location.Value].AdmitsNull);
            var allocation = Allocate(storage);
            var address = FieldAddress(storage.Type, allocation, 0);
            locations[location] = address;
            captureCellCount++;
            return address;
        }

        private LlvmValue Closure(HirNode node)
        {
            var closure = Hir.Closures[node.Data];
            return MakeClosure(owner.Reserve(closure.Function), closure.Captures, node);
        }

        private LlvmValue TemplateClosure(HirNode node)
        {
            var closure = Hir.TemplateClosures[node.Data];
            return MakeClosure(owner.Template(closure.Template), closure.Captures, node);
        }

        private LlvmValue CaptureAddress(HirCapture capture)
        {
            if (capture.SourceLocation.Value >= 0) return Address(capture.SourceLocation);
            if (!capture.SourceValue.IsNone && Hir.Node(capture.SourceValue) is { Kind: HirKind.ReadLocation or HirKind.ReadStorage } read)
                return Address(new(read.Data));
            throw new LlvmHirLoweringException(current.Source, "capture without a source location");
        }

        private LlvmValue MakeClosure(LlvmFunction target, ImmutableArray<HirCapture> captures, HirNode node)
        {
            var addresses = captures.Select(CaptureAddress).ToArray();
            owner.RecordCaptureSources(target, addresses);
            LlvmValue environment = LlvmConstantPointerNull.Get(owner.DataPointer);
            if (addresses.Length != 0)
            {
                var storage = owner.Environment(addresses.Select(address => owner.metadata.Types[address.LogicalType].Pointee).ToArray());
                environment = Allocate(storage);
                for (int index = 0; index < addresses.Length; index++)
                    Add(new LlvmStoreInstruction(addresses[index], FieldAddress(storage.Type, environment, index)));
            }
            current = node;
            var seed = LlvmPoisonValue.Get(owner.FunctionType());
            int logicalType = owner.metadata.RuntimeType(TypeRef.Function);
            var identity = Add(new LlvmInsertValueInstruction(seed, target, [0]) { LogicalType = logicalType });
            var captured = Add(new LlvmInsertValueInstruction(identity, environment, [1]) { LogicalType = logicalType });
            closureCount++;
            return Add(new LlvmInsertValueInstruction(captured, LlvmConstantPointerNull.Get(owner.DataPointer), [2]));
        }

        private LlvmValue SourceCall(HirNode node)
        {
            var call = Hir.Calls[node.Data];
            var descriptor = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            var arguments = new List<LlvmValue>();
            for (int index = 1; index < node.Children.Count; index++)
            {
                arguments.Add(Child(node, index));
                if (block.HasTerminator) return owner.Null;
            }
            current = node;
            if (call.Kind == HirCallKind.Path) return ProcessCall(node, descriptor, arguments);
            if (call.SessionSlot >= 0 && owner.physicalSessionTargets?.GetValueOrDefault(call.SessionSlot) is
                { NativeTargets.Count: > 0 } selected)
                return SessionNativeCall(node, descriptor, arguments, selected);
            if (call.Kind == HirCallKind.Dynamic || call.SessionSlot >= 0)
            {
                dynamicCallCount++;
                return CallableRuntime(LlvmCallableRuntimeOperation.Invoke, node, descriptor, arguments, call.Targets, call.SessionSlot);
            }
            var target = owner.Reserve(call.Function);
            var captures = Hir.Functions[call.Function.Value].Captures;
            var actual = new List<LlvmValue>();
            if (captures.Length != 0)
            {
                var pointees = captures.Select(capture => owner.metadata.Type(Hir.Locations[capture.Target.Value].StorageType, Hir.Locations[capture.Target.Value].AdmitsNull)).ToArray();
                var storage = owner.Environment(pointees);
                var environment = Add(new LlvmExtractValueInstruction(descriptor, [1]) { LogicalType = storage.LogicalType });
                for (int index = 0; index < captures.Length; index++)
                    actual.Add(Add(new LlvmLoadInstruction(owner.DataPointer, FieldAddress(storage.Type, environment, index))
                        { LogicalType = owner.metadata.Pointer(pointees[index]) }));
            }
            var definition = Hir.Functions[call.Function.Value];
            for (int index = 0; index < arguments.Count; index++)
                actual.Add(Represent(arguments[index], definition.ParameterEntryTypes.IsDefaultOrEmpty
                    ? Hir.Locations[definition.Parameters[index].Value].StorageType : definition.ParameterEntryTypes[index],
                    definition.ParameterEntryTypes.IsDefaultOrEmpty && Hir.Locations[definition.Parameters[index].Value].AdmitsNull));
            sourceCallCount++;
            var result = Add(new LlvmCallInstruction(target.FunctionType, target, actual,
                [new LlvmOperandBundleDef("msync.callee", [descriptor])])
                { LogicalType = owner.metadata.Type(definition.Result) });
            return Represent(result, node.Type);
        }
        private LlvmValue Adapt(HirNode node)
        {
            var adaptation = Hir.Adaptations[node.Data];
            var original = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            adaptationCount++;
            return CallableRuntime(LlvmCallableRuntimeOperation.Adapt, node, original, [], adaptation.Targets, adaptation.SessionSlot);
        }

        private LlvmValue CallableRuntime(LlvmCallableRuntimeOperation operation, HirNode node, LlvmValue descriptor,
            IReadOnlyList<LlvmValue> arguments, ImmutableArray<HirDispatchTarget> dispatch, int sessionSlot = -1)
        {
            LlvmValue[] inputs = [descriptor, .. arguments];
            var signature = LlvmFunctionType.Get(owner.Type(node.Type), inputs.Select(input => input.Type).ToArray());
            string name = operation == LlvmCallableRuntimeOperation.Invoke ? "msync.callable.invoke." : "msync.callable.adapt.";
            var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                name + owner.callableCalls.Count, owner.module);
            var targets = owner.Dispatch(dispatch);
            var selected = sessionSlot >= 0 ? owner.physicalSessionTargets?.GetValueOrDefault(sessionSlot) : null;
            bool physical = selected is { Targets.Count: > 0 };
            if (physical) targets = selected!.Targets;
            owner.callableCalls.Add(target, new(operation, targets) { SessionSlot = sessionSlot });
            var call = Add(new LlvmCallInstruction(signature, target, inputs));
            return operation == LlvmCallableRuntimeOperation.Invoke && (sessionSlot >= 0 && !physical || targets.Count == 0)
                ? owner.DeferredSource(call, "open_call") : call;
        }
    }
}
