using System.Globalization;
using System.Numerics;
using System.Text;

namespace Msync.Core.Llvm;

public static partial class LlvmIrWriter
{
    public static string Write(LlvmModule module, CompilationTrace? trace = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Write(module, output, trace);
        return output.ToString();
    }

    public static void Write(LlvmModule module, TextWriter output, CompilationTrace? trace = null)
    {
        var writer = new Writer(module, output, trace?.Detail);
        if (trace is null) writer.WriteModule();
        else trace.Run("compiler.llvm_ir.print", writer, static state => state.WriteModule(),
            new Dictionary<string, string> { ["module.name"] = module.ModuleIdentifier });
    }

    private sealed partial class Writer(LlvmModule module, TextWriter output, CompilationTrace? trace)
    {
        private readonly Dictionary<LlvmValue, int> slots = [];
        private readonly Dictionary<LlvmGlobalValue, int> globalSlots = [];

        internal void WriteModule()
        {
            output.WriteLine("; msync LLVM-derived IR: heapalloc, file.in, file.out are project extensions");
            output.WriteLine($"source_filename = {Quote(Encoding.UTF8.GetBytes(module.SourceFileName))}");
            if (module.DataLayout.StringRepresentation.Length != 0)
                output.WriteLine($"target datalayout = {Quote(Encoding.UTF8.GetBytes(module.DataLayout.StringRepresentation))}");
            if (module.TargetTriple.Data.Length != 0)
                output.WriteLine($"target triple = {Quote(Encoding.UTF8.GetBytes(module.TargetTriple.Data))}");
            foreach (var global in module.GlobalVariables.Cast<LlvmGlobalValue>().Concat(module.Functions))
                if (!global.HasName) globalSlots.Add(global, globalSlots.Count);
            WriteStructTypes();
            foreach (var global in module.GlobalVariables) WriteGlobal(global);
            foreach (var function in module.Functions) WriteFunctionObserved(function);
            if (hasInvariantLoadMetadata) output.WriteLine("!0 = !{}");
            trace?.Metric("llvm.functions", module.Functions.Count);
            trace?.Metric("llvm.globals", module.GlobalVariables.Count);
        }

        private void WriteStructTypes()
        {
            var seen = new HashSet<LlvmType>();
            var types = new List<LlvmType>();
            foreach (var global in module.GlobalVariables) CollectType(global.ValueType, seen, types);
            foreach (var function in module.Functions)
            {
                CollectType(function.FunctionType, seen, types);
                foreach (var instruction in function.BasicBlocks.SelectMany(block => block.Instructions))
                {
                    CollectType(instruction.Type, seen, types);
                    foreach (var operand in instruction.Operands)
                        if (operand is not null) CollectType(operand.Type, seen, types);
                    if (instruction is LlvmGetElementPtrInstruction gep) CollectType(gep.SourceElementType, seen, types);
                    if (instruction is LlvmAllocaInstruction alloca) CollectType(alloca.AllocatedType, seen, types);
                }
            }
            foreach (var structure in types.OfType<LlvmStructType>().Where(type => !type.IsLiteral))
                output.WriteLine($"{Type(structure)} = type {StructBody(structure)}");
        }

        private static void CollectType(LlvmType type, HashSet<LlvmType> seen, List<LlvmType> result)
        {
            if (!seen.Add(type)) return;
            result.Add(type);
            foreach (var subtype in type.Subtypes) CollectType(subtype, seen, result);
        }

        private void WriteGlobal(LlvmGlobalVariable global)
        {
            string initializer = global.Initializer is { } value ? " " + Reference(value) : "";
            string storage = global.IsDeclaration ? "external " : Linkage(global.Linkage);
            output.WriteLine($"{Reference(global)} = {storage}{(global.IsConstant ? "constant" : "global")} {Type(global.ValueType)}{initializer}");
        }

        private void WriteFunctionObserved(LlvmFunction function)
        {
            if (trace is null) { WriteFunction(function); return; }
            trace.Run("compiler.llvm_ir.print.function", (self: this, function),
                static state => state.self.WriteFunction(state.function),
                new Dictionary<string, string> { ["function.name"] = function.Name });
        }

