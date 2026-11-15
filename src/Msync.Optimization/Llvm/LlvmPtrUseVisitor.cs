// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/PtrUseVisitor.h/.cpp; InstVisitor dispatch for represented operations.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public struct LlvmPointerUseInfo
{
    public LlvmInstruction? AbortingInstruction { get; private set; }
    public LlvmInstruction? EscapingInstruction { get; private set; }
    public LlvmInstruction? EscapedReadOnlyInstruction { get; private set; }
    public readonly bool IsAborted => AbortingInstruction is not null;
    public readonly bool IsEscaped => EscapingInstruction is not null;
    public readonly bool IsEscapedReadOnly => EscapedReadOnlyInstruction is not null;
    public void Reset() { AbortingInstruction = null; EscapingInstruction = null; }
    public void SetAborted(LlvmInstruction instruction) => AbortingInstruction = instruction;
    public void SetEscaped(LlvmInstruction instruction) => EscapingInstruction = instruction;
    public void SetEscapedReadOnly(LlvmInstruction instruction) => EscapedReadOnlyInstruction = instruction;
    public void SetEscapedAndAborted(LlvmInstruction instruction) { SetEscaped(instruction); SetAborted(instruction); }
}

public abstract class LlvmPtrUseVisitor(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
{
    protected readonly LlvmDataLayout Layout = layout;
    protected readonly Dictionary<string, ulong>? Metrics = metrics;
    protected LlvmPointerUseInfo PointerInfo;
    protected LlvmUse CurrentUse = null!;
    protected bool IsOffsetKnown;
    protected LlvmAPInt Offset;
    private readonly List<UseToVisit> worklist = [];
    private readonly HashSet<LlvmUse> visited = [];
    private readonly record struct UseToVisit(LlvmUse Use, bool IsOffsetKnown, LlvmAPInt Offset);

    public LlvmPointerUseInfo VisitPtr(LlvmValue value)
    {
        Debug.Assert(value.Type.IsPointerTy() && value is LlvmInstruction or LlvmArgument);
        IsOffsetKnown = true;
        Offset = LlvmAPInt.GetZero(Layout.GetIndexTypeSizeInBits(value.Type));
        PointerInfo.Reset();
        EnqueueUsers(value);
        while (worklist.Count != 0)
        {
            var visit = worklist[^1];
            worklist.RemoveAt(worklist.Count - 1);
            CurrentUse = visit.Use;
            IsOffsetKnown = visit.IsOffsetKnown;
            if (IsOffsetKnown) Offset = visit.Offset;
            Count("pointer_uses.visited");
            Visit((LlvmInstruction)CurrentUse.User);
            if (PointerInfo.IsAborted) break;
        }
        return PointerInfo;
    }

    protected void EnqueueUsers(LlvmValue value)
    {
        foreach (var use in value.Uses)
        {
            if (!visited.Add(use)) { Count("pointer_uses.duplicate"); continue; }
            worklist.Add(new(use, IsOffsetKnown, Offset));
            Count("pointer_uses.enqueued");
        }
    }

    protected bool AdjustOffsetForGep(LlvmGetElementPtrInstruction instruction)
    {
        if (!IsOffsetKnown) return false;
        var delta = LlvmAPInt.GetZero(Layout.GetIndexTypeSizeInBits(instruction.Type));
        if (!LlvmValueTracking.AccumulateConstantOffset(instruction, Layout, ref delta, Metrics)) return false;
        Offset += delta.SExtOrTrunc(Offset.BitWidth);
        Count("pointer_uses.constant_gep");
        return true;
    }

    protected virtual void Visit(LlvmInstruction instruction)
    {
        switch (instruction)
        {
            case LlvmLoadInstruction load: VisitLoad(load); break;
            case LlvmStoreInstruction store: VisitStore(store); break;
            case LlvmGetElementPtrInstruction gep: VisitGep(gep); break;
            case LlvmPhiInstruction phi: VisitPhi(phi); break;
            case LlvmSelectInstruction select: VisitSelect(select); break;
            case LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast: VisitBitCast(cast); break;
            case LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } cast: VisitAddressSpaceCast(cast); break;
            case LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } cast: VisitPointerToInteger(cast); break;
            case LlvmCallBase call when call.CalledFunction?.IntrinsicDescriptor is not null: VisitIntrinsic(call); break;
            case LlvmCallBase call: VisitCall(call); break;
            default: VisitInstruction(instruction); break;
        }
    }

    protected virtual void VisitLoad(LlvmLoadInstruction instruction) => VisitInstruction(instruction);
    protected virtual void VisitStore(LlvmStoreInstruction instruction)
    {
        if (!ReferenceEquals(instruction.ValueOperand, CurrentUse.Value)) return;
        PointerInfo.SetEscaped(instruction);
        Count("pointer_uses.store_escape");
    }
    protected virtual void VisitBitCast(LlvmCastInstruction instruction) => EnqueueUsers(instruction);
    protected virtual void VisitAddressSpaceCast(LlvmCastInstruction instruction) => EnqueueUsers(instruction);
    protected virtual void VisitPointerToInteger(LlvmCastInstruction instruction)
    {
        PointerInfo.SetEscaped(instruction);
        Count("pointer_uses.integer_escape");
    }
    protected virtual void VisitPhi(LlvmPhiInstruction instruction) => VisitInstruction(instruction);
    protected virtual void VisitSelect(LlvmSelectInstruction instruction) => VisitInstruction(instruction);

    protected virtual void VisitGep(LlvmGetElementPtrInstruction instruction)
    {
        if (instruction.UseEmpty) return;
        if (!AdjustOffsetForGep(instruction))
        {
            IsOffsetKnown = false;
            Offset = new();
            Count("pointer_uses.unknown_gep");
        }
        EnqueueUsers(instruction);
    }

    protected virtual void VisitIntrinsic(LlvmCallBase instruction)
    {
        if (LlvmMemIntrinsic.TryGet(instruction, out var memory)) { VisitMemoryIntrinsic(memory); return; }
        var descriptor = instruction.CalledFunction!.IntrinsicDescriptor!;
        if (descriptor.Kind.Name == "llvm.fake.use")
        { PointerInfo.SetEscaped(instruction); Count("pointer_uses.fake_use_escape"); return; }
        if (descriptor.IsLifetimeStartOrEnd) { Count("pointer_uses.lifetime_intrinsic"); return; }
        VisitCall(instruction);
    }

    protected virtual void VisitMemoryIntrinsic(LlvmMemIntrinsic instruction) => Count("pointer_uses.memory_intrinsic");

    protected virtual void VisitCall(LlvmCallBase instruction)
    {
        PointerInfo.SetEscaped(instruction);
        Count("pointer_uses.call_escape");
        VisitInstruction(instruction);
    }

    protected virtual void VisitInstruction(LlvmInstruction instruction) { }

    protected void Count(string name, ulong amount = 1)
    {
        if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + amount;
    }
}
