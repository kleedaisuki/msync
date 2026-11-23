using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmCheckpointArtifact
{
    private sealed partial class Encoder
    {
        private void Type(LlvmType type)
        {
            if (types.TryGetValue(type, out int id)) { Int(id); return; }
            id = types.Count;
            types.Add(type, id);
            Int(~id);
            writer.Write((byte)type.Id);
            switch (type)
            {
                case LlvmIntegerType integer: writer.Write(integer.BitWidth); break;
                case LlvmByteType bytes: writer.Write(bytes.BitWidth); break;
                case LlvmPointerType pointer: writer.Write(pointer.AddressSpace); break;
                case LlvmTypedPointerType pointer: writer.Write(pointer.AddressSpace); Type(pointer.ElementType); break;
                case LlvmFunctionType function:
                    Type(function.ReturnType);
                    writer.Write(function.IsVarArg);
                    Seq(function.Parameters, Type);
                    break;
                case LlvmArrayType array:
                    writer.Write(array.NumElements);
                    Type(array.ElementType);
                    break;
                case LlvmVectorType vector:
                    writer.Write(vector.ElementCount.KnownMinValue);
                    Type(vector.ElementType);
                    break;
                case LlvmStructType structure:
                    writer.Write(structure.IsLiteral);
                    writer.Write(structure.IsPacked);
                    writer.Write(structure.IsOpaque);
                    writer.Write(structure.HasNativeRecordStorage);
                    if (!structure.IsLiteral) Text(structure.Name);
                    Seq(structure.Elements, Type);
                    break;
                case LlvmTargetExtensionType extension:
                    Text(extension.Name);
                    Seq(extension.TypeParameters, Type);
                    Seq(extension.IntParameters, writer.Write);
                    break;
            }
        }

        private void APInt(LlvmAPInt value)
        {
            writer.Write(value.BitWidth);
            for (uint i = 0; i < value.NumWords; ++i) writer.Write(value.GetWord(i));
        }

        private void Range(LlvmRangeAttribute value) { APInt(value.Lower); APInt(value.Upper); }

        private void Constant(LlvmConstant value)
        {
            Type(value.Type);
            writer.Write(value.SubclassId);
            switch (value)
            {
                case LlvmConstantInt integer: APInt(integer.Value); break;
                case LlvmConstantByte bytes: APInt(bytes.Value); break;
                case LlvmConstantFP fp:
                    writer.Write((byte)fp.Value.Semantics);
                    writer.Write(fp.Value.BitcastToAPInt().GetZExtValue());
                    break;
                case LlvmConstantDataArray array: Bytes(array.GetRawDataValues()); break;
                case LlvmConstantDataVector vector: Bytes(vector.GetRawDataValues()); break;
                case LlvmConstantArray or LlvmConstantStruct or LlvmConstantVector: ConstantOperands(value); break;
                case LlvmConstantExpr expression:
                    writer.Write((byte)expression.Opcode);
                    writer.Write(expression.Flags);
                    Reference(expression.SourceElementType, Type);
                    Optional(expression.InRange, range => { APInt(range.Lower); APInt(range.Upper); });
                    Seq(expression.ShuffleMask, Int);
                    ConstantOperands(expression);
                    break;
                case LlvmUndefValue or LlvmConstantPointerNull or LlvmConstantAggregateZero: break;
                default: throw new InvalidOperationException($"Unsupported checkpoint constant {value.GetType().Name}.");
            }
            ValueInfo(value);
        }

        private void ConstantOperands(LlvmConstant value)
        {
            Int(value.OperandCount);
            for (int i = 0; i < value.OperandCount; ++i)
            {
                Id(value.GetOperand(i)!);
                UseInfo(value.GetOperandUse(i));
            }
        }
    }

    private sealed partial class Decoder
    {
        private LlvmType Type()
        {
            int id = Int();
            if (id >= 0)
                return (uint)id < (uint)types.Count && types[id] is { } known ? known : throw Invalid($"invalid type reference {id}");
            if (~id != types.Count || ++typeDepth > 256) throw Invalid("invalid or excessively nested type definition");
            types.Add(null);
            LlvmTypeId kind = EnumByte<LlvmTypeId>();
            LlvmType type = kind switch
            {
                LlvmTypeId.IntegerTyID => LlvmIntegerType.Get(context, IntWidth()),
                LlvmTypeId.ByteTyID => LlvmByteType.Get(context, IntWidth()),
                LlvmTypeId.PointerTyID => LlvmPointerType.Get(context, reader.ReadUInt32()),
                LlvmTypeId.TypedPointerTyID => ReadTypedPointer(),
                LlvmTypeId.FunctionTyID => Signature(),
                LlvmTypeId.ArrayTyID => ArrayType(),
                LlvmTypeId.FixedVectorTyID or LlvmTypeId.ScalableVectorTyID => VectorType(kind),
                LlvmTypeId.StructTyID => StructDefinition(),
                LlvmTypeId.TargetExtTyID => LlvmTargetExtensionType.Get(context, Text(), Seq(Type, 4), Seq(reader.ReadUInt32, 4)),
                _ => context.GetPrimitiveType(kind) ?? throw Invalid($"unsupported primitive type {kind}")
            };
            types[~id] = type;
            --typeDepth;
            return type;
        }

        private uint IntWidth()
        {
            uint bits = reader.ReadUInt32();
            return bits is >= LlvmIntegerType.MinIntBits and <= LlvmIntegerType.MaxIntBits
                ? bits : throw Invalid($"invalid integer width {bits}");
        }

        private LlvmTypedPointerType ReadTypedPointer()
        {
            uint space = reader.ReadUInt32();
            return LlvmTypedPointerType.Get(Type(), space);
        }

        private LlvmFunctionType Signature()
        {
            LlvmType result = Type();
            bool variadic = Bool();
            var parameters = Seq(Type, 4);
            if (!LlvmFunctionType.IsValidReturnType(result) || parameters.Any(t => !LlvmFunctionType.IsValidArgumentType(t)))
                throw Invalid("invalid function signature");
            return LlvmFunctionType.Get(result, parameters, variadic);
        }

        private LlvmArrayType ArrayType()
        {
            ulong count = reader.ReadUInt64();
            LlvmType element = Type();
            if (!LlvmArrayType.IsValidElementType(element)) throw Invalid("invalid array element");
            return LlvmArrayType.Get(element, count);
        }

        private LlvmVectorType VectorType(LlvmTypeId kind)
        {
            uint count = reader.ReadUInt32();
            LlvmType element = Type();
            if (count == 0 || !LlvmVectorType.IsValidElementType(element)) throw Invalid("invalid vector type");
            return LlvmVectorType.Get(element, new LlvmElementCount(count, kind == LlvmTypeId.ScalableVectorTyID));
        }

        private LlvmStructType Struct() => Type() as LlvmStructType ?? throw Invalid("expected a structure type");

        private LlvmStructType StructDefinition()
        {
            bool literal = Bool(), packed = Bool(), opaque = Bool(), native = Bool();
            string name = literal ? "" : Text();
            LlvmType[] elements = Seq(Type, 4);
            if (elements.Any(t => !LlvmStructType.IsValidElementType(t)) ||
                literal && (opaque || native) || opaque && (elements.Length != 0 || packed || native) || native && packed ||
                name.Length != 0 && context.GetNamedStruct(name) is not null)
                throw Invalid("invalid structure definition");
            if (literal) return LlvmStructType.Get(context, elements, packed);
            if (native) return LlvmStructType.CreateNativeRecord(context, elements, name);
            return opaque ? LlvmStructType.Create(context, name) : LlvmStructType.Create(context, elements, name, packed);
        }

        private LlvmAPInt APInt()
        {
            uint bits = IntWidth();
            int count = (int)((bits + 63) / 64);
            if (count * 8L > Remaining) throw Invalid("truncated integer");
            if (count == 1) return new(bits, reader.ReadUInt64(), implicitTrunc: true);
            var words = new ulong[count];
            for (int i = 0; i < count; ++i) words[i] = reader.ReadUInt64();
            return new(bits, words);
        }

        private LlvmRangeAttribute Range() => new(APInt(), APInt());

        private LlvmConstant Constant()
        {
            LlvmType type = Type();
            LlvmValueKind kind = EnumByte<LlvmValueKind>();
            LlvmConstant value = kind switch
            {
                LlvmValueKind.ConstantIntVal => IntegerConstant(type, APInt()),
                LlvmValueKind.ConstantByteVal => ByteConstant(type, APInt()),
                LlvmValueKind.ConstantFPVal => FloatConstant(type),
                LlvmValueKind.ConstantDataArrayVal when type is LlvmArrayType array =>
                    LlvmConstantDataArray.GetImpl(array, ConstantData(array.ElementType, array.NumElements)),
                LlvmValueKind.ConstantDataVectorVal when type is LlvmFixedVectorType vector =>
                    LlvmConstantDataVector.GetImpl(vector, ConstantData(vector.ElementType, vector.ElementCount.KnownMinValue)),
                LlvmValueKind.ConstantArrayVal when type is LlvmArrayType array => ConstantOperands(items =>
                {
                    if ((ulong)items.Length != array.NumElements || items.Any(value => value.Type != array.ElementType))
                        throw Invalid("invalid array constant elements");
                    return LlvmConstantArray.Get(array, items);
                }),
                LlvmValueKind.ConstantStructVal when type is LlvmStructType structure => ConstantOperands(items =>
                {
                    if (!items.Select(item => item.Type).SequenceEqual(structure.Elements))
                        throw Invalid("invalid structure constant elements");
                    return LlvmConstantStruct.Get(structure, items);
                }),
                LlvmValueKind.ConstantVectorVal when type is LlvmFixedVectorType vector => ConstantOperands(items =>
                {
                    if (items.Length != vector.ElementCount.KnownMinValue || items.Any(value => value.Type != vector.ElementType))
                        throw Invalid("invalid vector constant elements");
                    return LlvmConstantVector.Get(items);
                }),
                LlvmValueKind.ConstantExprVal => ConstantExpression(type),
                LlvmValueKind.UndefValueVal => LlvmUndefValue.Get(type),
                LlvmValueKind.PoisonValueVal => LlvmPoisonValue.Get(type),
                LlvmValueKind.ConstantPointerNullVal when type.IsPtrOrPtrVectorTy() => LlvmConstantPointerNull.Get(type),
                LlvmValueKind.ConstantAggregateZeroVal when type.IsAggregateType() || type.IsVectorTy() =>
                    LlvmConstantAggregateZero.Get(type),
                _ => throw Invalid($"unsupported constant {kind} for {type.Id}")
            };
            if (value.Type != type || value.SubclassId != (byte)kind) throw Invalid("constant form or type mismatch");
            ValueInfo(value);
            return value;
        }

        private LlvmConstant IntegerConstant(LlvmType type, LlvmAPInt bits) =>
            type.GetScalarType().IsIntegerTy(bits.BitWidth)
                ? type is LlvmVectorType vector ? LlvmConstantInt.Get(context, vector.ElementCount, bits) : LlvmConstantInt.Get(context, bits)
                : throw Invalid("integer constant width mismatch");

        private LlvmConstant ByteConstant(LlvmType type, LlvmAPInt bits) =>
            type.GetScalarType().IsByteTy(bits.BitWidth)
                ? type is LlvmVectorType vector ? LlvmConstantByte.Get(context, vector.ElementCount, bits) : LlvmConstantByte.Get(context, bits)
                : throw Invalid("byte constant width mismatch");

        private LlvmConstant FloatConstant(LlvmType type)
        {
            var bits = LlvmAPFloat.FromBits(EnumByte<LlvmAPFloatSemantics>(), reader.ReadUInt64());
            LlvmConstant scalar = LlvmConstantFP.Get(context, bits);
            if (scalar.Type != type.GetScalarType()) throw Invalid("floating-point constant format mismatch");
            return type is LlvmVectorType vector ? LlvmConstantFP.Get(context, vector.ElementCount, bits) : scalar;
        }

        private byte[] ConstantData(LlvmType type, ulong count)
        {
            byte[] data = Bytes();
            ulong width = type.GetScalarSizeInBits() / 8;
            if (!LlvmConstantDataArray.IsElementTypeCompatible(type) || width == 0 || (ulong)data.Length / width != count ||
                (ulong)data.Length % width != 0) throw Invalid("constant data size does not match its type");
            return data;
        }

        private LlvmConstant ConstantOperands(Func<LlvmConstant[], LlvmConstant> create)
        {
            var items = new LlvmConstant[Count(9)];
            var info = new (int Logical, LlvmStructType? Storage)[items.Length];
            for (int i = 0; i < items.Length; ++i)
            {
                items[i] = Id<LlvmConstant>();
                info[i] = (Int(), Reference(Struct));
            }
            LlvmConstant value = create(items);
            if (value.OperandCount != items.Length) throw Invalid("constant has a different operand count");
            for (int i = 0; i < items.Length; ++i)
            {
                var use = value.GetOperandUse(i);
                (use.LogicalType, use.TargetStorageType) = info[i];
            }
            return value;
        }

        private LlvmConstant ConstantExpression(LlvmType type)
        {
            var opcode = EnumByte<LlvmOpcode>();
            uint flags = reader.ReadUInt32();
            LlvmType? source = Reference(Type);
            var range = Optional(() => new LlvmConstantExprRange(APInt(), APInt()));
            int[] mask = Seq(Int, 4);
            return ConstantOperands(operands => LlvmConstantExpr.Intern(type, opcode, operands, flags, source, range, mask));
        }
    }
}
