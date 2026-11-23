using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmCheckpointArtifact
{
    private sealed partial class Encoder
    {
        private readonly List<LlvmValue> values = [];
        private readonly Dictionary<LlvmValue, int> ids = [];
        private readonly Dictionary<LlvmType, int> types = [];
        private readonly BinaryWriter writer;
        internal Encoder(BinaryWriter writer) => this.writer = writer;

        internal int ValueCount => values.Count;
        internal int TypeCount => types.Count;
        internal void Int(int value) => writer.Write(value);
        internal void Text(string value) => Bytes(HirArtifact.Utf8.GetBytes(value));
        private void Bytes(ReadOnlySpan<byte> bytes) { Int(bytes.Length); writer.Write(bytes); }
        private void Seq<T>(IReadOnlyCollection<T> items, Action<T> write)
        {
            Int(items.Count);
            foreach (T item in items) write(item);
        }
        private void Optional<T>(T? value, Action<T> write) where T : struct
        {
            writer.Write(value.HasValue);
            if (value.HasValue) write(value.Value);
        }
        private void Reference<T>(T? value, Action<T> write) where T : class
        {
            writer.Write(value is not null);
            if (value is not null) write(value);
        }

        private void Declare(LlvmValue value)
        {
            ids.Add(value, values.Count);
            values.Add(value);
        }
        private void Id(LlvmValue value) => Int(ids.TryGetValue(value, out int id) ? id
            : throw new InvalidOperationException($"Unowned SSA value in checkpoint: {value.Name}."));
        private void Collect(LlvmConstant constant)
        {
            if (ids.ContainsKey(constant)) return;
            for (int i = 0; i < constant.OperandCount; ++i) Collect((LlvmConstant)constant.GetOperand(i)!);
            Declare(constant);
        }

        internal void Graph(LlvmModule module, CompilationTrace? trace)
        {
            foreach (var global in module.GlobalVariables) Declare(global);
            foreach (var function in module.Functions)
            {
                Declare(function);
                foreach (var argument in function.Arguments) Declare(argument);
                foreach (var block in function.BasicBlocks)
                {
                    Declare(block);
                    foreach (var instruction in block.Instructions) Declare(instruction);
                }
            }
            int firstConstant = values.Count;
            foreach (var global in module.GlobalVariables)
                if (global.Initializer is { } initializer) Collect(initializer);
            for (int i = 0; i < firstConstant; ++i)
            {
                if (values[i] is not LlvmInstruction instruction) continue;
                for (int operand = 0; operand < instruction.OperandCount; ++operand)
                    if (instruction.GetOperand(operand) is LlvmConstant constant) Collect(constant);
                if (instruction is LlvmSwitchInstruction @switch)
                    for (int c = 0; c < @switch.CaseCount; ++c) Collect(@switch.GetCaseValue(c));
            }

            Text(module.ModuleIdentifier);
            Text(module.SourceFileName);
            Text(module.TargetTriple.ToString());
            Text(module.DataLayout.StringRepresentation);
            writer.Write(module.SemanticInterposition);
            Optional(module.ExplicitThreadModel, model => writer.Write((byte)model));
            writer.Write(module.Context.ShouldDiscardValueNames);
            Int(module.Context.NonGlobalValueMaxNameSize);
            writer.Write(module.Context.UseConstantIntForFixedLengthSplat);
            writer.Write(module.Context.UseConstantIntForScalableSplat);
            writer.Write(module.Context.NamedStructTypesUniqueId);
            writer.Write(module.ValueSymbolTable.LastUnique);
            Seq(module.GlobalVariables, global =>
            {
                Global(global);
                writer.Write(global.IsConstant);
                writer.Write(global.IsExternallyInitialized);
            });
            Seq(module.Functions, function =>
            {
                Global(function);
                Function(function);
                writer.Write(function.NextBlockNum);
                writer.Write(function.ValueSymbolTable?.LastUnique ?? 0);
                foreach (var argument in function.Arguments) ValueInfo(argument);
                Seq(function.BasicBlocks, block =>
                {
                    ValueInfo(block);
                    writer.Write(block.Number);
                    Seq(block.Instructions, instruction => Type(instruction.Type));
                });
            });
            Int(values.Count - firstConstant);
            for (int i = firstConstant; i < values.Count; ++i) Constant((LlvmConstant)values[i]);

            foreach (var global in module.GlobalVariables)
            {
                Reference(global.Initializer, Id);
                if (global.HasInitializer) UseInfo(global.GetOperandUse(0));
            }
            for (int i = 0; i < firstConstant; ++i)
                if (values[i] is LlvmInstruction instruction) Instruction(instruction);

            int useCount = 0;
            foreach (var value in values)
            {
                if (!value.HasUseList) continue;
                var uses = value.Uses.Where(use => ids.ContainsKey(use.User)).ToArray();
                Seq(uses, use => { Id(use.User); Int(use.OperandNo); });
                useCount += uses.Length;
            }
            trace?.Metric("graph.constants", values.Count - firstConstant);
            trace?.Metric("graph.use_edges", useCount);
            GraphMetrics(module, trace);
        }

        private void ValueInfo(LlvmValue value)
        {
            Bytes(value.NameBytes.Span);
            Int(value.LogicalType);
            writer.Write(value.SubclassOptionalData);
        }

        private void Global(LlvmGlobalObject value)
        {
            Type(value.ValueType);
            writer.Write(value.AddressSpace);
            writer.Write((byte)value.Linkage);
            writer.Write((byte)value.Visibility);
            writer.Write((byte)value.DllStorageClass);
            writer.Write((byte)value.ThreadLocalMode);
            writer.Write((byte)value.UnnamedAddr);
            writer.Write(value.IsDSOLocal);
            writer.Write(value.Alignment?.Value ?? 0UL);
            ValueInfo(value);
        }

        private void UseInfo(LlvmUse use)
        {
            Int(use.LogicalType);
            Reference(use.TargetStorageType, Type);
        }
    }

    private sealed partial class Decoder
    {
        private readonly LlvmContext context = new();
        private readonly List<LlvmType?> types = [];
        private readonly List<LlvmValue> values = [];
        private readonly List<(int Id, LlvmBasicBlock Block)> instructions = [];
        private readonly Dictionary<LlvmValue, int> forward = [];
        private readonly List<(LlvmInstruction Instruction, int Operand, int Id)> fixups = [];
        private int typeDepth;
        private readonly BinaryReader reader;
        private readonly long extent;
        internal Decoder(BinaryReader reader) => (this.reader, extent) = (reader, reader.BaseStream.Length);
        private long Remaining => extent - reader.BaseStream.Position;

        internal int ValueCount => values.Count;
        internal int TypeCount => types.Count;
        private int Int() => reader.ReadInt32();
        private bool Bool() => reader.ReadByte() switch
        {
            0 => false, 1 => true, _ => throw Invalid("invalid Boolean")
        };
        private int Count(int minimumBytes = 1)
        {
            int count = Int();
            if (count < 0 || (long)count * minimumBytes > Remaining)
                throw Invalid($"invalid count {count} or truncated table");
            return count;
        }
        private T[] Seq<T>(Func<T> read, int minimumBytes = 1)
        {
            var items = new T[Count(minimumBytes)];
            for (int i = 0; i < items.Length; ++i) items[i] = read();
            return items;
        }
        private T? Optional<T>(Func<T> read) where T : struct => Bool() ? read() : null;
        private T? Reference<T>(Func<T> read) where T : class => Bool() ? read() : null;
        private byte[] Bytes() => reader.ReadBytes(Count());
        internal string Text() => HirArtifact.Utf8.GetString(Bytes());
        private T EnumByte<T>() where T : struct, Enum
        {
            byte value = reader.ReadByte();
            T result = (T)Enum.ToObject(typeof(T), value);
            if (!Enum.IsDefined(result)) throw Invalid($"unknown {typeof(T).Name} {value}");
            return result;
        }

        private T At<T>(int id) where T : LlvmValue =>
            (uint)id < (uint)values.Count && values[id] is T value && value is not ForwardValue ? value
                : throw Invalid($"invalid {typeof(T).Name} reference {id}");
        private T Id<T>() where T : LlvmValue => At<T>(Int());
        private LlvmValue Operand()
        {
            int id = Int();
            if ((uint)id >= (uint)values.Count) throw Invalid($"invalid operand reference {id}");
            return values[id];
        }

        internal LlvmModule Graph(CompilationTrace? trace)
        {
            LlvmModule module = new(Text(), context)
            {
                SourceFileName = Text(), TargetTriple = new(Text()), DataLayout = new(Text()),
                SemanticInterposition = Bool()
            };
            if (Optional(EnumByte<LlvmThreadModel>) is { } model) module.SetThreadModel(model);
            context.SetDiscardValueNames(Bool());
            context.NonGlobalValueMaxNameSize = Int();
            context.UseConstantIntForFixedLengthSplat = Bool();
            context.UseConstantIntForScalableSplat = Bool();
            context.NamedStructTypesUniqueId = reader.ReadUInt32();
            module.ValueSymbolTable.LastUnique = reader.ReadUInt32();
            for (int i = Count(); i != 0; --i) ReadGlobal(module);
            for (int i = Count(); i != 0; --i) ReadFunction(module);
            int constantCount = Count();
            for (int i = 0; i < constantCount; ++i) values.Add(Constant());
            foreach (var global in module.GlobalVariables)
            {
                global.Initializer = Reference(Id<LlvmConstant>);
                if (global.Initializer is not { } initializer) continue;
                if (initializer.Type != global.ValueType) throw Invalid("global initializer has a different type");
                UseInfo(global.GetOperandUse(0));
            }
            foreach (var (id, block) in instructions)
            {
                var instruction = Instruction(values[id].Type, block);
                block.Append(instruction);
                ValueInfo(instruction);
                values[id] = instruction;
            }
            foreach (var (instruction, operand, id) in fixups)
            {
                LlvmValue value = At<LlvmInstruction>(id);
                CheckLocal(value, instruction.Parent!);
                instruction.SetOperand(operand, value);
            }
            foreach (var function in module.Functions)
                foreach (var block in function.BasicBlocks)
                    if (!block.HasTerminator || block.Instructions.Take(block.Instructions.Count - 1).Any(i => i.IsTerminator))
                        throw Invalid($"incomplete basic block in function '{function.Name}'");
            foreach (var value in values)
                if (value.HasUseList) ReadUseOrder(value);
            trace?.Metric("graph.constants", constantCount);
            trace?.Metric("graph.forward_edges", fixups.Count);
            GraphMetrics(module, trace);
            return module;
        }

        private void ReadUseOrder(LlvmValue value)
        {
            int count = Count(8);
            if (count != value.GetNumUses()) throw Invalid("incomplete use list");
            LlvmUse? previous = null;
            // Constructors register uses in decode order, which need not be producer order.
            for (int i = 0; i < count; ++i)
            {
                var user = Id<LlvmUser>();
                int index = Int();
                if ((uint)index >= (uint)user.OperandCount) throw Invalid("invalid use index");
                var use = user.GetOperandUse(index);
                if (!ReferenceEquals(use.Value, value) || !use.Linked) throw Invalid("invalid or duplicate use");
                use.Linked = false;
                use.Previous = previous;
                if (previous is null) value.FirstUse = use;
                else previous.Next = use;
                previous = use;
            }
            if (previous is not null) previous.Next = null;
            else value.FirstUse = null;
            for (var use = value.FirstUse; use is not null; use = use.Next) use.Linked = true;
        }

        private void ValueInfo(LlvmValue value)
        {
            byte[] name = Bytes();
            value.SetName(name);
            if (!value.NameBytes.Span.SequenceEqual(name)) throw Invalid("invalid or duplicate SSA name");
            value.LogicalType = Int();
            value.SubclassOptionalData = reader.ReadByte();
        }

        private void ReadGlobal(LlvmModule module)
        {
            var type = Type();
            var global = new LlvmGlobalVariable(type, false, LlvmLinkage.ExternalLinkage, addressSpace: reader.ReadUInt32());
            module.InsertGlobalVariable(global);
            values.Add(global);
            Global(global);
            global.IsConstant = Bool();
            global.IsExternallyInitialized = Bool();
        }

        private void ReadFunction(LlvmModule module)
        {
            var type = Type() as LlvmFunctionType ?? throw Invalid("function has no signature");
            var function = new LlvmFunction(type, LlvmLinkage.ExternalLinkage, reader.ReadUInt32(), parent: module);
            values.Add(function);
            Global(function);
            Function(function);
            uint nextBlock = reader.ReadUInt32();
            uint unique = reader.ReadUInt32();
            if (function.ValueSymbolTable is { } symbols) symbols.LastUnique = unique;
            foreach (var argument in function.Arguments)
            {
                values.Add(argument);
                ValueInfo(argument);
            }
            HashSet<uint> blockNumbers = [];
            for (int i = Count(); i != 0; --i)
            {
                var block = new LlvmBasicBlock(context, parent: function);
                values.Add(block);
                ValueInfo(block);
                block.Number = reader.ReadUInt32();
                if (block.Number >= nextBlock || !blockNumbers.Add(block.Number)) throw Invalid("invalid function block number");
                for (int j = Count(4); j != 0; --j)
                {
                    int id = values.Count;
                    var placeholder = new ForwardValue(Type());
                    instructions.Add((id, block));
                    values.Add(placeholder);
                    forward.Add(placeholder, id);
                }
            }
            function.NextBlockNum = nextBlock;
        }

        private void Global(LlvmGlobalObject global)
        {
            global.Linkage = EnumByte<LlvmLinkage>();
            global.Visibility = EnumByte<LlvmVisibility>();
            global.DllStorageClass = EnumByte<LlvmDllStorageClass>();
            global.ThreadLocalMode = EnumByte<LlvmThreadLocalMode>();
            global.UnnamedAddr = EnumByte<LlvmUnnamedAddr>();
            global.IsDSOLocal = Bool();
            ulong align = reader.ReadUInt64();
            global.Alignment = align == 0 ? null : Alignment(align);
            ValueInfo(global);
        }

        private static void CheckLocal(LlvmValue value, LlvmBasicBlock block)
        {
            if (value is LlvmBasicBlock target && target.Parent != block.Parent ||
                value is LlvmInstruction instruction && instruction.Parent?.Parent != block.Parent ||
                value is LlvmArgument argument && argument.Parent != block.Parent)
                throw Invalid("operand references a different function");
        }

        private void UseInfo(LlvmUse use)
        {
            use.LogicalType = Int();
            use.TargetStorageType = Reference(Struct);
        }

        private sealed class ForwardValue(LlvmType type) : LlvmValue(type, (byte)LlvmValueKind.ArgumentVal);
    }
    private static void GraphMetrics(LlvmModule module, CompilationTrace? trace)
    {
        if (trace is null) return;
        int blocks = 0, instructions = 0;
        foreach (var function in module.Functions)
        {
            blocks += function.BasicBlocks.Count;
            foreach (var block in function.BasicBlocks) instructions += block.Instructions.Count;
        }
        trace.Metric("graph.blocks", blocks);
        trace.Metric("graph.instructions", instructions);
        trace.Metric("graph.globals", module.GlobalVariables.Count);
    }

}
