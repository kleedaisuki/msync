using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private IReadOnlySet<int>? inheritedTextSlots;
        private HashSet<int>? newTextSlots;
        private HashSet<LlvmValue>? storedText;
        private HashSet<LlvmValue>? retainableText;
        private bool? hasFileOutput;

        internal bool HasStoredText => storedText is { Count: > 0 };
        internal bool StoredText(LlvmValue value) => storedText?.Contains(value) == true;
        internal bool SessionTextEntry(LlvmFunction function) =>
            graph.SessionGeneration is { } && IsEntry(function) && StringValueType(function);

        private bool StringValueType(LlvmFunction function) =>
            graph.SourceFunctions.TryGetValue(function, out var source) && graph.Types[source.ResultType].Kind == TypeKind.String;

        internal bool StoredTextAddress(LlvmValue pointer) =>
            (newTextSlots is not null || inheritedTextSlots is { Count: > 0 })
            && SessionStringCell(pointer) is { } cell && IsTextSlot(cell.Slot);

        private bool IsTextSlot(int slot) =>
            inheritedTextSlots?.Contains(slot) == true || newTextSlots?.Contains(slot) == true;

        private IReadOnlySet<int> TextSlots() => newTextSlots ?? inheritedTextSlots ?? new HashSet<int>();

        internal void PrepareSessionText(CompilationTrace? trace)
        {
            if (graph.SessionGeneration is not { } session) return;
            inheritedTextSlots = session.HpirLayouts?.TextSlots;
            var sources = graph.NativeCalls.Where(pair => pair.Value is "process.stdout" or "process.stderr").ToArray();
            if (sources.Length == 0 && !HasFileText && inheritedTextSlots is not { Count: > 0 }) return;
            var cells = graph.SessionCells.Where(cell => graph.Types[cell.Type].Kind == TypeKind.String && !cell.Aliased)
                .ToDictionary(cell => cell.Slot);
            HashSet<int> linked = [];
            retainableText = [];
            Queue<LlvmValue> queue = new();
            foreach (var (slot, cell) in cells)
                if (IsTextSlot(slot)) Link(cell, null);
            foreach (var (target, _) in sources)
                foreach (var user in target.Users)
                    if (user is LlvmCallInstruction call && ReferenceEquals(call.CalledFunction, target)) Enqueue(call);
            int reads = 0;
            if (HasFileText)
                foreach (var function in entries.Keys)
                foreach (var block in function.BasicBlocks)
                foreach (var instruction in block.Instructions)
                    if (instruction is LlvmFileInInst { Format: LlvmFileInFormat.Text } input)
                    {
                        Enqueue(input);
                        ++reads;
                    }
            while (queue.TryDequeue(out var value))
            {
                foreach (var user in value.Users)
                {
                    if (user is LlvmStoreInstruction store && store.ValueOperand == value
                        && SessionStringCell(store.PointerOperand) is { } cell)
                    {
                        if (!cells[cell.Slot].Captured || value is not LlvmFileInInst)
                            Link(cells[cell.Slot], store.Source);
                    }
                    else if (user is LlvmCallInstruction narrow && NarrowsText(narrow))
                    {
                        if (StoredText(value)) (storedText ??= []).Add(narrow);
                        Enqueue(narrow);
                    }
                }
            }
            trace?.Metric("session.text.effect_producers", sources.Length);
            trace?.Metric("session.text.file_read_producers", reads);
            trace?.Metric("session.text.slots_linked", linked.Count);
            trace?.Metric("session.text.stored_operands", storedText?.Count ?? 0);

            void Link(LlvmSessionCellInfo cell, SourceSpan? source)
            {
                if (!linked.Add(cell.Slot)) return;
                if (cell.Captured)
                    throw new MsyncException("Stored Text completion cannot yet cross an existing closure capture.", source);
                if (!IsTextSlot(cell.Slot))
                    (newTextSlots ??= inheritedTextSlots is null ? [] : new(inheritedTextSlots)).Add(cell.Slot);
                LinkAddress(cell.Global);
            }

            void LinkAddress(LlvmValue pointer)
            {
                foreach (var user in pointer.Users)
                {
                    switch (user)
                    {
                        case LlvmGetElementPtrInstruction { HasAllZeroIndices: true } gep when gep.PointerOperand == pointer:
                            LinkAddress(gep);
                            break;
                        case LlvmLoadInstruction load when load.PointerOperand == pointer:
                            (storedText ??= []).Add(load);
                            Enqueue(load);
                            break;
                        case LlvmStoreInstruction store when store.PointerOperand == pointer:
                            break;
                        default:
                            throw new MsyncException("Stored Text completion cannot yet cross a captured address boundary.",
                                (user as LlvmInstruction)?.Source);
                    }
                }
            }

            void Enqueue(LlvmValue value)
            {
                if (retainableText.Add(value)) queue.Enqueue(value);
            }
        }

        private HpirSessionCell? SessionStringCell(LlvmValue pointer)
            => CanonicalCell(pointer) is { Kind: TypeKind.String } cell ? cell : null;

        private HpirSessionCell? CanonicalCell(LlvmValue pointer)
        {
            while (pointer is LlvmGetElementPtrInstruction { HasAllZeroIndices: true } gep)
                pointer = gep.PointerOperand;
            if (pointer is not LlvmGlobalVariable global
                || !GlobalOrigins().TryGetValue(global, out var origin) || origin.Guard)
                return null;
            return origin.Cell is { Aliased: false } cell ? cell : null;
        }

        internal bool ReadySessionScalarStore(LlvmStoreInstruction store) =>
            IsPersistentSession && CanonicalCell(store.PointerOperand) is { Kind: TypeKind.Number or TypeKind.Bool }
            && (store.ValueOperand.Type.IsDoubleTy() || store.ValueOperand.Type.IsIntegerTy(1))
            && OwnsScalarPublication(store.ValueOperand);

        internal bool ReadySessionUnionStore(LlvmStoreInstruction store) =>
            IsPersistentSession && CanonicalCell(store.PointerOperand) is { Union: not null }
            && OwnsScalarPublication(store.ValueOperand);

        private bool OwnsScalarPublication(LlvmValue value)
        {
            // A ready-only cell must not wait for work that a later input
            // or a later effect in this input has not yet admitted.
            while (value is LlvmCallInstruction { CalledFunction: { } target } call)
            {
                if (FileMetadata(target) != HpirFileMetadata.Invalid)
                {
                    hasFileOutput ??= HasLiveTransferSources || entries.Keys.Any(static function => function.BasicBlocks
                        .Any(static block => block.Instructions.Any(static instruction => instruction is LlvmFileOutInst)));
                    return hasFileOutput == false;
                }
                if (call.ArgumentCount != 1 || RepresentationOperation(call) != LlvmRuntimeConversionOperation.Represent)
                    break;
                value = call.GetArgument(0);
            }
            return false;
        }

        internal bool ReadySessionTextStore(LlvmStoreInstruction store) =>
            IsPersistentSession && StringValue(store.ValueOperand)
            && (!StoredTextAddress(store.PointerOperand) || retainableText?.Contains(store.ValueOperand) != true);

        internal HpirSessionUnion? PromptInputStore(LlvmStoreInstruction store) =>
            IsPersistentSession && store.ValueOperand is LlvmCallInstruction { CalledFunction: { } target }
                && StandardTextTarget(target) == HpirService.ReadLineText
                && CanonicalCell(store.PointerOperand) is { Kind: TypeKind.String | TypeKind.Null, Union: { } union }
                ? union : null;

        internal int ReadyTextLayoutTest() => PhysicalLayoutImport(TextLayout());
    }

    private sealed partial class Body
    {
        private int storedTextDemands, promptInputStores;

        private bool TryStorePromptInput(LlvmStoreInstruction store)
        {
            if (geometry.PromptInputStore(store) is not { } union) return false;
            // The next REPL command and io.read_line share the same input owner.
            int text = ObserveText(store, store.ValueOperand);
            int tag = Memory(store.PointerOperand), zero = FormatImmediate(store, 0), eof = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, tag, Immediate: Tag(TypeKind.String)), store.Source);
            Add(new(HpirOp.EqU64, eof, text, zero), store.Source);
            int tested = CursorBranch(eof, store.Source);
            Add(new(HpirOp.ConstU8, tag, Immediate: Tag(TypeKind.Null)), store.Source);
            LinkCursorBranch(tested, tested + 1, code.Count);
            Copy(HpirWidth.U64, Memory(store.PointerOperand, union.PointerOffset, store), text, store.Source);
            ++promptInputStores;
            return true;
        }

        private int ObserveText(LlvmInstruction site, LlvmValue value)
        {
            int text = Location(value), ready = Reserve(8, 8);
            int wait = geometry.TextInputImport(HpirService.DemandText);
            if (geometry.MixedTextResult(value) || geometry.StoredText(value))
            {
                if (geometry.StoredText(value)) ++storedTextDemands;
                SelectTextCarrier(site, value, pending =>
                {
                    if (pending) EmitCall(site, ~wait, ready, [text]);
                    else Copy(HpirWidth.U64, ready, text, site.Source);
                });
                return ready;
            }
            EmitCall(site, ~wait, ready, [text]);
            return ready;
        }
    }
}