        private void WriteFunction(LlvmFunction function)
        {
            slots.Clear();
            foreach (var argument in function.Arguments) AddSlot(argument);
            foreach (var block in function.BasicBlocks)
            {
                AddSlot(block);
                foreach (var instruction in block.Instructions)
                    if (!instruction.Type.IsVoidTy()) AddSlot(instruction);
            }
            string parameters = string.Join(", ", function.Arguments.Select(Typed));
            if (function.IsVarArg) parameters += parameters.Length == 0 ? "..." : ", ...";
            string definition = function.IsDeclaration ? "declare" : "define";
            output.Write($"{definition} {Linkage(function.Linkage)}{Type(function.ReturnType)} {Reference(function)}({parameters})");
            if (function.IsDeclaration) { output.WriteLine(); return; }
            output.WriteLine(" {");
            int instructions = 0, heap = 0, fileIn = 0, fileOut = 0;
            foreach (var block in function.BasicBlocks)
            {
                output.WriteLine($"{LocalName(block)}:");
                foreach (var instruction in block.Instructions)
                {
                    WriteInstruction(instruction);
                    instructions++;
                    if (instruction.Opcode == LlvmOpcode.HeapAlloc) heap++;
                    if (instruction.Opcode == LlvmOpcode.FileIn) fileIn++;
                    if (instruction.Opcode == LlvmOpcode.FileOut) fileOut++;
                }
            }
            output.WriteLine("}");
            trace?.Metric("llvm.blocks", function.BasicBlocks.Count);
            trace?.Metric("llvm.instructions", instructions);
            trace?.Metric("llvm.heap_alloc", heap);
            trace?.Metric("llvm.file_in", fileIn);
            trace?.Metric("llvm.file_out", fileOut);
        }

        private void AddSlot(LlvmValue value)
        {
            if (!value.HasName) slots.Add(value, slots.Count);
        }

