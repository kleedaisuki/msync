using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private void GuestCopy(NativeExecutionInstruction instruction, int pc, NativeGuestView? view)
        {
            ref readonly NativeFrameSlot source = ref function.FrameSlot(view?.Source ?? instruction.A);
            ref readonly NativeFrameSlot target = ref function.FrameSlot(instruction.Result);
            int src = source.Offset.References * sizeof(long), dst = target.Offset.References * sizeof(long);
            int pending = GuestPending();
            Bytes(0x49, 0x8b, 0x44, 0x24, NativeTierLayout.GuestHostRootsOffset);
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.GuestEdgesOffset);
            Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84); int noEdges = code.Count; Int(0);
            // Host roots are pinned and read-only here. The bridge owns every managed reference write.
            Bytes(0x48, 0x8b, 0x88); Int(src);
            Bytes(0x48, 0x0b, 0x88); Int(src + 8);
            Bytes(0x48, 0x0b, 0x88); Int(dst);
            Bytes(0x48, 0x0b, 0x88); Int(dst + 8);
            if (view is not null)
            {
                int nativeName = (function.FrameSlot(instruction.A).Offset.References + 1) * sizeof(long);
                Bytes(0x48, 0x0b, 0x88); Int(nativeName);
            }
            Bytes(0x0f, 0x85); int host = code.Count; Int(0);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x30);
            Bytes(0x41, 0x80, 0xba); Int(source.Offset.Bytes); Bytes(0, 0x0f, 0x84);
            int notReady = code.Count; Int(0);
            Bytes(0x4d, 0x8b, 0x4c, 0x24, 0x28);
            List<int>? guards = view is { } checkedView ? GuestViewGuards(instruction.A, source, checkedView) : null;
            Bytes(0x49, 0x8b, 0x8b); Int(src);
            Bytes(0x49, 0x89, 0x8b); Int(dst);
            Bytes(0x49, 0x8b, 0x8b); Int(src + 8);
            Bytes(0x49, 0x89, 0x8b); Int(dst + 8);
            Bytes(0x49, 0x8b, 0x89); Int(source.Offset.Bytes);
            Bytes(0x49, 0x89, 0x89); Int(target.Offset.Bytes);
            if (source.Type.IsUnion)
            {
                Bytes(0x41, 0x8b, 0x89); Int(source.Offset.Bytes + 8);
                Bytes(0x41, 0x89, 0x89); Int(target.Offset.Bytes + 8);
            }
            Bytes(0x41, 0xc6, 0x82); Int(target.Offset.Bytes); Bytes(1);
            Advance(pc, pc + instruction.Span, instruction.Span, layout.Hits[pc]);
            PatchLocal(pending);
            PatchLocal(noEdges);
            PatchLocal(host);
            PatchLocal(notReady);
            if (guards is not null)
                foreach (int guard in guards) PatchLocal(guard);
            EmitBridge(pc);
        }

        private List<int> GuestViewGuards(int callee, NativeFrameSlot source, NativeGuestView view)
        {
            ref readonly NativeFrameSlot functionSlot = ref function.FrameSlot(callee);
            List<int> guards = [];
            Bytes(0x41, 0x80, 0xba); Int(functionSlot.Offset.Bytes); Bytes(0);
            Guard(0x84);
            if (view.Captured)
            {
                int root = functionSlot.Offset.References * sizeof(long);
                Bytes(0x48, 0x8b, 0x88); Int(root);
                Bytes(0x49, 0x0b, 0x8b); Int(root);
                Guard(0x84);
            }
            Bytes(0x41, 0x80, 0xb9); Int(source.Offset.Bytes); Bytes((byte)NativeKind.Null, 0x0f, 0x84);
            int absent = code.Count; Int(0);
            Bytes(0x49, 0x8b, 0x93); Int(source.Offset.References * sizeof(long));
            Bytes(0x48, 0x85, 0xd2);
            Guard(0x84);
            Bytes(0x66, 0x81, 0x7a, unchecked((byte)NativeGuestObjectTable.LayoutDisplacement),
                (byte)view.Layout, (byte)(view.Layout >> 8));
            Guard(0x85);
            PatchLocal(absent);
            return guards;

            void Guard(byte condition)
            {
                Bytes(0x0f, condition);
                guards.Add(code.Count);
                Int(0);
            }
        }

        private (int NoLane, int NonNative) GuestEdge(int index, int descriptor = -1)
        {
            if (descriptor < 0)
                Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.GuestEdgesOffset);
            else
            {
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
                Bytes(0x4d, 0x8b, 0x9a); Int(descriptor * 32 + 16);
            }
            Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84);
            int noLane = code.Count; Int(0);
            Bytes(0x4d, 0x8b, 0x9b); Int(index * sizeof(long));
            Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84);
            int host = code.Count; Int(0);
            return (noLane, host);
        }

        private void GuestOffset(NativeFrameSlot basis, int descriptor)
        {
            if (descriptor < 0)
            {
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                Bytes(0x49, 0x63, 0x92); Int(basis.Offset.Bytes);
            }
            else
            {
                Bytes(0x49, 0x8b, 0x92); Int(descriptor * 32);
                Bytes(0x48, 0x63, 0x12);
            }
        }

        private int GuestPending()
        {
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 0x02, 0x0f, 0x85);
            int pending = code.Count; Int(0);
            return pending;
        }

        private void GuestHasLayout(NativeExecutionInstruction instruction, int pc)
        {
            function.TryGuestLayoutCheck(pc, out NativeFrameSlot basis);
            int pending = GuestPending();
            (int noLane, int host) = GuestEdge(basis.Offset.References);
            Bytes(0x41, 0x0f, 0xb7, 0x43, unchecked((byte)NativeGuestObjectTable.LayoutDisplacement)); // eax: header layout.
            Bytes(0x3d); Int(NativeGuestObjectTable.UnknownLayout); Bytes(0x0f, 0x84);
            int unknown = code.Count; Int(0);
            int next = pc + instruction.Span;
            if (layout.LayoutBranches?.Contains(pc) == true)
            {
                RecordHit(layout.Hits[pc]);
                Bytes(0x3d); Int(instruction.B);
                NativeExecutionInstruction branch = function.Instructions[next];
                Conditional(branch, next, instruction.Span + branch.Span, 0x85);
            }
            else
            {
                Bytes(0x3d); Int(instruction.B); Bytes(0x0f, 0x94, 0xc0);
                layout.TryFixed(instruction.Result, out FixedTierSlot result);
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                Bytes(0x41, 0x88, 0x82); Int(result.DataOffset);
                StoreInitialized(instruction.Result, result);
                Advance(pc, next, instruction.Span, layout.Hits[pc]);
            }
            PatchLocal(pending);
            PatchLocal(noLane);
            PatchLocal(host);
            PatchLocal(unknown);
            EmitBridge(pc);
        }

        private void GuestMemory(NativeExecutionInstruction instruction, int pc)
        {
            function.TryGuestAccess(pc, out NativeGuestAccess access);
            bool load = instruction.Op == NativeOp.Load;
            bool boolean = access.Kind == NativeKind.Bool;
            int pending = GuestPending();
            if (!load) Scalar(instruction.B, access.Kind, false, pc);

            int descriptor = access.Base.Binding < 0 ? -1 : layout.Scalars[instruction.A];
            (int noLane, int host) = GuestEdge(descriptor < 0 ? access.Base.Offset.References : 0, descriptor);
            Bytes(0x41, 0x8b, 0x4b, unchecked((byte)NativeGuestObjectTable.RawSizeDisplacement)); // ecx: flag displacement.
            GuestOffset(access.Base, descriptor);
            if (descriptor >= 0) Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28); // r10: frame bytes.
            Bytes(0x49, 0x01, 0xd3, 0x4c, 0x01, 0xd9); // r11: interior payload, rcx: interior flags.

            int uninitialized = -1;
            if (load)
            {
                Bytes(0x80, 0xb9); Int(access.Displacement); Bytes(0x00, 0x0f, 0x84);
                uninitialized = code.Count; Int(0);
                if (boolean) Bytes(0x41, 0x0f, 0xb6, 0x83);
                else Bytes(0xf2, 0x41, 0x0f, 0x10, 0x83);
                Int(access.Displacement);
                if (boolean) Bytes(0x41, 0x88, 0x82);
                else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82);
                Int(access.Value.Offset.Bytes);
                layout.TryFixed(instruction.Result, out FixedTierSlot value);
                StoreInitialized(instruction.Result, value);
            }
            else
            {
                if (boolean) Bytes(0x41, 0x88, 0x83);
                else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x83);
                Int(access.Displacement);
                Bytes(0xc6, 0x81); Int(access.Displacement); Bytes(1);
            }
            Advance(pc, pc + instruction.Span, instruction.Span, layout.Hits[pc]);
            PatchLocal(pending);
            PatchLocal(noLane);
            PatchLocal(host);
            if (uninitialized >= 0) PatchLocal(uninitialized);
            EmitBridge(pc);
        }
    }
}
