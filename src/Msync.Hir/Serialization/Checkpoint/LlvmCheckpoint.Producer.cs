using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmCheckpointArtifact
{
    private sealed partial class Encoder
    {
        internal void Producer(LlvmHirLoweringResult graph, CompilationTrace? trace)
        {
            Id(graph.Entry);
            Seq(graph.Types, type =>
            {
                writer.Write((ushort)type.Kind);
                Int(type.Element);
                Reference(type.Layouts, layouts => Seq(layouts, Int));
                Int(type.Pointee);
            });
            Seq(graph.Layouts, layout => Seq(layout.Fields, field => { Text(field.Name); Int(field.Type); }));
            Map(graph.Constants, Id, NativeConstant);
            Map(graph.RuntimeCalls, Id, op => writer.Write((byte)op));
            Map(graph.StructLayouts, Type, Int);
            Map(graph.IteratorCalls, Id, op => writer.Write((byte)op));
            Map(graph.SourceFunctions, Id, source =>
            {
                Int(source.CaptureCount);
                Int(source.ResultType);
                writer.Write(source.IsTemplate);
                Optional(source.SourceFunction, id => Int(id.Value));
                Reference(source.SourceName, Text);
            });
            Map(graph.TemplateFunctions, Id, source =>
            {
                Seq(source.Parameters, Int);
                Seq(source.Captures, Int);
                Optional(source.SourceTemplate, id => Int(id.Value));
                Reference(source.SourceName, Text);
            });
            Map(graph.NativeCalls, Id, Text);
            Map(graph.Constructors, Id, kind => writer.Write((byte)kind));
            Map(graph.CallableCalls, Id, binding =>
            {
                writer.Write((byte)binding.Operation);
                Int(binding.SessionSlot);
                Map(binding.Targets, Id, Id);
            });
            Seq(graph.StaticScopes, scope => { Id(scope.Object); Id(scope.Initialized); Int(scope.Layout); Int(scope.Declaration); });
            Reference(graph.EmptyRangeConsumers, set => Seq(set, Id));
            Reference(graph.ObservedTextArguments, map => Map(map, Id, Int));
            Reference(graph.CompletedProcessText, set => Seq(set, Id));
            Reference(graph.TextCompletionFields, set => Seq(set, Field));
            Reference(graph.SnapshotRangeConsumers, map => Map(map, Id, shape =>
            {
                Optional(shape.First, Snapshot);
                Optional(shape.Second, Snapshot);
            }));
            Reference(graph.CompletionVectors, set => Seq(set, Id));
            Map(graph.EnumeratedLayouts, kind => writer.Write((ushort)kind), Int);
            Map(graph.ZippedLayouts, pair => { writer.Write((ushort)pair.First); writer.Write((ushort)pair.Second); }, Int);
            Int(graph.FileLineSources);
            Int(graph.FileTextSources);
            Int(graph.FileTransferSources);
            Optional(graph.RegexFindLayouts, pair => { Int(pair.Match); Int(pair.Group); });
            Reference(graph.PathBatchCarriers, map => Map(map, Id, Id));
            Map(graph.RangeCallbackWork, Id, work => writer.Write((byte)work));
            Map(graph.UnionTypes, Type, kind => writer.Write((ushort)kind));
            Map(graph.PhysicalStructs, Int, Type);
            Map(graph.ValueOperations, Id, ValueOperation);
            Map(graph.RuntimeConversions, Id, conversion =>
            {
                writer.Write((byte)conversion.Operation);
                Int(conversion.TargetType);
            });
            Map(graph.LiftedUnaryCalls, Id, op => writer.Write((byte)op));
            Seq(graph.FutureParameters, Id);
            Seq(graph.FutureResults, Id);
            Seq(graph.FutureFields, Field);
            Map(graph.FutureCaptures, Id, fields => Seq(fields, Field));
            ProducerMetrics(graph, trace);
        }

        private void Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> entries, Action<TKey> key, Action<TValue> value)
        {
            Int(entries.Count);
            foreach (var pair in entries) { key(pair.Key); value(pair.Value); }
        }

        private void Field(LlvmFieldCarrier field) { Int(field.Layout); Int(field.Ordinal); }

        private void Snapshot(LlvmSnapshotCarrier snapshot)
        {
            Type(snapshot.Element);
            Flags(snapshot.Empty, snapshot.Completion);
        }

        private void ValueOperation(LlvmRuntimeValueBinding binding)
        {
            writer.Write((byte)binding.Operation);
            Int(binding.Mode);
            Reference(binding.Symbol, Text);
            Reference(binding.Projection, projection => Seq(projection.Cases, lane => { Int(lane.Layout); Int(lane.Field); }));
        }

        private void NativeConstant(NativeConstant constant)
        {
            writer.Write((byte)constant.Kind);
            Reference(constant.Text, Text);
            Reference(constant.Type, TypeRef);
            writer.Write((byte)constant.ConnectionKind);
            Optional(constant.Path, path =>
            {
                Text(path.Raw);
                Reference(path.WorkingDirectory, Text);
                Optional(path.Connection, connection =>
                { writer.Write((byte)connection.Kind); Reference(connection.Address, Text); });
                Reference(path.HomeDirectory, Text);
                Reference(path.LocalSpelling, Text);
            });
            Int(constant.OriginId);
            Int(constant.OriginOffset);
        }

        private void TypeRef(TypeRef type)
        {
            writer.Write((ushort)type.Kind);
            Reference(type.Element, TypeRef);
        }
    }

    private sealed partial class Decoder
    {
        private NativeCodeType[] logicalTypes = [];
        private NativeCodeLayout[] layouts = [];

        internal LlvmHirLoweringResult Producer(LlvmModule module, CompilationTrace? trace)
        {
            var entry = Id<LlvmFunction>();
            logicalTypes = Seq(() => new NativeCodeType(Kind(), Int(), Reference(() => Seq(Int, 4)), Int()), 11);
            layouts = Seq(() => new NativeCodeLayout(Seq(() => new NativeCodeField(Text(), LogicalType()), 8)), 4);
            ValidateLogicalTypes();
            var graph = new LlvmHirLoweringResult(module,
                Map(Id<LlvmGlobalVariable>, NativeConstant), Map(Id<LlvmFunction>, EnumByte<NativeBinary>), entry,
                logicalTypes, layouts, Map(Struct, Layout),
                Map(Id<LlvmFunction>, EnumByte<LlvmIteratorOperation>),
                Map(Id<LlvmFunction>, SourceFunction),
                Map(Id<LlvmFunction>, TemplateFunction),
                Map(Id<LlvmFunction>, Text),
                Map(Id<LlvmFunction>, EnumByte<NativeKind>),
                Map(Id<LlvmFunction>, Callable))
            {
                StaticScopes = Seq(() => new LlvmStaticScopeInfo(Id<LlvmGlobalVariable>(), Id<LlvmGlobalVariable>(), Layout(), Nonnegative("scope declaration")), 16),
                EmptyRangeConsumers = Reference(() => Set(Id<LlvmFunction>)),
                ObservedTextArguments = Reference(() => Map(Id<LlvmFunction>, () => Nonnegative("text argument"))),
                CompletedProcessText = Reference(() => Set(Id<LlvmFunction>)),
                TextCompletionFields = Reference(() => Set(Field)),
                SnapshotRangeConsumers = Reference(() => Map(Id<LlvmFunction>,
                    () => new LlvmSnapshotOperands(Optional(Snapshot), Optional(Snapshot)))),
                CompletionVectors = Reference(() => Set(Id<LlvmFunction>)),
                EnumeratedLayouts = Map(Kind, Layout),
                ZippedLayouts = Map(() => (Kind(), Kind()), Layout),
                FileLineSources = Nonnegative("file line sources"),
                FileTextSources = Nonnegative("file text sources"),
                FileTransferSources = Nonnegative("file transfer sources"),
                RegexFindLayouts = Optional(() => (Layout(), Layout())),
                PathBatchCarriers = Reference(() => Map(Id<LlvmFunction>, Id<LlvmFunction>)),
                RangeCallbackWork = Map(Id<LlvmFunction>, EnumByte<LlvmRangeCallbackWork>),
                UnionTypes = Map(Struct, Kind),
                PhysicalStructs = Map(Layout, Struct),
                ValueOperations = Map(Id<LlvmFunction>, ValueOperation),
                RuntimeConversions = Map(Id<LlvmFunction>, () => new LlvmRuntimeConversionBinding(
                    EnumByte<LlvmRuntimeConversionOperation>(), MaybeLogicalType(Int()))),
                LiftedUnaryCalls = Map(Id<LlvmFunction>, EnumByte<NativeUnary>),
                FutureParameters = Set(Id<LlvmArgument>),
                FutureResults = Set(Id<LlvmFunction>),
                FutureFields = Set(Field),
                FutureCaptures = Map(Id<LlvmArgument>, () => Seq(Field, 8))
            };
            ValidateProducer(graph);
            ProducerMetrics(graph, trace);
            return graph;
        }

        private void ValidateLogicalTypes()
        {
            foreach (var type in logicalTypes)
            {
                MaybeLogicalType(type.Element);
                MaybeLogicalType(type.Pointee);
                if (type.Layouts?.Any(id => (uint)id >= (uint)layouts.Length) == true)
                    throw Invalid("logical type references an unknown layout");
            }
        }

        private void ValidateProducer(LlvmHirLoweringResult graph)
        {
            if (graph.Entry.IsDeclaration || !graph.SourceFunctions.ContainsKey(graph.Entry))
                throw Invalid("entry is missing its source function contract");
            foreach (var (function, info) in graph.SourceFunctions)
            {
                if ((uint)info.CaptureCount > function.ArgumentCount) throw Invalid("capture count exceeds function arity");
            }
            if (graph.ObservedTextArguments is { } observations) ValidateTextArguments(observations);
            foreach (var value in values)
            {
                MaybeLogicalType(value.LogicalType);
                if (value is LlvmAllocaInstruction alloca)
                {
                    MaybeLogicalType(alloca.AllocatedLogicalType);
                    if (alloca.StorageView is { } view) MaybeLogicalType(view.LogicalType);
                }
                if (value is LlvmHeapAllocInst heap && !graph.PhysicalStructs.ContainsKey(heap.LogicalLayout))
                    throw Invalid("allocation is missing its physical layout");
                if (value is not LlvmUser user) continue;
                for (int i = 0; i < user.OperandCount; ++i) MaybeLogicalType(user.GetOperandUse(i).LogicalType);
            }
        }

        private static void ValidateTextArguments(IReadOnlyDictionary<LlvmFunction, int> observations)
        {
            foreach (var (function, arg) in observations)
                if ((uint)arg >= (uint)function.ArgumentCount) throw Invalid("text observation references an unknown argument");
        }

        private Dictionary<TKey, TValue> Map<TKey, TValue>(Func<TKey> key, Func<TValue> value) where TKey : notnull
        {
            int count = Count(2);
            var entries = new Dictionary<TKey, TValue>();
            for (int i = 0; i < count; ++i)
                if (!entries.TryAdd(key(), value())) throw Invalid("duplicate entry in producer table");
            return entries;
        }

        private HashSet<T> Set<T>(Func<T> read)
        {
            int count = Count();
            var entries = new HashSet<T>();
            for (int i = 0; i < count; ++i)
                if (!entries.Add(read())) throw Invalid("duplicate fact in producer set");
            return entries;
        }

        private int Nonnegative(string field)
        {
            int value = Int();
            return value >= 0 ? value : throw Invalid($"invalid {field}: {value}");
        }
        private TypeKind Kind()
        {
            var kind = (TypeKind)reader.ReadUInt16();
            return (kind & ~TypeKind.Runtime) == 0 ? kind : throw Invalid("unknown logical type bits");
        }
        private int LogicalType() => LogicalType(Int());
        private int LogicalType(int id) => (uint)id < (uint)logicalTypes.Length ? id : throw Invalid($"unknown logical type {id}");
        private int MaybeLogicalType(int id) => id == -1 ? id : LogicalType(id);
        private int Layout()
        {
            int id = Int();
            return (uint)id < (uint)layouts.Length ? id : throw Invalid($"unknown logical layout {id}");
        }
        private LlvmFieldCarrier Field()
        {
            int layout = Layout();
            int ordinal = Int();
            if ((uint)ordinal >= (uint)layouts[layout].Fields.Count)
                throw Invalid($"unknown field {ordinal} in layout {layout}");
            return new(layout, ordinal);
        }

        private LlvmSourceFunctionInfo SourceFunction() =>
            new(Nonnegative("capture count"), LogicalType(), Bool())
            {
                SourceFunction = Optional(() => new HirFunctionId(Nonnegative("source function"))),
                SourceName = Reference(Text)
            };

        private LlvmTemplateFunctionInfo TemplateFunction() => new(Seq(LogicalType, 4), Seq(LogicalType, 4))
        {
            SourceTemplate = Optional(() => new HirTemplateId(Nonnegative("source template"))),
            SourceName = Reference(Text)
        };

        private LlvmCallableRuntimeBinding Callable()
        {
            var op = EnumByte<LlvmCallableRuntimeOperation>();
            if (Int() != -1) throw Invalid("checkpoint contains a REPL callable");
            return new(op, Map(Id<LlvmFunction>, Id<LlvmFunction>));
        }

        private LlvmSnapshotCarrier Snapshot()
        {
            LlvmType element = Type();
            uint flags = FlagBits(2);
            return new(element, Has(flags, 0), Has(flags, 1));
        }

        private LlvmRuntimeValueBinding ValueOperation() =>
            new(EnumByte<LlvmRuntimeValueOperation>(), Int(), Reference(Text))
            {
                Projection = Reference(() => new NativeProjection(Seq(() =>
                {
                    var field = Field();
                    return new NativeProjectionCase(field.Layout, field.Ordinal);
                }, 8)))
            };

        private NativeConstant NativeConstant() =>
            new(EnumByte<NativeKind>(), Reference(Text), Reference(() => TypeRef(0)), EnumByte<ConnectionKind>())
            {
                Path = Optional(() => new NativePath(Text(), Reference(Text),
                    Optional(() => new NativeConnection(EnumByte<ConnectionKind>(), Reference(Text)!)), Reference(Text), Reference(Text))),
                OriginId = Int(),
                OriginOffset = Int()
            };

        private TypeRef TypeRef(int depth)
        {
            if (depth > 256) throw Invalid("excessively nested constant type");
            return new(Kind(), Reference(() => TypeRef(depth + 1)));
        }
    }

    private static void ProducerMetrics(LlvmHirLoweringResult graph, CompilationTrace? trace)
    {
        if (trace is null) return;
        trace.Metric("producer.types", graph.Types.Count);
        trace.Metric("producer.layouts", graph.Layouts.Count);
        trace.Metric("producer.physical_structs", graph.PhysicalStructs.Count);
        trace.Metric("producer.constants", graph.Constants.Count);
        trace.Metric("producer.source_functions", graph.SourceFunctions.Count);
        trace.Metric("producer.native_calls", graph.NativeCalls.Count);
        trace.Metric("producer.callable_bindings", graph.CallableCalls.Count);
        trace.Metric("producer.range_callbacks", graph.RangeCallbackWork.Count);
        trace.Metric("producer.range_consumers", (graph.SnapshotRangeConsumers?.Count ?? 0) + (graph.EmptyRangeConsumers?.Count ?? 0));
        trace.Metric("producer.future_carriers", graph.FutureFields.Count + graph.FutureParameters.Count +
            graph.FutureResults.Count + graph.FutureCaptures.Count);
    }
}
