using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmCheckpointArtifact
{
    private sealed partial class Encoder
    {
        private void Flags(params bool[] flags)
        {
            uint bits = 0;
            for (int i = 0; i < flags.Length; ++i)
                if (flags[i]) bits |= 1U << i;
            writer.Write(bits);
        }

        private void Attributes(LlvmParameterAttributes attrs)
        {
            writer.Write((ulong)attrs.Flags);
            writer.Write(attrs.Alignment?.Value ?? 0UL);
            Optional(attrs.DereferenceableBytes, writer.Write);
            Optional(attrs.DereferenceableOrNullBytes, writer.Write);
            Optional(attrs.CaptureInfo, capture => writer.Write(capture.ToIntValue()));
            Optional(attrs.Range, Range);
            writer.Write((ushort)attrs.NoFPClass);
            Reference(attrs.ByValType, Type);
            Reference(attrs.ByRefType, Type);
            Reference(attrs.StructRetType, Type);
            Reference(attrs.PreallocatedType, Type);
            Reference(attrs.InAllocaType, Type);
            Reference(attrs.ElementType, Type);
            Optional(attrs.DeadOnReturnBytes, writer.Write);
            Reference(attrs.Initializes, ranges => Seq(ranges, Range));
        }

        private void Function(LlvmFunction f)
        {
            writer.Write(f.CallingConvention);
            Optional(f.MemoryEffectsAttribute, memory => writer.Write(memory.Raw));
            Optional(f.VScaleRange, scale => { writer.Write(scale.Minimum); writer.Write(scale.Maximum); });
            Optional(f.DenormalFPEnvironment, denormal => writer.Write(denormal.ToIntValue()));
            Flags(f.NoInline, f.NoDuplicate, f.Convergent, f.ReturnsTwice, f.NoMerge, f.NoReturn,
                f.NoCreateUndefOrPoison, f.NoUnwind, f.StrictFP, f.NoImplicitFloat, f.NoBuiltin,
                f.WillReturnAttribute, f.NoFree, f.NoSync, f.Speculatable,
                f.OptimizeForSize, f.MinSize, f.NoJumpTables, f.IsPresplitCoroutine, f.NullPointerIsValid,
                f.Naked, f.Thunk, f.SanitizeThread, f.SanitizeConcurrency, f.SanitizeAddress, f.SanitizeHWAddress);
            Attributes(f.ReturnAttributes);
            for (int i = 0; i < f.ArgumentCount; ++i) Attributes(f.GetParamAttributes(i));
        }

        private void CallAttributes(LlvmCallInstruction call)
        {
            writer.Write(call.CallingConvention);
            Optional(call.MemoryEffectsAttribute, memory => writer.Write(memory.Raw));
            Flags(call.NoInline, call.NoDuplicate, call.Convergent, call.ReturnsTwice, call.NoMerge, call.NoReturn,
                call.NoCreateUndefOrPoison, call.NoUnwind, call.StrictFP, call.NoImplicitFloat, call.NoBuiltin,
                call.WillReturnAttribute, call.NoFree, call.NoSync, call.Speculatable);
            Attributes(call.ReturnAttributes);
            for (int i = 0; i < call.ArgumentCount; ++i) Attributes(call.GetParamAttributes(i));
        }
    }

    private sealed partial class Decoder
    {
        private uint FlagBits(int width)
        {
            uint bits = reader.ReadUInt32();
            return bits >> width == 0 ? bits : throw Invalid("unknown flag bits");
        }
        private static bool Has(uint bits, int index) => (bits & (1U << index)) != 0;

        private LlvmParameterAttributes Attributes()
        {
            var flags = (LlvmAttributeKind)reader.ReadUInt64();
            ulong align = reader.ReadUInt64();
            return new()
            {
                Flags = flags,
                Alignment = align == 0 ? null : Alignment(align),
                DereferenceableBytes = Optional(reader.ReadUInt64),
                DereferenceableOrNullBytes = Optional(reader.ReadUInt64),
                CaptureInfo = Optional(() => LlvmCaptureInfo.CreateFromIntValue(reader.ReadUInt32())),
                Range = Optional(Range),
                NoFPClass = (LlvmFPClassTest)reader.ReadUInt16(),
                ByValType = Reference(Type),
                ByRefType = Reference(Type),
                StructRetType = Reference(Type),
                PreallocatedType = Reference(Type),
                InAllocaType = Reference(Type),
                ElementType = Reference(Type),
                DeadOnReturnBytes = Optional(reader.ReadUInt64),
                Initializes = Reference(() => Seq(Range, 24))
            };
        }

        private void Function(LlvmFunction f)
        {
            f.CallingConvention = reader.ReadUInt32();
            f.MemoryEffectsAttribute = Optional(() => new LlvmMemoryEffectsAttribute(reader.ReadUInt32()));
            f.VScaleRange = Optional(() => new LlvmVScaleRange(reader.ReadUInt32(), reader.ReadUInt32()));
            f.DenormalFPEnvironment = Optional(() => LlvmDenormalFPEnv.CreateFromIntValue(reader.ReadUInt32()));
            uint flags = FlagBits(26);
            f.NoInline = Has(flags, 0); f.NoDuplicate = Has(flags, 1); f.Convergent = Has(flags, 2);
            f.ReturnsTwice = Has(flags, 3); f.NoMerge = Has(flags, 4); f.NoReturn = Has(flags, 5);
            f.NoCreateUndefOrPoison = Has(flags, 6); f.NoUnwind = Has(flags, 7); f.StrictFP = Has(flags, 8);
            f.NoImplicitFloat = Has(flags, 9); f.NoBuiltin = Has(flags, 10); f.WillReturnAttribute = Has(flags, 11);
            f.NoFree = Has(flags, 12); f.NoSync = Has(flags, 13); f.Speculatable = Has(flags, 14);
            f.OptimizeForSize = Has(flags, 15); f.MinSize = Has(flags, 16); f.NoJumpTables = Has(flags, 17);
            f.IsPresplitCoroutine = Has(flags, 18); f.NullPointerIsValid = Has(flags, 19); f.Naked = Has(flags, 20);
            f.Thunk = Has(flags, 21); f.SanitizeThread = Has(flags, 22); f.SanitizeConcurrency = Has(flags, 23);
            f.SanitizeAddress = Has(flags, 24); f.SanitizeHWAddress = Has(flags, 25);
            f.ReturnAttributes = Attributes();
            for (int i = 0; i < f.ArgumentCount; ++i) f.SetParamAttributes(i, Attributes());
        }

        private void CallAttributes(LlvmCallInstruction call)
        {
            call.CallingConvention = reader.ReadUInt32();
            call.MemoryEffectsAttribute = Optional(() => new LlvmMemoryEffectsAttribute(reader.ReadUInt32()));
            uint flags = FlagBits(15);
            call.NoInline = Has(flags, 0); call.NoDuplicate = Has(flags, 1); call.Convergent = Has(flags, 2);
            call.ReturnsTwice = Has(flags, 3); call.NoMerge = Has(flags, 4); call.NoReturn = Has(flags, 5);
            call.NoCreateUndefOrPoison = Has(flags, 6); call.NoUnwind = Has(flags, 7); call.StrictFP = Has(flags, 8);
            call.NoImplicitFloat = Has(flags, 9); call.NoBuiltin = Has(flags, 10); call.WillReturnAttribute = Has(flags, 11);
            call.NoFree = Has(flags, 12); call.NoSync = Has(flags, 13); call.Speculatable = Has(flags, 14);
            call.ReturnAttributes = Attributes();
            for (int i = 0; i < call.ArgumentCount; ++i) call.SetParamAttributes(i, Attributes());
        }
    }
}
