using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<(HpirService, int, int), int> futureImports = [];
        private readonly Dictionary<int, int> futureLayouts = [];
        private readonly Dictionary<(bool, string), int> processImports = [];
        private readonly bool hasProcesses = graph.ValueOperations.Values.Any(binding =>
            binding.Operation == LlvmRuntimeValueOperation.ProcessCall)
            || graph.NativeCalls.Values.Any(name => name.StartsWith("process.", StringComparison.Ordinal));

        internal bool HasProcessSources => hasProcesses;
        internal bool HasLiveTransferSources => graph.FileTransferSources != 0;
        internal bool HasFutureSignatures => graph.FutureParameters.Count != 0 || graph.FutureResults.Count != 0;
        internal bool FutureResult(LlvmFunction function) =>
            !IsEntry(function) && Union(function.ReturnType) == TypeKind.Never && graph.FutureResults.Contains(function);
        internal bool FutureUnionResult(LlvmFunction function) =>
            !IsEntry(function) && Union(function.ReturnType) != TypeKind.Never && graph.FutureResults.Contains(function);
        internal bool FutureParameter(LlvmArgument value) =>
            Union(value.Type) == TypeKind.Never && graph.FutureParameters.Contains(value);
        internal bool FutureUnionParameter(LlvmArgument value) =>
            Union(value.Type) != TypeKind.Never && graph.FutureParameters.Contains(value);
        internal bool FutureArguments(LlvmFunction function) => function.Arguments.Any(graph.FutureParameters.Contains);
        internal bool TextOutput(LlvmFunction target) => graph.NativeCalls.TryGetValue(target, out string? name)
            && HpirScalarImports.TryTextOutput(name, out _);
        internal bool NumberFormat(LlvmCallInstruction call) => call.CalledFunction is { } target
            && graph.RuntimeConversions.TryGetValue(target, out var conversion)
            && conversion is { Operation: LlvmRuntimeConversionOperation.Convert, TargetType: >= 0 }
            && graph.Types[conversion.TargetType].Kind == TypeKind.String && call.GetArgument(0).Type.IsDoubleTy();

        internal bool NoOpReleaseMarker(LlvmFunction target) => graph.ValueOperations.TryGetValue(target, out var binding)
            && binding is { Operation: LlvmRuntimeValueOperation.Barrier, Mode: 0, Symbol: null };

        internal bool ProcessStart(LlvmFunction target, out int scheduling)
        {
            scheduling = 0;
            if (!graph.ValueOperations.TryGetValue(target, out var binding) || binding.Operation != LlvmRuntimeValueOperation.ProcessCall)
                return false;
            scheduling = binding.Mode;
            return true;
        }

        internal HpirService? ProcessProjection(LlvmFunction target) => graph.NativeCalls.GetValueOrDefault(target) switch
        {
            "process.exit_code" => HpirService.ProcessExit,
            "process.success" => HpirService.ProcessSuccess,
            "process.stdout" => HpirService.ProcessStdout,
            "process.stderr" => HpirService.ProcessStderr,
            _ => null
        };

        internal bool ProcessCarrier(LlvmValue value) => IsGuestPointer(value.Type)
            && (value.LogicalType >= 0 && graph.Types[value.LogicalType].Kind == TypeKind.Process
                || value is LlvmCallInstruction { CalledFunction: { } target }
                && graph.SourceFunctions.TryGetValue(target, out var source) && graph.Types[source.ResultType].Kind == TypeKind.Process);

        internal bool StringValue(LlvmValue value) => value.Type.IsPointerTy() && value.LogicalType >= 0
            && graph.Types[value.LogicalType].Kind == TypeKind.String;

        internal HpirProcessArgument ProcessArgument(LlvmValue value) => value.Type switch
        {
            _ when value.Type.IsDoubleTy() => HpirProcessArgument.Number,
            _ when value.Type.IsIntegerTy(1) => HpirProcessArgument.Bool,
            _ when IsPathRecord(value.Type) => HpirProcessArgument.Path,
            _ when StringValue(value) => HpirProcessArgument.Text,
            _ => HpirProcessArgument.Invalid
        };

        internal bool ScalarDemand(LlvmFunction target) => graph.ValueOperations.TryGetValue(target, out var binding)
            && binding.Operation == LlvmRuntimeValueOperation.DemandBool;

        internal HpirFutureOperation LiftOperation(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return HpirFutureOperation.Ready;
            if (graph.LiftedUnaryCalls.TryGetValue(target, out var unary))
                return unary == NativeUnary.Negate ? HpirFutureOperation.Negate : HpirFutureOperation.Not;
            if (!graph.RuntimeCalls.TryGetValue(target, out var binary) || call.ArgumentCount != 2)
                return HpirFutureOperation.Ready;
            return call.GetArgument(0).Type.IsDoubleTy() && call.GetArgument(1).Type.IsDoubleTy()
                ? NumberOperation(binary) : call.GetArgument(0).Type.IsIntegerTy(1) && call.GetArgument(1).Type.IsIntegerTy(1)
                    ? binary switch
                    {
                        NativeBinary.Equal => HpirFutureOperation.BoolEqual,
                        NativeBinary.NotEqual => HpirFutureOperation.BoolNotEqual,
                        _ => HpirFutureOperation.Ready
                    }
                    : HpirFutureOperation.Ready;
        }

        internal HpirMathOp FutureMath(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.NativeCalls.TryGetValue(target, out string? name)
                || !name.StartsWith("math.", StringComparison.Ordinal)
                || !HpirScalarImports.TryMath(name.AsSpan("math.".Length), out HpirMathOp operation))
                return HpirMathOp.Invalid;
            var (arity, width) = HpirFutureMathImports.Shape(operation);
            return call.ArgumentCount == arity && call.Arguments.All(argument => argument.Type.IsDoubleTy())
                && (width == HpirWidth.U8 ? call.Type.IsIntegerTy(1) : call.Type.IsDoubleTy())
                ? operation : HpirMathOp.Invalid;
        }

        private static HpirFutureOperation NumberOperation(NativeBinary operation) => operation switch
        {
            NativeBinary.Add => HpirFutureOperation.Add,
            NativeBinary.Subtract => HpirFutureOperation.Subtract,
            NativeBinary.Multiply => HpirFutureOperation.Multiply,
            NativeBinary.Divide => HpirFutureOperation.Divide,
            NativeBinary.Remainder => HpirFutureOperation.Remainder,
            NativeBinary.Power => HpirFutureOperation.Power,
            NativeBinary.Equal => HpirFutureOperation.Equal,
            NativeBinary.NotEqual => HpirFutureOperation.NotEqual,
            NativeBinary.Less => HpirFutureOperation.Less,
            NativeBinary.LessEqual => HpirFutureOperation.LessEqual,
            NativeBinary.Greater => HpirFutureOperation.Greater,
            NativeBinary.GreaterEqual => HpirFutureOperation.GreaterEqual,
            _ => HpirFutureOperation.Ready
        };

        internal int DemandImport(HpirWidth payload, HpirFutureInput mode) =>
            FutureImport(HpirFutureImports.Demand(payload, mode));

        internal int ProcessStartImport(int scheduling, HpirProcessArgument[] arguments)
        {
            bool release = (scheduling & 1) != 0;
            var key = (release, string.Create(arguments.Length, arguments, static (shape, kinds) =>
            {
                for (int i = 0; i < kinds.Length; ++i) shape[i] = (char)kinds[i];
            }));
            if (processImports.TryGetValue(key, out int index)) return index;
            int layout = FutureLayout(-(int)HpirService.ProcessStart, HpirFutureImports.ProcessHandle());
            index = Imports.Count;
            Imports.Add(HpirFutureImports.StartProcess(layout, release, arguments));
            processImports.Add(key, index);
            return index;
        }

        internal int ProcessImport(HpirService service)
        {
            if (service is HpirService.ProcessStdout or HpirService.ProcessStderr)
                return TextInputImport(service);
            int layout = FutureLayout(-(int)HpirService.ProcessExit, HpirFutureImports.ProcessScalarProjection());
            return FutureImport(service == HpirService.ProcessExit
                ? HpirFutureImports.ProcessExit(layout) : HpirFutureImports.ProcessSuccess(layout));
        }

        internal int LiftImport(HpirFutureOperation operation, HpirFutureInput left, HpirFutureInput right)
        {
            HpirFutureImports.TryShape(operation, out _, out var input, out var result);
            int layout = FutureLayout(((int)input << 4) | (int)result, HpirFutureImports.Lifted(operation));
            return FutureImport(HpirFutureImports.Lift(operation, left, right, layout));
        }

        internal int FutureMathImport(HpirMathOp operation, HpirFutureInput first, HpirFutureInput second, HpirFutureInput third)
        {
            int arity = HpirFutureMathImports.Shape(operation).Arity;
            int layout = FutureLayout(1000 + arity, HpirFutureMathImports.Layout(arity));
            return FutureImport(HpirFutureMathImports.Lift(new(operation, first, second, third, layout)));
        }

        private int FutureLayout(int key, HpirLayout declaration)
        {
            if (futureLayouts.TryGetValue(key, out int index)) return index;
            index = Layouts.Count;
            Layouts.Add(declaration);
            futureLayouts.Add(key, index);
            return index;
        }

        private int FutureImport(HpirImport import)
        {
            int geometry = import.Service == HpirService.FutureDemand ? (int)import.Result : import.Arguments.Length;
            var key = (import.Service, import.Binding, geometry);
            if (futureImports.TryGetValue(key, out int index)) return index;
            index = Imports.Count;
            Imports.Add(import);
            futureImports.Add(key, index);
            return index;
        }
    }

    private sealed partial class Body
    {
        private HashSet<LlvmValue>? futureCells;
        private HashSet<LlvmValue>? textCells;
        private Dictionary<LlvmCallInstruction, LlvmValue>? deferredFormats;
        private HashSet<LlvmValue>? mixedCells;
        private Dictionary<LlvmValue, int>? payloads;
        private int absentFuture = -1;
        private HashSet<LlvmValue>? processHandles;
        private int processSites, processArguments, readyDemands;

        private void ReportProcesses(CompilationTrace? trace)
        {
            trace?.Metric("process.argv.sites", processSites);
            trace?.Metric("process.argv.operands", processArguments);
            if (pendingProcessArguments != 0) trace?.Metric("process.argv.pending_text_operands", pendingProcessArguments);
            if (mixedProcessArguments != 0) trace?.Metric("process.argv.either_text_operands", mixedProcessArguments);
            trace?.Metric("process.spawn.ready_vector", processSpawns);
            trace?.Metric("process.control.sites", processControls);
        }

        private bool FutureCell(LlvmValue value) => futureCells?.Contains(value) == true;
        private bool TextCell(LlvmValue value) => textCells?.Contains(value) == true;
        private bool FutureRoot(LlvmValue value) => FutureCell(value) || TextCell(value) || processHandles?.Contains(value) == true;
        private HpirWidth ValueWidth(LlvmValue value) => FutureRoot(value) ? HpirWidth.U64 : Width(value.Type);

        // Select completion lanes across the surviving SSA edges; field and
        // signature decisions already belong to the pre-LLVM producer.
        private void PrepareFutureCarriers(CompilationTrace? trace)
        {
            if (!geometry.HasProcessSources && !geometry.HasLineInput && !geometry.HasFileText && !geometry.HasFutureSignatures
                && !geometry.HasFutureFields && !geometry.HasLiveTransferSources && !geometry.HasFileMetadata
                && !geometry.HasStoredText && !geometry.HasTextResults) return;
            bool needsAbsent = geometry.FutureResult(function) || geometry.FutureUnionResult(function);
            bool needsArgumentTextTags = false;
            var work = new Queue<LlvmValue>();
            var text = new Queue<LlvmValue>();
            foreach (var argument in function.Arguments)
            {
                if (geometry.FutureParameter(argument)) MarkAbiValue(argument);
                if (geometry.FutureUnionParameter(argument)) MarkUnion(argument);
                if (geometry.TextParameter(argument)) EnqueueText(argument);
                if (geometry.HasProcessSources && geometry.ProcessCarrier(argument)) MarkProcess(argument);
            }
            foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
            {
                if (geometry.HasProcessSources && geometry.ProcessCarrier(instruction)) MarkProcess(instruction);
                if (geometry.StoredText(instruction)) EnqueueText(instruction);
                if (geometry.TextResult(instruction)) EnqueueText(instruction);
                if (instruction is LlvmLoadInstruction item && IsCompletionItem(item.PointerOperand))
                {
                    MarkAbiValue(item);
                    continue;
                }
                if (instruction is LlvmLoadInstruction load && geometry.FutureStorageDelta(load.GetOperandUse(0)) is not null)
                {
                    if (geometry.Union(load.Type) == TypeKind.Never) MarkAbiValue(load);
                    else MarkUnion(load);
                    continue;
                }
                if (instruction is LlvmStoreInstruction store && geometry.FutureStorageDelta(store.GetOperandUse(1)) is not null)
                    needsAbsent = true;
                if (instruction is LlvmFileOutInst { Mode: LlvmFileOutMode.Transfer, UseEmpty: false } transfer) Enqueue(transfer);
                if (instruction is LlvmFileInInst { Format: LlvmFileInFormat.Text } fileText) EnqueueText(fileText);
                if (instruction is not LlvmCallInstruction { CalledFunction: { } target } call) continue;
                if (HasContextCheck(call)) continue;
                if (geometry.VectorAccessFor(call) is VectorAccess.At or VectorAccess.Front or VectorAccess.First
                    && AccessSnapshot(call) is { Completion: true, Empty: false } shape && call.Type == shape.Element)
                    MarkAbiValue(call);
                if (geometry.IsEntry(function) && geometry.CooperativeNumberFront(call)) Enqueue(call);
                if (!target.IsDeclaration)
                {
                    if (geometry.FutureArguments(target)) needsAbsent = true;
                    if (target.Arguments.Any(geometry.MixedTextResult)) needsArgumentTextTags = true;
                }
                if (geometry.ProcessStart(target, out _))
                {
                    if (!geometry.IsEntry(function) && Aggregate(function.ReturnType) && !Geometry.IsPathRecord(function.ReturnType)
                        && geometry.Leaves(function.ReturnType).Any(leaf => geometry.IsGuestPointer(leaf.Type)))
                        throw Error("a process in a function with borrowed pointers in its aggregate return ABI", call);
                    MarkProcess(call);
                    continue;
                }
                if (geometry.StandardTextTarget(target) is not null)
                {
                    if (!geometry.IsEntry(function) && !geometry.TextResult(function) && Aggregate(function.ReturnType)
                        && geometry.Leaves(function.ReturnType).Any(leaf => geometry.IsGuestPointer(leaf.Type)))
                        throw Error("input in a function with borrowed pointers in its aggregate return ABI", call);
                    EnqueueText(call);
                    continue;
                }
                if (geometry.ProcessProjection(target) is HpirService.ProcessStdout or HpirService.ProcessStderr)
                {
                    if (!geometry.ReadyProcessText(call)) EnqueueText(call);
                }
                else if (geometry.ProcessProjection(target) is not null) Enqueue(call);
                else if (geometry.FutureResult(target) || geometry.FileMetadata(target) != HpirFileMetadata.Invalid) MarkAbiValue(call);
                else if (geometry.FutureUnionResult(target)) MarkUnion(call);
            }
            while (text.TryDequeue(out var input))
                foreach (var user in input.Users)
                {
                    if (user is LlvmStoreInstruction root && root.ValueOperand == input
                        && (geometry.StoredTextAddress(root.PointerOperand) || geometry.TextFieldStorage(root.GetOperandUse(1))
                            || geometry.PromptInputStore(root).HasValue)) continue;
                    if (user is LlvmReturnInstruction
                        && (geometry.SessionTextEntry(function) || geometry.TextResult(function))) continue;
                    if (user is LlvmInstruction forwarded && geometry.TextResult(forwarded)
                        && geometry.TextForwardInputs(forwarded)?.Contains(input) == true) continue;
                    if (user is LlvmFileOutInst { Mode: LlvmFileOutMode.Transfer } transfer && transfer.Value == input) continue;
                    if (user is LlvmFileOutInst write && geometry.CanWriteFileText(write)
                        && (write.Value == input || write.Destination == input)) continue;
                    if (user is LlvmCallInstruction { CalledFunction: { } control, ArgumentCount: 2 } pipeWrite
                        && geometry.ProcessControl(control) == HpirProcessControl.WriteInput && pipeWrite.GetArgument(1) == input)
                        continue;
                    if (user is LlvmCallInstruction observed
                        && (ObservesTextArgument(observed, input) || FormatObservesText(observed, input)
                            || geometry.LiftedTextConcat(observed) || TextReduction(observed, input)
                            || PathTextDemand(observed, input)
                            || ProcessTextArgument(observed, input) || geometry.TextCallArgument(observed, input))) continue;
                    if (user is not LlvmCallInstruction { CalledFunction: { } target, ArgumentCount: 1 } call)
                        throw Error("a text completion crossing an unfinished physical carrier boundary", user as LlvmInstruction);
                    if ((geometry.StoredText(input) || geometry.MixedTextResult(input))
                        && !geometry.NarrowsText(call) && !geometry.TextOutput(target))
                        throw Error("an unfinished observation of mixed stored Text", call);
                    if (geometry.TextProjection(call) is not null) Enqueue(call);
                    else if (geometry.NarrowsText(call)) EnqueueText(call);
                    else if (!geometry.TextOutput(target) && geometry.ObservedTextParse(target) is null)
                        throw Error("a text completion crossing an unfinished physical carrier boundary", call);
                }
            while (work.TryDequeue(out var value))
                foreach (var user in value.Users)
                {
                    if (FutureCell(value) && FutureOperands(user) is { } inputs && inputs.Contains(value))
                        Enqueue((LlvmInstruction)user);
                    else if (UnionFlow(value, user) is var use && use != FutureUnionUse.None)
                    {
                        if (use == FutureUnionUse.Forward && geometry.Union(user.Type) != TypeKind.Never) MarkUnion(user);
                        else MarkAbiValue(user);
                    }
                }
            FindMixedResults(work);
            if (mixedCells is not null || unionCells is not null || needsAbsent)
            {
                absentFuture = Reserve(8, 8);
                Add(new(HpirOp.ConstU64, absentFuture, Immediate: 0));
            }
            if (futureCells is not null)
                foreach (var value in futureCells)
                    foreach (var user in value.Users)
                        if (!FutureUse(value, user))
                            throw Error("a deferred scalar crossing an unfinished physical carrier boundary: " + CarrierUse(user),
                                user as LlvmInstruction);
            if (unionCells is not null)
                foreach (var value in unionCells)
                    foreach (var user in value.Users)
                        if (!UnionFutureUse(value, user))
                            throw Error("a scalar completion leaving its physical union: " + CarrierUse(user), user as LlvmInstruction);
            if (processHandles is not null)
                foreach (var value in processHandles)
                    foreach (var user in value.Users)
                        if (!ProcessUse(value, user))
                            throw Error("a process handle escaping its current physical carrier", user as LlvmInstruction);
            trace?.Metric("future.known_cells", (futureCells?.Count ?? 0) - (mixedCells?.Count ?? 0));
            trace?.Metric("future.mixed_cells", mixedCells?.Count ?? 0);
            trace?.Metric("future.process_handles", processHandles?.Count ?? 0);
            trace?.Metric("future.text_cells", textCells?.Count ?? 0);
            trace?.Metric("future.union_cells", unionCells?.Count ?? 0);
            if (needsArgumentTextTags || geometry.MixedTextResult(function) || textCells?.Any(geometry.MixedTextResult) == true)
                PrepareTextTags();
            return;

            void Enqueue(LlvmInstruction value)
            {
                if ((futureCells ??= []).Add(value)) work.Enqueue(value);
            }

            void EnqueueText(LlvmValue value)
            {
                if ((textCells ??= []).Add(value)) text.Enqueue(value);
            }

            void MarkAbiValue(LlvmValue value)
            {
                (mixedCells ??= []).Add(value);
                if ((futureCells ??= []).Add(value)) work.Enqueue(value);
            }

            void MarkUnion(LlvmValue value)
            {
                if ((unionCells ??= []).Add(value)) work.Enqueue(value);
            }
        }

        private void MarkProcess(LlvmValue value) => (processHandles ??= []).Add(value);

        private bool ProcessUse(LlvmValue value, LlvmUser user)
        {
            if (user is LlvmReturnInstruction) return !geometry.IsEntry(function) || geometry.IsPersistentSession;
            if (user is LlvmStoreInstruction store) return store.ValueOperand == value;
            if (user is LlvmSelectInstruction or LlvmPhiInstruction) return processHandles!.Contains(user);
            if (user is not LlvmCallInstruction { CalledFunction: { } target } call) return false;
            if (geometry.ProcessProjection(target) is not null || geometry.ProcessControl(target) != HpirProcessControl.Invalid
                || geometry.AcquireTarget(target) || geometry.TextFormatCall(call, out _)) return true;
            if (target.IsDeclaration) return false;
            for (int index = 0; index < call.ArgumentCount; index++)
                if (call.GetArgument(index) == value && !geometry.ProcessCarrier(target.Arguments[index])) return false;
            return true;
        }

        private void FindMixedResults(Queue<LlvmValue> work)
        {
            if (futureCells is null) return;
            foreach (var value in futureCells) Mark(value);
            while (work.TryDequeue(out var value))
                foreach (var user in value.Users)
                    if (FutureCell(user)) Mark(user);
            return;

            void Mark(LlvmValue value)
            {
                if (FutureOperands(value) is not { } inputs) return;
                bool mixed = value is LlvmCallInstruction
                    ? inputs.All(input => FutureInput(input) != HpirFutureInput.Deferred)
                    : inputs.Any(input => FutureInput(input) != HpirFutureInput.Deferred);
                if (mixed && (mixedCells ??= []).Add(value)) work.Enqueue(value);
            }
        }

        private IEnumerable<LlvmValue>? FutureOperands(LlvmValue value) => value switch
        {
            LlvmSelectInstruction select => [select.TrueValue, select.FalseValue],
            LlvmPhiInstruction phi => Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue),
            LlvmCallInstruction call when HpirFutureImports.TryShape(geometry.LiftOperation(call), out _, out _, out _)
                || geometry.FutureMath(call) != HpirMathOp.Invalid => call.Arguments,
            _ => null
        };

        private bool FutureUse(LlvmValue value, LlvmUser user)
        {
            if (FutureCell(user) && FutureOperands(user) is { } inputs && inputs.Contains(value)) return true;
            if (user is LlvmStoreInstruction store && store.ValueOperand == value)
                return geometry.FutureStorageDelta(store.GetOperandUse(1)) is not null;
            if (user is LlvmReturnInstruction) return geometry.IsEntry(function) || geometry.FutureResult(function);
            if (user is not LlvmCallInstruction { CalledFunction: { } target } call) return false;
            if (UnionFlow(value, user) != FutureUnionUse.None) return true;
            if (geometry.IsCompletionVector(target)) return true;
            if (geometry.SizeOnlyPack(call) || geometry.ScalarDemand(target) || geometry.TextFormatCall(call, out bool namedFormat) && !namedFormat) return true;
            if (geometry.CountedProducer(call) != HpirRangeBound.None && value == call.GetArgument(1)) return true;
            if (geometry.VectorWindow(call) != HpirRangeBound.None && value == call.GetArgument(1)
                && geometry.SnapshotShape(call.GetArgument(0)) is not null) return true;
            if (geometry.NumericRangeProducer(call) != default || geometry.IsTransformTake(call)
                && (geometry.TransformCallback(call) is not null || geometry.TextPipeline(call) is not null
                    || geometry.ZipShape(call) is not null || geometry.SortedShape(call) is not null)) return true;
            if (RecordFileBoolFormat(value, call)) return true;
            if (RecordFileNumberFormat(value, call)) return true;
            if (geometry.NumberFormat(call) && call.HasOneUse && call.Users.First() is LlvmCallInstruction
                { CalledFunction: { } output, ArgumentCount: 1 } consume && consume.GetArgument(0) == call
                && geometry.TextOutput(output))
            {
                (deferredFormats ??= []).TryAdd(call, value);
                return true;
            }
            if (target.IsDeclaration) return false;
            for (int index = 0; index < call.ArgumentCount; ++index)
                if (call.GetArgument(index) == value && !geometry.FutureParameter(target.Arguments[index])) return false;
            return true;
        }

        private static string CarrierUse(LlvmUser user) => user switch
        {
            LlvmCallInstruction invoke => invoke.CalledFunction?.Name ?? "indirect call",
            LlvmStoreInstruction store => $"store through {store.PointerOperand.GetType().Name}",
            LlvmInstruction instruction => instruction.Opcode.ToString(),
            _ => user.GetType().Name
        };

        private bool TryFutureCall(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { IsDeclaration: true } target) return false;
            if (TryContextCheck(call)) return true;
            if (geometry.NoOpReleaseMarker(target) || TryWorkCall(call, target)) return true;
            if (deferredFormats?.ContainsKey(call) == true) return true;
            if (TryFileMetadata(call, target)) return true;
            if (TryOutputFormat(call, target)) return true;
            if (TryProcessLibrary(call, target)) return true;
            if (geometry.StandardTextTarget(target) is { } input)
            {
                EmitCall(call, ~geometry.TextInputImport(input), Location(call), []);
                return true;
            }
            if (call.ArgumentCount == 1 && TextCell(call.GetArgument(0)))
                return TryTextFutureUse(call, target);
            if (call.ArgumentCount == 1 && geometry.RepresentationOperation(call) == LlvmRuntimeConversionOperation.IsNull
                && (FutureCell(call.GetArgument(0)) || UnionCell(call.GetArgument(0))))
            {
                EmitFutureUnionNull(call);
                return true;
            }
            if (geometry.ProcessStart(target, out int scheduling))
            {
                EmitProcessStart(call, scheduling);
                return true;
            }
            if (geometry.ProcessProjection(target) is { } projection)
            {
                LlvmValue handle = call.GetArgument(0);
                if (processHandles?.Contains(handle) != true)
                    throw Error("a process value crossing an unfinished handle carrier boundary", call);
                int import = (projection is HpirService.ProcessStdout or HpirService.ProcessStderr) && !TextCell(call)
                    ? geometry.ReadyProcessTextImport(projection == HpirService.ProcessStderr) : geometry.ProcessImport(projection);
                EmitImportCall(call, import, [Location(handle)]);
                return true;
            }
            if (geometry.ScalarDemand(target) && call.ArgumentCount == 1)
            {
                LlvmValue condition = call.GetArgument(0);
                if (FutureCell(condition)) Demand(call, condition, Location(call));
                else
                {
                    Alias(call, Location(condition));
                    ++readyDemands;
                }
                return true;
            }
            if (!FutureCell(call)) return TryReadyFormerLift(call);
            if (call.ArgumentCount == 1 && UnionCell(call.GetArgument(0))
                && geometry.RepresentationOperation(call) == LlvmRuntimeConversionOperation.Represent) return false;
            if (geometry.CooperativeNumberFront(call)) return false;
            if (geometry.FutureMath(call) is { } math && math != HpirMathOp.Invalid)
            {
                HpirFutureInput second = call.ArgumentCount > 1 ? FutureInput(call.GetArgument(1)) : HpirFutureInput.Ready;
                HpirFutureInput third = call.ArgumentCount > 2 ? FutureInput(call.GetArgument(2)) : HpirFutureInput.Ready;
                int import = geometry.FutureMathImport(math, FutureInput(call.GetArgument(0)), second, third);
                EmitImportCall(call, import, MakeLiftArguments(call));
                return true;
            }
            HpirFutureOperation operation = geometry.LiftOperation(call);
            HpirFutureInput left = FutureInput(call.GetArgument(0));
            HpirFutureInput right = call.ArgumentCount == 2 ? FutureInput(call.GetArgument(1)) : HpirFutureInput.Ready;
            EmitImportCall(call, geometry.LiftImport(operation, left, right), MakeLiftArguments(call));
            return true;
        }

        private int[] MakeLiftArguments(LlvmCallInstruction call)
        {
            var arguments = new List<int>(5);
            if (FutureInput(call) == HpirFutureInput.Either)
            {
                int address = Reserve(8, 8);
                Add(new(HpirOp.AddressOf, address, Payload(call)), call.Source);
                arguments.Add(address);
            }
            foreach (var argument in call.Arguments) AddFutureInput(arguments, argument);
            return arguments.ToArray();
        }

        private bool TryOutputFormat(LlvmCallInstruction output, LlvmFunction target)
        {
            if (deferredFormats is null || output.ArgumentCount != 1
                || output.GetArgument(0) is not LlvmCallInstruction format
                || !deferredFormats.TryGetValue(format, out var source)
                || !geometry.TryScalarImport(target, out int write)
                || !geometry.TryScalarImport(format.CalledFunction!, out int convert)) return false;

            // Formatting is pure; its sole actual text consumer owns demand.
            // Keep the observed scalar, not the earlier storage place.
            int payload = Reserve(8, 8);
            Demand(output, source, payload);
            int text = Reserve(8, 8);
            EmitCall(output, ~convert, text, [payload]);
            EmitCall(output, ~write, -1, [text], safepoint: false);
            return true;
        }

        private bool TryTextFutureUse(LlvmCallInstruction call, LlvmFunction target)
        {
            int text = Location(call.GetArgument(0));
            if (geometry.ObservedTextParse(target) is { } parse)
            {
                // The dominating Bool demand published the same completion's
                // first lane. Reading it cannot suspend or manufacture a Future.
                int readyText = Reserve(8, 8);
                Add(new(HpirOp.CopyU64, readyText, ~text), call.Source);
                EmitImportCall(call, geometry.ScalarParseImport(parse), [readyText], safepoint: false);
                return true;
            }
            if (geometry.TextProjection(call) is { } projection)
            {
                EmitCall(call, ~geometry.TextInputImport(projection), Location(call), [text]);
                return true;
            }
            if (geometry.NarrowsText(call))
            {
                Alias(call, text);
                ForwardTextTag(call, call.GetArgument(0));
                return true;
            }
            if (!geometry.TextOutput(target) || !geometry.TryScalarImport(target, out int output)) return false;
            int ready = ObserveText(call, call.GetArgument(0));
            EmitCall(call, ~output, -1, [ready], safepoint: false);
            return true;
        }

        private bool ProcessTextArgument(LlvmCallInstruction call, LlvmValue operand)
        {
            if (call.CalledFunction is not { } target || !geometry.ProcessStart(target, out _)
                || !geometry.StringValue(operand) || call.GetArgument(0) == operand) return false;
            return true;
        }

        private int pendingProcessArguments, mixedProcessArguments;

        private void EmitProcessStart(LlvmCallInstruction call, int scheduling)
        {
            var arguments = new int[call.ArgumentCount];
            var shape = new HpirProcessArgument[arguments.Length - 1];
            arguments[0] = PathRecordAddress(call.GetArgument(0), call);
            for (int i = 1; i < arguments.Length; ++i)
            {
                var argument = call.GetArgument(i);
                var kind = geometry.ProcessArgument(argument);
                if (kind == HpirProcessArgument.Text && TextCell(argument))
                {
                    shape[i - 1] = HpirProcessArgument.PendingText;
                    arguments[i] = Location(argument);
                    ++pendingProcessArguments;
                    if (geometry.MixedTextResult(argument))
                    {
                        shape[i - 1] = HpirProcessArgument.EitherText;
                        arguments[i] = ProcessEitherText(call, argument);
                        ++mixedProcessArguments;
                    }
                    continue;
                }
                if (kind == HpirProcessArgument.Invalid || FutureRoot(argument))
                    throw Error("a process argument requires a concrete Path, String, Number or Bool", call);
                shape[i - 1] = kind;
                arguments[i] = kind == HpirProcessArgument.Path ? PathRecordAddress(argument, call) : Location(argument);
            }
            ++processSites;
            processArguments += shape.Length;
            EmitImportCall(call, geometry.ProcessStartImport(scheduling, shape), arguments);
        }

        private int ProcessEitherText(LlvmCallInstruction site, LlvmValue value)
        {
            int view = Reserve(16, 8), address = Reserve(8, 8);
            Copy(HpirWidth.U64, view, Location(value), site.Source);
            Copy(HpirWidth.U8, view + HpirFutureImports.ProcessTextTag, TextTag(value), site.Source);
            Add(new(HpirOp.AddressOf, address, view), site.Source);
            return address;
        }

        private HpirFutureInput FutureInput(LlvmValue value) =>
            mixedCells?.Contains(value) == true ? HpirFutureInput.Either
                : FutureCell(value) ? HpirFutureInput.Deferred : HpirFutureInput.Ready;

        private void Demand(LlvmInstruction origin, LlvmValue cell, int output)
        {
            var arguments = new List<int>(2);
            AddFutureInput(arguments, cell);
            EmitCall(origin, ~geometry.DemandImport(Width(cell.Type), FutureInput(cell)), output, arguments.ToArray());
        }

        private int Payload(LlvmValue value)
        {
            payloads ??= [];
            if (payloads.TryGetValue(value, out int offset)) return offset;
            int width = (int)Width(value.Type);
            offset = Reserve(width, width);
            payloads.Add(value, offset);
            return offset;
        }

        private void AddFutureInput(List<int> arguments, LlvmValue value)
        {
            arguments.Add(Location(value));
            if (FutureInput(value) == HpirFutureInput.Either) arguments.Add(Payload(value));
        }

        private void LoadFutureField(LlvmLoadInstruction load, int delta)
        {
            Copy(HpirWidth.U64, Location(load), Memory(load.PointerOperand, delta, load), load.Source);
            Copy(Width(load.Type), Payload(load), Memory(load.PointerOperand), load.Source);
        }

        private void StoreFutureField(LlvmStoreInstruction store, int delta)
        {
            LlvmValue value = store.ValueOperand;
            HpirFutureInput mode = FutureInput(value);
            Copy(HpirWidth.U64, Memory(store.PointerOperand, delta, store),
                mode == HpirFutureInput.Ready ? absentFuture : Location(value), store.Source);
            if (mode != HpirFutureInput.Deferred)
                Copy(Width(value.Type), Memory(store.PointerOperand),
                    mode == HpirFutureInput.Either ? Payload(value) : Location(value), store.Source);
        }

        private void AddCarrierMoves(List<Move> moves, LlvmValue result, LlvmValue source, SourceSpan? span)
        {
            HpirFutureInput incoming = FutureInput(source);
            AddMove(Location(result), incoming == HpirFutureInput.Ready ? absentFuture : Location(source), HpirWidth.U64);
            if (FutureInput(result) == HpirFutureInput.Either && incoming != HpirFutureInput.Deferred)
                AddMove(Payload(result), incoming == HpirFutureInput.Either ? Payload(source) : Location(source), Width(result.Type));
            return;

            void AddMove(int to, int from, HpirWidth width)
            {
                if (to != from) moves.Add(new(to, from, width, span));
            }
        }
    }
}
