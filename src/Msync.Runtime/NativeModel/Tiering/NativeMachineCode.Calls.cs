using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private int RepeatFixedCall(int pc, NativeKind kind, NativeArgumentMove[] moves)
        {
            List<int> misses = [];
            void Miss(byte condition)
            {
                Bytes(0x0f, condition);
                misses.Add(code.Count); Int(0);
            }

            int status = kind == NativeKind.Bool ? NativeInterpreter.TierBoolReturn : NativeInterpreter.TierNumberReturn;
            Bytes(0x41, 0x83, 0x7c, 0x24, NativeTierLayout.FixedReturnStateOffset, (byte)-status);
            Miss(0x85);
            Bytes(0x41, 0x81, 0xbc, 0x24); Int(NativeTierLayout.FixedRepeatPcOffset); Int(pc);
            Miss(0x85);
            Bytes(0x41, 0x83, 0xbc, 0x24); Int(NativeTierLayout.FixedRepeatsOffset); Bytes(NativeInterpreter.FixedRepeatLimit);
            Miss(0x83);
            LoadFixedStamp(NativeTierLayout.FixedPendingOffset);
            Bytes(0x41, 0x83, 0x3b, 0); // Global pending count.
            Miss(0x85);
            LoadFixedStamp(NativeTierLayout.FixedCallableInitOffset);
            Bytes(0x41, 0x80, 0x3b, 0);
            Miss(0x84);
            LoadFixedStamp(NativeTierLayout.FixedCallableEdgesOffset);
            Bytes(0x49, 0x8b, 0x03);
            Bytes(0x49, 0x3b, 0x84, 0x24); Int(NativeTierLayout.FixedEnvironmentOffset);
            Miss(0x85);
            Bytes(0x49, 0x83, 0x7b, 8, 0); // The native-name word must still be empty.
            Miss(0x85);
            GuardFixedArguments(moves, Miss);

            // The preceding inline RET restored precisely this segment, SP and FP.
            // Count the completed call and reset the child's fuel before reusing its context.
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.ChildContextOffset);
            Bytes(0xb8); Int(256);
            Bytes(0x41, 0x2b, 0x43, 32);
            Bytes(0x41, 0x01, 0x84, 0x24); Int(NativeTierLayout.FixedRepeatFuelOffset);
            Bytes(0x41, 0xc7, 0x43, 32); Int(256);
            Bytes(0x41, 0xff, 0x84, 0x24); Int(NativeTierLayout.FixedRepeatsOffset);
            Bytes(0x41, 0xc7, 0x44, 0x24, NativeTierLayout.FixedReturnStateOffset); Int(status);
            Bytes(0xe9); int admitted = code.Count; Int(0);
            foreach (int miss in misses) PatchLocal(miss);
            return admitted;
        }

        private void LoadFixedStamp(int offset)
        {
            Bytes(0x4d, 0x8b, 0x9c, 0x24); Int(offset); // r11 = stamp word.
        }

        private void GuardFixedArguments(NativeArgumentMove[] moves, Action<byte> miss)
        {
            if (moves.All(static move => move.Representation.References == 0)) return;
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.StackCursorOffset, 0x4d, 0x8b, 0x1b);
            Bytes(0x4d, 0x8b, 0x5b, NativeStackHeader.X64ReferencesOffset);
            foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
                for (int i = 0; i < move.Representation.References; i++)
                {
                    Bytes(0x49, 0x83, 0xbb); Int((move.Source.References + i) * sizeof(long)); Bytes(0);
                    miss(0x8c);
                }
        }

        private void PublishFixedFrame(NativeOffset extent)
        {
            // r11 is the cursor, r8 the child context, rax the new header.
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.StackCursorOffset);
            Bytes(0x4d, 0x8b, 0x44, 0x24, NativeTierLayout.ChildContextOffset);
            Bytes(0x45, 0x8b, 0x4c, 0x24, NativeTierLayout.FixedCallStartOffset); // r9d: aligned byte start.
            Bytes(0x4d, 0x8b, 0x53, NativeStackCursor.X64DataOffset, 0x4d, 0x01, 0xca);
            Bytes(0x49, 0x8d, 0x42, unchecked((byte)-NativeStackHeader.X64Bytes));
            Bytes(0x48, 0xc7, 0x00); Int(0); // Empty pending index.
            FixedFrameLane(NativeStackHeader.X64DataOffset, NativeTierLayout.RawOffset);
            Bytes(0x4d, 0x8b, 0x53, NativeStackCursor.X64InitializationOffset, 0x4d, 0x01, 0xca);
            FixedFrameLane(NativeStackHeader.X64InitializationOffset, NativeTierLayout.InitializationOffset);

            Bytes(0x41, 0x8b, 0x4c, 0x24, NativeTierLayout.FixedCallStartOffset + 4); // ecx: root start.
            if (extent.References == 0) Bytes(0x45, 0x31, 0xd2);
            else
            {
                Bytes(0x4d, 0x8b, 0x53, NativeStackCursor.X64ReferencesOffset);
                Bytes(0x4d, 0x8d, 0x14, 0xca); // r10 + rcx * 8.
            }
            FixedFrameLane(NativeStackHeader.X64ReferencesOffset, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x49, 0x8b, 0x13, 0x48, 0x89, 0x50, NativeStackHeader.X64PreviousOffset);
            Bytes(0x48, 0x8b, 0x52, NativeStackHeader.X64ArenaOffset);
            Bytes(0x48, 0x89, 0x50, NativeStackHeader.X64ArenaOffset);
            Bytes(0xc7, 0x40, NativeStackHeader.X64ReferenceCountOffset); Int(extent.References);

            Bytes(0x41, 0x81, 0xc1); Int(extent.Bytes);
            if (extent.References != 0)
            {
                Bytes(0x81, 0xc1); Int(extent.References);
            }
            Bytes(0x41, 0x89, 0x4b, NativeStackCursor.X64TopOffset + 4);
            Bytes(0x45, 0x89, 0x4b, NativeStackCursor.X64TopOffset);
            Bytes(0x49, 0x89, 0x03); // Publish FP only after the header is complete.
        }

        private void FixedFrameLane(byte headerOffset, byte contextOffset) =>
            Bytes(0x4c, 0x89, 0x50, headerOffset, 0x4d, 0x89, 0x50, contextOffset);

        private void ReturnUnattached(NativeOffset extent, int destination, NativeKind kind, int successor)
        {
            byte status = unchecked((byte)(kind == NativeKind.Bool ? NativeInterpreter.TierBoolReturn : NativeInterpreter.TierNumberReturn));
            Bytes(0x83, 0xf8, status, 0x0f, 0x85); int different = code.Count; Int(0);
            Bytes(0x41, 0x83, 0x7c, 0x24, NativeTierLayout.FixedReturnStateOffset, status, 0x0f, 0x85);
            int notArmed = code.Count; Int(0);
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.StackCursorOffset);
            Bytes(0x4d, 0x8b, 0x53, NativeStackCursor.X64FrameOffset);
            Bytes(0x66, 0x0f, 0xef, 0xc0, 0x31, 0xc0); // Keep RDX (the result) intact.
            ClearReturnLane(NativeStackHeader.X64InitializationOffset, extent.Bytes);
            ClearReturnLane(NativeStackHeader.X64ReferencesOffset, extent.References * sizeof(long));
            Bytes(0x49, 0x8b, 0x44, 0x24, NativeTierLayout.FixedReturnTopOffset);
            Bytes(0x49, 0x89, 0x43, NativeStackCursor.X64TopOffset);
            Bytes(0x4d, 0x8b, 0x52, NativeStackHeader.X64PreviousOffset);
            Bytes(0x4d, 0x89, 0x53, NativeStackCursor.X64FrameOffset);
            FixedScalarResult(destination, kind, fromReturnRegister: true);
            // Negative arms the reservation; positive records an already completed RET.
            Bytes(0x41, 0xf7, 0x5c, 0x24, NativeTierLayout.FixedReturnStateOffset);
            Jump(0xe9, successor);
            PatchLocal(different);
            PatchLocal(notArmed);
        }

        private void ClearReturnLane(byte headerOffset, int bytes)
        {
            if (bytes == 0) return;
            Bytes(0x4d, 0x8b, 0x4a, headerOffset);
            int offset = 0;
            for (; offset + 16 <= bytes; offset += 16)
            {
                Bytes(0x41, 0x0f, 0x11, 0x81); Int(offset);
            }
            for (int width = 8; width != 0; width >>= 1)
            {
                if (bytes - offset < width) continue;
                if (width == 2) Bytes(0x66);
                Bytes(width == 8 ? (byte)0x49 : (byte)0x41, width == 1 ? (byte)0x88 : (byte)0x89, 0x81);
                Int(offset);
                offset += width;
            }
        }

        private void PlaceFixedArguments(NativeArgumentMove[] moves)
        {
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.StackCursorOffset,
                0x4d, 0x8b, 0x5b, NativeStackCursor.X64FrameOffset); // r11: child FP.
            Bytes(0x4d, 0x8b, 0x43, NativeStackHeader.X64PreviousOffset); // r8: caller FP.
            FixedArgumentBases(NativeStackHeader.X64DataOffset);
            foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
            {
                int copied = 0;
                for (int width = 8; width > 0; width >>= 1)
                    while (move.Representation.Bytes - copied >= width)
                    {
                        FixedArgumentWord(move.Source.Bytes + copied, move.Destination.Bytes + copied, width);
                        copied += width;
                    }
            }
            if (moves.Any(static move => move.Representation.References != 0))
            {
                FixedArgumentBases(NativeStackHeader.X64ReferencesOffset);
                foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
                    for (int edge = 0; edge < move.Representation.References; edge++)
                        FixedArgumentWord((move.Source.References + edge) * 8, (move.Destination.References + edge) * 8, 8);
            }
            Bytes(0x4d, 0x8b, 0x4b, NativeStackHeader.X64InitializationOffset);
            foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
            {
                Bytes(0x41, 0xc6, 0x81); Int(move.Destination.Bytes); Bytes(1);
            }
        }

        private void FixedArgumentBases(byte offset)
        {
            Bytes(0x4d, 0x8b, 0x50, offset); // r10: caller lane.
            Bytes(0x4d, 0x8b, 0x4b, offset); // r9: child lane.
        }

        private void FixedArgumentWord(int source, int destination, int width)
        {
            if (width == 2) Bytes(0x66);
            Bytes(width == 8 ? (byte)0x49 : (byte)0x41, width == 1 ? (byte)0x8a : (byte)0x8b, 0x82);
            Int(source);
            if (width == 2) Bytes(0x66);
            Bytes(width == 8 ? (byte)0x49 : (byte)0x41, width == 1 ? (byte)0x88 : (byte)0x89, 0x81);
            Int(destination);
        }

        private void MachineCall(NativeExecutionInstruction instruction, int pc, NativeMachineCall call, NativeMachineCallableSource? source = null)
        {
            // No entry bridge: build the callee's ordinary tier context in the caller's scratch.
            int scratch = CallScratch;
            int raw = scratch + call.Leaf.DataOffset, init = scratch + call.Leaf.InitOffset;
            int edges = scratch + call.Leaf.EdgesOffset;
            int slots = scratch + NativeTierLayout.HeaderBytes;
            int start = source?.Pc ?? pc;
            bool captured = call.Leaf.Captures.Length != 0;
            bool addressResult = call.Leaf.ResultKind == NativeKind.Address;
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, captured || call.Leaf.GuestReferences != 0 ? (byte)3 : (byte)1);
            Jump(0x0f, 0x85, GuardTarget(start, demand: true));
            if (source is null) EmitIsNull(instruction.A, pc);
            if (addressResult) GuardAddressResult(instruction.Result, start);

            StackImmediate(scratch + 16, 0);  // Pc and Observed.
            StackImmediate(scratch + 32, 256); // Fuel and PendingMask.
            if (call.ArgumentWindow is { } window) BorrowArgumentWindow(scratch, window);
            else PlaceMachineArguments(instruction, start, call, source, scratch, raw, init, edges, slots);

            Bytes(0x48, 0x8d, windows ? (byte)0x8c : (byte)0xbc, 0x24); Int(scratch); // rcx or rdi = child context.
            Bytes(0x31, windows ? (byte)0xd2 : (byte)0xf6, 0x48, 0xb8); Long(call.Address); Bytes(0xff, 0xd0);
            bool boolean = call.Leaf.ResultKind == NativeKind.Bool;
            bool isNull = call.Leaf.ResultKind == NativeKind.Null;
            Bytes(0x83, 0xf8, addressResult ? unchecked((byte)NativeInterpreter.TierAddressReturn)
                : isNull ? (byte)0xf5 : boolean ? (byte)0xf7 : (byte)0xf8);
            if (!captured || call.Leaf.GuestReferences != 0) Jump(0x0f, 0x85, GuardTarget(pc, demand: true));
            else
            {
                Bytes(0x0f, 0x84); int returned = code.Count; Int(0);
                Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(pc);
                Bytes(0xb8); Int(-12); Jump(0xe9, -1);
                PatchLocal(returned);
            }

            layout.TryFixed(instruction.Result, out FixedTierSlot destination);
            if (addressResult) StoreAddressResult(instruction.Result);
            else if (!isNull)
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

        private void BorrowArgumentWindow(int scratch, NativeOffset window)
        {
            // The callee does not write the window or reach a safepoint.
            // The caller's published frame remains the root owner.
            StackImmediate(scratch, 0);
            WindowPointer(scratch, 40, window.Bytes);
            WindowPointer(scratch, 48, window.Bytes);
            WindowPointer(scratch, NativeTierLayout.GuestEdgesOffset, window.References * sizeof(long));
        }

        private void WindowPointer(int scratch, int contextOffset, int displacement)
        {
            Bytes(0x49, 0x8b, 0x44, 0x24, (byte)contextOffset);
            if (displacement != 0) { Bytes(0x48, 0x05); Int(displacement); }
            Bytes(0x48, 0x89, 0x84, 0x24); Int(scratch + contextOffset);
        }

        private void PlaceMachineArguments(NativeExecutionInstruction instruction, int start, NativeMachineCall call,
            NativeMachineCallableSource? source, int scratch, int raw, int init, int edges, int slots)
        {
            ClearCallInitialization(init, call.Leaf.FrameBytes);
            StackPointer(scratch + 8, slots);
            StackPointer(scratch + 40, raw);
            StackPointer(scratch + 48, init);
            if (call.Leaf.GuestReferences != 0)
            {
                ClearCallInitialization(edges, call.Leaf.GuestReferences * sizeof(long));
                StackImmediate(scratch, 0);
                StackPointer(scratch + NativeTierLayout.GuestEdgesOffset, edges);
            }
            if (call.Leaf.Captures.Length != 0) SetupCaptures(instruction.A, start, call.Leaf, raw, init, slots, source);
            if (source is { InlineArgument: >= 0 } projected)
                InlineCallArgument(projected.InlineArgument, start);
            for (int index = 0; index < call.Inputs.Count; index++)
            {
                NativeMachineParameter parameter = call.Leaf.Parameters[index];
                if (parameter.Omitted) continue;
                if (parameter.Kind == NativeKind.Address)
                {
                    MachineGuestArgument(call.Inputs[index], parameter, start, raw, init, edges, slots);
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

        private void MachineGuestArgument(int input, NativeMachineParameter parameter, int pc, int raw, int init, int edges, int slots)
        {
            ref readonly NativeFrameSlot basis = ref function.FrameSlot(input);
            int demand = GuardTarget(pc, demand: true);
            RequireAddressInitialized(basis.Offset.Bytes, demand);
            (int noLane, int host, int stack) = GuestEdge(basis.Offset.References);
            jumps.Add((noLane, demand));
            jumps.Add((host, demand));
            jumps.Add((stack, demand));
            int edge = edges + parameter.Reference * sizeof(long);
            Bytes(0x4c, 0x89, 0x9c, 0x24); Int(edge);
            Bytes(0x4d, 0x8b, 0x54, 0x24, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x49, 0x8b, 0x82); Int((basis.Offset.References + 1) * sizeof(long));
            Bytes(0x48, 0x85, 0xc0); Jump(0x0f, 0x88, demand);
            Bytes(0x48, 0x89, 0x84, 0x24); Int(edge + sizeof(long));
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
            Bytes(0x49, 0x8b, 0x82); Int(basis.Offset.Bytes);
            Bytes(0x48, 0x89, 0x84, 0x24); Int(raw + parameter.Offset);
            Bytes(0xc6, 0x84, 0x24); Int(init + parameter.Offset); Bytes(1);
            if (parameter.Descriptor < 0) return;
            int descriptor = slots + parameter.Descriptor * 32;
            StackPointer(descriptor, raw + parameter.Offset);
            StackPointer(descriptor + 8, init + parameter.Offset);
            StackPointer(descriptor + 16, edge);
        }

        private void RequireAddressInitialized(int offset, int demand)
        {
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x30);
            Bytes(0x41, 0x80, 0xba); Int(offset); Bytes(0);
            Jump(0x0f, 0x84, demand);
        }

        private void GuardAddressResult(int slot, int pc)
        {
            int demand = GuardTarget(pc, demand: true);
            int edge = function.FrameSlot(slot).Offset.References * sizeof(long);
            Bytes(0x4d, 0x8b, 0x54, 0x24, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x4d, 0x85, 0xd2); Jump(0x0f, 0x84, demand);
            Bytes(0x49, 0x8b, 0x82); Int(edge);
            Bytes(0x49, 0x0b, 0x82); Int(edge + sizeof(long));
            Jump(0x0f, 0x88, demand);
        }

        private void LoadReturnAddress(int slot, int pc)
        {
            // The machine leaf ABI returns the packed interior offset and the
            // two reference words in RDX/R8/R9, leaving EAX for exit status.
            int demand = GuardTarget(pc, demand: true);
            NativeOffset offset = function.FrameSlot(slot).Offset;
            int edge = offset.References * sizeof(long);
            jumps.Add((GuestPending(), demand));
            RequireAddressInitialized(offset.Bytes, demand);
            Bytes(0x4d, 0x8b, 0x54, 0x24, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x4d, 0x85, 0xd2); Jump(0x0f, 0x84, demand);
            Bytes(0x4d, 0x8b, 0x82); Int(edge);
            Bytes(0x4d, 0x85, 0xc0); Jump(0x0f, 0x8e, demand);
            Bytes(0x41, 0xf6, 0xc0, 7); Jump(0x0f, 0x85, demand);
            Bytes(0x4d, 0x8b, 0x8a); Int(edge + sizeof(long));
            Bytes(0x4d, 0x85, 0xc9); Jump(0x0f, 0x88, demand);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
            Bytes(0x49, 0x8b, 0x92); Int(offset.Bytes);
        }

        private void StoreAddressResult(int slot)
        {
            NativeOffset offset = function.FrameSlot(slot).Offset;
            int edge = offset.References * sizeof(long);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
            Bytes(0x49, 0x89, 0x92); Int(offset.Bytes);
            Bytes(0x4d, 0x8b, 0x54, 0x24, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x4d, 0x89, 0x82); Int(edge);
            Bytes(0x4d, 0x89, 0x8a); Int(edge + sizeof(long));
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