        private string LocalName(LlvmValue value) => value.HasName
            ? Quote(value.NameBytes.Span) : slots[value].ToString(CultureInfo.InvariantCulture);
        private string Typed(LlvmValue value) => Type(value.Type) + " " + Reference(value);
        private string Reference(LlvmValue value) => value switch
        {
            LlvmGlobalValue global => "@" + (global.HasName ? Quote(global.NameBytes.Span) : globalSlots[global].ToString(CultureInfo.InvariantCulture)),
            LlvmConstantInt integer => Integer(integer.Value),
            LlvmConstantByte bytes => Integer(bytes.Value),
            LlvmConstantFP floating => Floating(floating),
            LlvmConstantPointerNull => "null",
            LlvmConstantAggregateZero => "zeroinitializer",
            LlvmPoisonValue => "poison",
            LlvmUndefValue => "undef",
            LlvmConstantExpr expression => ConstantExpression(expression),
            LlvmConstantVector vector => "<" + string.Join(", ", vector.Operands.Select(value => Typed(value!))) + ">",
            LlvmConstantDataVector vector => "<" + string.Join(", ", Enumerable.Range(0, (int)((LlvmFixedVectorType)vector.Type).NumElements)
                .Select(index => Typed(vector.GetElementAsConstant((uint)index)))) + ">",
            LlvmConstantStruct structure => Aggregate(structure),
            LlvmConstantArray array => "[ " + string.Join(", ", array.Operands.Select(value => Typed(value!))) + " ]",
            LlvmConstantDataArray data => DataArray(data),
            LlvmConstant constant => throw new NotSupportedException($"Text rendering is not implemented for LLVM constant {constant.GetType().Name}."),
            _ => "%" + LocalName(value)
        };
        private string ConstantExpression(LlvmConstantExpr expression)
        {
            string opcode = expression.Opcode.ToString().ToLowerInvariant();
            if (expression.IsCast)
                return opcode + " (" + Typed(expression.GetConstantOperand(0)) + " to " + Type(expression.Type) + ")";
            if (expression.Opcode == LlvmOpcode.GetElementPtr)
                return opcode + (expression.NoWrapFlags.IsInBounds ? " inbounds" :
                    (expression.NoWrapFlags.HasNoUnsignedSignedWrap ? " nusw" : "")) +
                    (expression.NoWrapFlags.HasNoUnsignedWrap ? " nuw" : "") +
                    (expression.InRange is { } range ? " inrange(" + Integer(range.Lower) + ", " + Integer(range.Upper) + ")" : "") +
                    " (" + Type(expression.SourceElementType!) + ", " +
                    string.Join(", ", expression.Operands.Select(value => Typed(value!))) + ")";
            if (expression.Opcode == LlvmOpcode.ShuffleVector)
                return opcode + " (" + Typed(expression.GetConstantOperand(0)) + ", " + Typed(expression.GetConstantOperand(1)) +
                    ", <" + expression.ShuffleMask.Count + " x i32> <" +
                    string.Join(", ", expression.ShuffleMask.Select(lane => lane == -1 ? "i32 poison" : "i32 " + lane)) + ">)";
            string flags = expression.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub
                ? (expression.HasNoUnsignedWrap ? " nuw" : "") + (expression.HasNoSignedWrap ? " nsw" : "") : "";
            return opcode + flags + " (" + string.Join(", ", expression.Operands.Select(value => Typed(value!))) + ")";
        }
        private string Aggregate(LlvmConstantStruct structure)
        {
            string fields = "{ " + string.Join(", ", structure.Operands.Select(value => Typed(value!))) + " }";
            return structure.StructType.IsPacked ? "<" + fields + ">" : fields;
        }
        private string DataArray(LlvmConstantDataArray data) => data.ElementType.IsIntegerTy(8)
            ? "c" + Quote(data.GetRawDataValues())
            : "[ " + string.Join(", ", Enumerable.Range(0, (int)data.NumElements)
                .Select(index => Typed(data.GetElementAsConstant((uint)index)))) + " ]";
        private static string Integer(LlvmAPInt value)
        {
            BigInteger number = BigInteger.Zero;
            for (uint i = value.NumWords; i != 0; i--) number = (number << 64) | value.GetWord(i - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        private static string Type(LlvmType type) => type switch
        {
            LlvmIntegerType integer => "i" + integer.BitWidth.ToString(CultureInfo.InvariantCulture),
            LlvmByteType bytes => "b" + bytes.BitWidth.ToString(CultureInfo.InvariantCulture),
            LlvmPointerType pointer => pointer.AddressSpace == 0 ? "ptr" : $"ptr addrspace({pointer.AddressSpace})",
            LlvmArrayType array => $"[{array.NumElements} x {Type(array.ElementType)}]",
            LlvmVectorType vector => $"<{(vector.ElementCount.IsScalable ? "vscale x " : "")}{vector.ElementCount.KnownMinValue} x {Type(vector.ElementType)}>",
            LlvmStructType structure => structure.IsLiteral ? StructBody(structure) : "%" + Quote(Encoding.UTF8.GetBytes(structure.Name)),
            LlvmFunctionType function => Type(function.ReturnType) + " (" + string.Join(", ", function.Parameters.Select(Type))
                + (function.IsVarArg ? (function.NumParams == 0 ? "..." : ", ...") : "") + ")",
            LlvmTargetExtensionType extension => "target(" + Quote(Encoding.UTF8.GetBytes(extension.Name))
                + string.Concat(extension.TypeParameters.Select(parameter => ", " + Type(parameter)))
                + string.Concat(extension.IntParameters.Select(parameter => ", " + parameter.ToString(CultureInfo.InvariantCulture))) + ")",
            _ => type.Id switch
            {
                LlvmTypeId.VoidTyID => "void", LlvmTypeId.LabelTyID => "label", LlvmTypeId.HalfTyID => "half",
                LlvmTypeId.BFloatTyID => "bfloat", LlvmTypeId.FloatTyID => "float", LlvmTypeId.DoubleTyID => "double",
                LlvmTypeId.X86_FP80TyID => "x86_fp80", LlvmTypeId.FP128TyID => "fp128", LlvmTypeId.PPC_FP128TyID => "ppc_fp128",
                LlvmTypeId.MetadataTyID => "metadata", LlvmTypeId.TokenTyID => "token", LlvmTypeId.X86_AMXTyID => "x86_amx",
                _ => throw new NotSupportedException($"Text rendering is not implemented for LLVM type {type.Id}.")
            }
        };
        private static string StructBody(LlvmStructType structure)
        {
            if (structure.IsOpaque) return "opaque";
            string fields = "{ " + string.Join(", ", structure.Elements.Select(Type)) + " }";
            return structure.IsPacked ? "<" + fields + ">" : fields;
        }
        private static string Linkage(LlvmLinkage linkage) => linkage switch
        {
            LlvmLinkage.ExternalLinkage => "", LlvmLinkage.InternalLinkage => "internal ",
            LlvmLinkage.PrivateLinkage => "private ", LlvmLinkage.AvailableExternallyLinkage => "available_externally ",
            LlvmLinkage.LinkOnceAnyLinkage => "linkonce ", LlvmLinkage.LinkOnceODRLinkage => "linkonce_odr ",
            LlvmLinkage.WeakAnyLinkage => "weak ", LlvmLinkage.WeakODRLinkage => "weak_odr ",
            LlvmLinkage.AppendingLinkage => "appending ", LlvmLinkage.ExternalWeakLinkage => "extern_weak ",
            LlvmLinkage.CommonLinkage => "common ", _ => throw new ArgumentOutOfRangeException(nameof(linkage))
        };
        private static string Quote(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder("\"");
            foreach (byte value in bytes)
            {
                if (value is >= 32 and <= 126 && value is not ((byte)'"') and not ((byte)'\\')) text.Append((char)value);
                else text.Append('\\').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
            return text.Append('"').ToString();
        }
    }
}
