using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private void MachineCall(NativeExecutionInstruction instruction, int pc, NativeMachineCall call, NativeMachineCallableSource? source = null)
        {
            // No entry bridge: build the callee's ordinary tier context in the caller's scratch.
            int scratch = CallScratch;
            int raw = scratch + call.Leaf.DataOffset, init = scratch + call.Leaf.InitOffset;
            int edges = scratch + call.Leaf.EdgesOffset;
            int slots = scratch + NativeTierLayout.HeaderBytes;
            int start = source?.Pc ?? pc;
            bool captured = call.Leaf.Captures.Length != 0;
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, captured || call.Leaf.GuestReferences != 0 ? (byte)3 : (byte)1);
            Jump(0x0f, 0x85, GuardTarget(start, demand: true));
            if (source is null) EmitIsNull(instruction.A, pc);

            ClearCallInitialization(init, call.Leaf.FrameBytes);
            StackImmediate(scratch + 16, 0);  // Pc and Observed.
            StackImmediate(scratch + 32, 256); // Fuel and PendingMask.
            StackPointer(scratch + 8, slots);
            StackPointer(scratch + 40, raw);
            StackPointer(scratch + 48, init);
            if (call.Leaf.GuestReferences != 0)
            {
                StackImmediate(scratch, 0);
                StackPointer(scratch + NativeTierLayout.GuestEdgesOffset, edges);
            }
            if (captured) SetupCaptures(instruction.A, start, call.Leaf, raw, init, slots, source);
            if (source is { InlineArgument: >= 0 } projected)
                InlineCallArgument(projected.InlineArgument, start);
            for (int index = 0; index < call.Inputs.Count; index++)
            {
                NativeMachineParameter parameter = call.Leaf.Parameters[index];
                if (parameter.Kind == NativeKind.Address)
                {
                    MachineGuestArgument(call.Inputs[index], parameter, start, raw, edges, slots);
                    continue;
                }
                Scalar(call.Inputs[index], parameter.Kind, false, start);
                if (parameter.Kind == NativeKind.Bool) Bytes(0x88, 0x84, 0x24);
                else Bytes(0xf2, 0x0f, 0x11, 0x84, 0x24);
                Int(raw + parameter.Offset);
                Bytes(0xc6, 0x84, 0x24); Int(init + parameter.Offset); Bytes(1);
                if (parameter.Descriptor < 0) continue;
                int descriptor = slots + parameter.Descriptor * 32;
                StackPointer(descriptor, raw + parameter.Offset);
                StackPointer(descriptor + 8, init + parameter.Offset);
                StackImmediate(descriptor + 24, 0);
            }

            Bytes(0x48, 0x8d, windows ? (byte)0x8c : (byte)0xbc, 0x24); Int(scratch); // rcx or rdi = child context.
            Bytes(0x31, windows ? (byte)0xd2 : (byte)0xf6, 0x48, 0xb8); Long(call.Address); Bytes(0xff, 0xd0);
            bool boolean = call.Leaf.ResultKind == NativeKind.Bool;
            bool isNull = call.Leaf.ResultKind == NativeKind.Null;
            Bytes(0x83, 0xf8, isNull ? (byte)0xf5 : boolean ? (byte)0xf7 : (byte)0xf8);
            if (!captured || call.Leaf.GuestReferences != 0) Jump(0x0f, 0x85, GuardTarget(pc, demand: true));
            else
            {
                Bytes(0x0f, 0x84); int returned = code.Count; Int(0);
                Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(pc);
                Bytes(0xb8); Int(-12); Jump(0xe9, -1);
                PatchLocal(returned);
            }

            layout.TryFixed(instruction.Result, out FixedTierSlot destination);
            if (!isNull)
            {
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                if (boolean)
                {
                    Bytes(0x8a, 0x84, 0x24); Int(raw + call.Leaf.ResultOffset);
                    Bytes(0x41, 0x88, 0x82);
                }
                else
                {
                    Bytes(0xf2, 0x0f, 0x10, 0x84, 0x24); Int(raw + call.Leaf.ResultOffset);
                    Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82);
                }
                Int(destination.DataOffset);
            }
            StoreInitialized(instruction.Result, destination);
            Bytes(0x49, 0xff, 0x44, 0x24, 0x38); // Completed machine-to-machine calls.
            if (source is not null) RecordHit(layout.Hits[start]);
            if (source is { InlineArgument: >= 0 } lifted) RecordHit(layout.Hits[lifted.InlineArgument]);
            Advance(start, pc + instruction.Span, pc - start + instruction.Span, layout.Hits[pc]);
        }

        private void ClearCallInitialization(int offset, int bytes)
        {
            if (bytes == 0) return;
            if (!windows)
            {
                Bytes(0x48, 0x8d, 0xbc, 0x24); Int(offset);
                Bytes(0x31, 0xc0, 0xb9); Int(bytes / 8); Bytes(0xf3, 0x48, 0xab);
                return;
            }

            // Windows preserves RDI. Clear the flags with volatile registers only.
            Bytes(0x4c, 0x8d, 0x94, 0x24); Int(offset);
            Bytes(0x31, 0xc0, 0xb9); Int(bytes / 8);
            int loop = code.Count;
            Bytes(0x49, 0x89, 0x02, 0x49, 0x83, 0xc2, 0x08, 0xff, 0xc9);
            Bytes(0x75, unchecked((byte)(loop - code.Count - 2)));
        }

        private void MachineGuestArgument(int input, NativeMachineParameter parameter, int pc, int raw, int edges, int slots)
        {
            ref readonly NativeFrameSlot basis = ref function.FrameSlot(input);
            int demand = GuardTarget(pc, demand: true);
            (int noLane, int host) = GuestEdge(basis.Offset.References);
            jumps.Add((noLane, demand));
            jumps.Add((host, demand));
            int edge = edges + parameter.Reference * sizeof(long);
            Bytes(0x4c, 0x89, 0x9c, 0x24); Int(edge);
            GuestOffset(basis, -1);
            Bytes(0x89, 0x94, 0x24); Int(raw + parameter.Offset);
            if (parameter.Descriptor < 0) return;
            int descriptor = slots + parameter.Descriptor * 32;
            StackPointer(descriptor, raw + parameter.Offset);
            StackPointer(descriptor + 16, edge);
        }

        private void InlineCallArgument(int pc, int entry)
        {
            NativeExecutionInstruction argument = function.Instructions[pc];
            Calculate(argument, operations[pc], entry);
            layout.TryFixed(argument.Result, out FixedTierSlot destination);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
            Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82); Int(destination.DataOffset);
            StoreInitialized(argument.Result, destination);
        }

        private void SetupCaptures(int slot, int pc, NativeMachineLeaf leaf, int raw, int init, int slots, NativeMachineCallableSource? source)
        {
            int demand = GuardTarget(pc, demand: true);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
            if (source is { Field: { } field } access)
            {
                int descriptor = layout.Scalars[access.Base] * 32;
                Bytes(0x4d, 0x8b, 0x9a); Int(descriptor);
                Bytes(0x4d, 0x85, 0xdb); Jump(0x0f, 0x84, demand);
                Bytes(0x49, 0x8b, 0x82); Int(descriptor + 8);
                Bytes(0x80, 0xb8); Int(field.Bytes); Bytes(0); Jump(0x0f, 0x84, demand);
                Bytes(0x4d, 0x8b, 0x9b); Int(field.References * 8);
            }
            else if (source is { } compose)
            {
                Bytes(0x4d, 0x8b, 0x9a); Int(layout.Scalars[compose.Base] * 32 + 16);
            }
            else
            {
                Bytes(0x4d, 0x8b, 0x9a); Int(layout.Scalars[slot] * 32);
            }
            Bytes(0x4d, 0x85, 0xdb); Jump(0x0f, 0x84, demand);
            Bytes(0x41, 0x81, 0x3b); Int(leaf.CaptureCount); Jump(0x0f, 0x85, demand);
            foreach (NativeMachineCapture capture in leaf.Captures)
            {
                int offset = 8 + capture.Index * NativeCaptureVector.EntryBytes;
                Bytes(0x41, 0x81, 0xbb); Int(offset + 16); Int(NativeCaptureVector.Shape(capture.Kind, capture.Location));
                Jump(0x0f, 0x85, demand);
                Bytes(0x49, 0x8b, 0x8b); Int(offset + 8);
                Bytes(0x80, 0x39, 0x00); Jump(0x0f, 0x84, demand);
                Bytes(0x49, 0x8b, 0x83); Int(offset);
                if (capture.Location)
                {
                    int descriptor = slots + capture.Descriptor * 32;
                    Bytes(0x48, 0x89, 0x84, 0x24); Int(descriptor);
                    Bytes(0x48, 0x89, 0x8c, 0x24); Int(descriptor + 8);
                    StackImmediate(descriptor + 24, 0);
                    continue;
                }
                if (capture.Kind == NativeKind.Bool)
                {
                    Bytes(0x8a, 0x00, 0x88, 0x84, 0x24); Int(raw + capture.Offset);
                }
                else
                {
                    Bytes(0x48, 0x8b, 0x00, 0x48, 0x89, 0x84, 0x24); Int(raw + capture.Offset);
                }
                Bytes(0xc6, 0x84, 0x24); Int(init + capture.Offset); Bytes(1);
            }
        }

        private void StackImmediate(int destination, int value)
        {
            Bytes(0x48, 0xc7, 0x84, 0x24); Int(destination); Int(value);
        }

        private void StackPointer(int destination, int target)
        {
            Bytes(0x48, 0x8d, 0x84, 0x24); Int(target);
            Bytes(0x48, 0x89, 0x84, 0x24); Int(destination);
        }
    }
}
