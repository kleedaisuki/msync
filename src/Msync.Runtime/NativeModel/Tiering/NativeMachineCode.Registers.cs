using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private bool StoreInitialized(int slot, FixedTierSlot location)
        {
            if (function.InitializationUnobserved(slot)) { InitializationWritesElided++; return false; }
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x30);
            Bytes(0x41, 0xc6, 0x82); Int(location.ByteOffset); Bytes(0x01);
            return true;
        }

        private void Region(NativeRegisterRegion region)
        {
            foreach (int input in region.Inputs)
            {
                NativeRegisterValue value = region.Values[input];
                Scalar(value.Slot, value.Kind, false, region.Start);
                if (value.Unsigned) Bytes(0xf2, 0x48, 0x0f, 0x2c, 0xc0);
                WriteValue(value);
            }
            foreach (NativeRegisterOperation operation in region.Operations)
            {
                Calculate(function.Instructions[operation.Pc], operation.Op, region.Start, region, operation);
                WriteValue(region.Values[operation.Result]);
            }
            foreach (int output in region.Outputs) StoreOutput(region.Values[output]);
            Commit(region);
            AdvanceRegion(region);
        }

        private void ReadValue(NativeRegisterValue value, NativeKind kind, bool second)
        {
            int target = second ? 1 : 0;
            if (value.Register >= 0) Bytes(0x66, 0x0f, 0x28, (byte)(0xc0 | target << 3 | value.Register));
            else
            {
                Bytes(0xf2, 0x0f, 0x10, (byte)(0x84 | target << 3), 0x24);
                Int((windows ? 32 : 0) + value.Spill * 8);
            }
            if (value.Unsigned && kind == NativeKind.Number)
            {
                Bytes(0x66, 0x48, 0x0f, 0x7e, second ? (byte)0xc9 : (byte)0xc0);
                Bytes(0xf2, 0x48, 0x0f, 0x2a, second ? (byte)0xc9 : (byte)0xc0);
            }
            if (kind == NativeKind.Bool) Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0);
        }

        private void WriteValue(NativeRegisterValue value)
        {
            if (value.Unsigned || value.Kind == NativeKind.Bool) Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc0);
            if (value.Register >= 0) Bytes(0x66, 0x0f, 0x28, (byte)(0xc0 | value.Register << 3));
            else
            {
                Bytes(0xf2, 0x0f, 0x11, 0x84, 0x24);
                Int((windows ? 32 : 0) + value.Spill * 8);
            }
        }

        private void StoreOutput(NativeRegisterValue value)
        {
            ReadValue(value, value.Kind, false);
            if (layout.TryFixed(value.Slot, out FixedTierSlot fixedSlot))
            {
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                if (value.Kind == NativeKind.Bool) Bytes(0x41, 0x88, 0x82);
                else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82);
                Int(fixedSlot.DataOffset);
                if (!function.HasKind(value.Slot, value.Kind))
                {
                    Bytes(0x41, 0xc6, 0x82); Int(fixedSlot.ByteOffset); Bytes((byte)value.Kind);
                }
                StoreInitialized(value.Slot, fixedSlot);
                return;
            }
            int offset = checked(layout.Scalars[value.Slot] * 32);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset);
            if (value.Kind == NativeKind.Bool) Bytes(0x41, 0x88, 0x03);
            else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x03);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset + 8); Bytes(0x41, 0xc6, 0x03, 0x01);
            if (function.HasKind(value.Slot, value.Kind)) return;
            Bytes(0x4d, 0x8b, 0x9a); Int(offset + 16);
            Bytes(0x41, 0xc6, 0x03, (byte)value.Kind);
        }

        private void Commit(NativeRegisterRegion region)
        {
            if (region.PendingOutputs.Length == 0) return;
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 0x01, 0x0f, 0x84); int noPending = code.Count; Int(0);
            Bytes(0x31, 0xc9, 0x4d, 0x8b, 0x54, 0x24, 0x08);
            foreach (int slot in region.PendingOutputs)
            {
                Bytes(0x41, 0x0b, 0x8a); Int(checked(layout.Scalars[slot] * 32 + 24));
            }
            Bytes(0xf6, 0xc1, 0x02, 0x0f, 0x84); int ready = code.Count; Int(0);
            Arguments(region.Start);
            Bytes(0x48, 0xb8); Long(commit); Bytes(0xff, 0xd0);
            PatchLocal(ready);
            PatchLocal(noPending);
        }

        private void AdvanceRegion(NativeRegisterRegion region)
            => Advance(region.Start, region.End, region.End - region.Start, layout.Hits[region.Start]);

        private void Control(NativeExecutionInstruction instruction, int pc)
        {
            if (instruction.Op == NativeOp.Jump) { Advance(pc, instruction.A, 1, layout.Hits[pc]); return; }
            Scalar(instruction.A, NativeKind.Bool, false, pc);
            Bytes(0x85, 0xc0, 0x0f, 0x84); int otherwise = code.Count; Int(0);
            Advance(pc, instruction.B, 1, layout.TrueHits[pc]);
            PatchLocal(otherwise);
            Advance(pc, instruction.C, 1, layout.Hits[pc]);
        }

        private void Advance(int pc, int next, int visits, int hit)
        {
            Bytes(0x41, 0x83, 0x7c, 0x24, 0x14, 0x00, 0x74); int skipHit = code.Count; Bytes(0);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x18, 0x49, 0xff, 0x82); Int(checked(hit * 8));
            code[skipHit] = (byte)(code.Count - skipHit - 1);
            Bytes(0x41, 0x81, 0x6c, 0x24, 0x20); Int(visits);
            Jump(0x0f, 0x8f, next);
            Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(next); Bytes(0xb8); Int(-5); Jump(0xe9, -1);
        }
    }

    private sealed partial class Arm64
    {
        private bool StoreInitialized(int slot, FixedTierSlot location)
        {
            if (function.InitializationUnobserved(slot)) { InitializationWritesElided++; return false; }
            Word(0xf9401a71); Load(14, 1); StoreFrameByte(14, location.ByteOffset);
            return true;
        }

        private void Region(NativeRegisterRegion region)
        {
            foreach (int input in region.Inputs)
            {
                NativeRegisterValue value = region.Values[input];
                Scalar(value.Slot, value.Kind, false, region.Start);
                if (value.Unsigned) Word(0x9e790002);
                WriteValue(value);
            }
            foreach (NativeRegisterOperation operation in region.Operations)
            {
                Calculate(function.Instructions[operation.Pc], operation.Op, region.Start, region, operation);
                WriteValue(region.Values[operation.Result]);
            }
            foreach (int output in region.Outputs) StoreOutput(region.Values[output]);
            Commit(region);
            AdvanceRegion(region);
        }

        private void ReadValue(NativeRegisterValue value, NativeKind kind, bool second)
        {
            uint target = second ? 1u : 0u;
            if (value.Register >= 0) Word(0x1e604000 | ((uint)value.Register << 5) | target);
            else
            {
                Load(17, (ulong)(value.Spill * 8)); Word(0x8b3163f1);
                Word(0xfd400220 | target);
            }
            if (value.Unsigned && kind == NativeKind.Number)
            {
                Word(second ? 0x9e660023u : 0x9e660002u);
                Word(second ? 0x9e630061u : 0x9e630040u);
            }
            if (kind == NativeKind.Bool) Word(0x9e660002);
        }

        private void WriteValue(NativeRegisterValue value)
        {
            if (value.Unsigned || value.Kind == NativeKind.Bool) Word(0x9e670040);
            if (value.Register >= 0) Word(0x1e604000 | (uint)value.Register);
            else
            {
                Load(17, (ulong)(value.Spill * 8)); Word(0x8b3163f1);
                Word(0xfd000220);
            }
        }

        private void StoreOutput(NativeRegisterValue value)
        {
            ReadValue(value, value.Kind, false);
            if (layout.TryFixed(value.Slot, out FixedTierSlot fixedSlot))
            {
                Word(0xf9401671);
                bool isBool = value.Kind == NativeKind.Bool;
                if (isBool) StoreFrameByte(2, fixedSlot.DataOffset);
                else StoreFrameDouble(fixedSlot.DataOffset);
                if (!function.HasKind(value.Slot, value.Kind))
                {
                    if ((uint)fixedSlot.DataOffset >= (isBool ? 4096u : 32768u) || (!isBool && (fixedSlot.DataOffset & 7) != 0))
                        Word(0xf9401671);
                    Load(14, (ulong)value.Kind);
                    StoreFrameByte(14, fixedSlot.ByteOffset);
                }
                StoreInitialized(value.Slot, fixedSlot);
                return;
            }
            Word(0xf9400670); Load(15, (ulong)checked(layout.Scalars[value.Slot] * 32)); Word(0x8b0f020f);
            Word(0xf94001f1); Word(value.Kind == NativeKind.Bool ? 0x39000222u : 0xfd000220u);
            Word(0xf94005f1); Load(14, 1); Word(0x3900022e);
            if (function.HasKind(value.Slot, value.Kind)) return;
            Word(0xf94009f1); Load(14, (ulong)value.Kind); Word(0x3900022e);
        }

        private void Commit(NativeRegisterRegion region)
        {
            if (region.PendingOutputs.Length == 0) return;
            Word(0xb940266e); int noPending = code.Count; Word(0);
            Load(14, 0); Word(0xf9400670);
            foreach (int slot in region.PendingOutputs)
            {
                Load(15, (ulong)checked(layout.Scalars[slot] * 32)); Word(0x8b0f020f);
                Word(0xb94019f1); Word(0x2a1101ce);
            }
            int ready = code.Count; Word(0);
            Word(0xaa1303e0); Load(1, (ulong)region.Start); Load(16, (ulong)commit); Word(0xd63f0200);
            code[ready] = 0x3608000e | (((uint)(code.Count - ready) & 0x3fff) << 5);
            code[noPending] = 0x3600000eu | (((uint)(code.Count - noPending) & 0x3fffu) << 5);
        }

        private void AdvanceRegion(NativeRegisterRegion region)
            => Advance(region.End, region.End - region.Start, layout.Hits[region.Start]);

        private void Control(NativeExecutionInstruction instruction, int pc)
        {
            if (instruction.Op == NativeOp.Jump) { Advance(instruction.A, 1, layout.Hits[pc]); return; }
            Scalar(instruction.A, NativeKind.Bool, false, pc);
            int otherwise = code.Count; Word(0);
            Advance(instruction.B, 1, layout.TrueHits[pc]);
            code[otherwise] = 0x34000002 | (((uint)(code.Count - otherwise) & 0x7ffff) << 5);
            Advance(instruction.C, 1, layout.Hits[pc]);
        }

        private void Advance(int next, int visits, int hit)
        {
            Word(0xb9401670); int skipHit = code.Count; Word(0);
            Word(0xf9400e70); Load(15, (ulong)checked(hit * 8)); Word(0x8b0f0210);
            Word(0xf9400211); Word(0x91000631); Word(0xf9000211);
            code[skipHit] = 0x34000000u | 16u | (((uint)(code.Count - skipHit) & 0x7ffffu) << 5);
            Word(0xb9402271); Load(15, (ulong)visits); Word(0x4b0f0231); Word(0xb9002271);
            Word(0x7100023f); Branch(next, 5);
            Load(17, (ulong)next); Word(0xb9001271); Word(0x12800080); Branch(-1, 0);
        }
    }
}
