using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class PointerWalkSmallSetProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        var pointer = LlvmType.GetPointerTy(context);
        var function = new LlvmFunction(LlvmFunctionType.Get(pointer, [pointer]), LlvmLinkage.InternalLinkage, 0);
        for (int length = 0; length <= 8; ++length) CheckChains(function.Arguments[0], length);
        for (int length = 1; length <= 8; ++length) CheckCycles(function.Arguments[0], length);
        CheckRepresentations(function.Arguments[0]);
        return 91;
    }

    private static void CheckChains(LlvmValue root, int length)
    {
        LlvmValue casts = root, offsets = root;
        var one = LlvmConstantInt.Get(LlvmType.GetInt64Ty(root.Context), 1);
        for (int index = 0; index < length; ++index)
        {
            casts = new LlvmCastInstruction(LlvmOpcode.BitCast, casts, root.Type);
            offsets = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(root.Context), offsets, [one]);
        }
        Dictionary<string, ulong> metrics = [];
        Program.Require(ReferenceEquals(LlvmValueTracking.StripPointerCasts(casts, metrics), root),
            "Distinct cast instructions remain distinct visited entries and strip to their original root.");
        Program.Require(metrics.GetValueOrDefault("value_tracking.pointer_visited.overflow_promotions") == (length >= 4 ? 1UL : 0UL),
            "The fifth distinct node, and not the fourth, promotes the pointer walk.");
        Program.Require(metrics["value_tracking.pointer_visited.inline_inserts"] == (ulong)Math.Min(length + 1, 4),
            "Short walks store every visited reference inline without an overflow collection.");
        metrics.Clear();
        var offset = LlvmAPInt.GetZero(64);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripAndAccumulateConstantOffsets(offsets, new(), ref offset, metrics: metrics), root)
            && offset.GetZExtValue() == (ulong)length, "Offset chains retain the entire accumulated displacement beyond inline capacity.");
        Program.Require(metrics.GetValueOrDefault("value_tracking.pointer_visited.overflow_promotions") == (length >= 4 ? 1UL : 0UL),
            "Both pointer walkers use the same inline capacity and lazy overflow policy.");
    }

    private static void CheckCycles(LlvmValue root, int length)
    {
        List<LlvmCastInstruction> casts = [];
        List<LlvmGetElementPtrInstruction> offsets = [];
        LlvmValue castHead = root, offsetHead = root;
        var one = LlvmConstantInt.Get(LlvmType.GetInt64Ty(root.Context), 1);
        for (int index = 0; index < length; ++index)
        {
            var cast = new LlvmCastInstruction(LlvmOpcode.BitCast, castHead, root.Type);
            var gep = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(root.Context), offsetHead, [one]);
            casts.Add(cast); offsets.Add(gep); castHead = cast; offsetHead = gep;
        }
        casts[0].SetOperand(0, castHead);
        offsets[0].SetOperand(0, offsetHead);
        Dictionary<string, ulong> metrics = [];
        int callbacks = 0;
        Program.Require(ReferenceEquals(LlvmValueTracking.StripInBoundsOffsets(castHead, _ => ++callbacks, metrics), castHead)
            && callbacks == length, "A cyclic walk returns its first repeated reference without repeating callbacks.");
        Program.Require(metrics["value_tracking.pointer_casts.cycle"] == 1,
            "Short and overflow cast cycles retain their existing cycle outcome.");
        Program.Require(metrics.GetValueOrDefault("value_tracking.pointer_visited.overflow_promotions") == (length > 4 ? 1UL : 0UL),
            "A repeated inline reference does not promote the visited set.");
        metrics.Clear();
        var offset = LlvmAPInt.GetZero(64);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripAndAccumulateConstantOffsets(castHead, new(), ref offset, metrics: metrics), castHead)
            && offset.IsZero && metrics["value_tracking.pointer_offsets.cycle"] == 1, "Offset stripping also terminates on cast cycles.");
        offset = LlvmAPInt.GetZero(64);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripAndAccumulateConstantOffsets(offsetHead, new(), ref offset), offsetHead)
            && offset.GetZExtValue() == (ulong)length, "A cyclic GEP contributes each displacement once, including after overflow.");
    }

    private static void CheckRepresentations(LlvmValue root)
    {
        var addressType = LlvmType.GetPointerTy(root.Context, 1);
        var cast = new LlvmCastInstruction(LlvmOpcode.AddrSpaceCast, root, addressType);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripPointerCasts(cast), root), "Ordinary cast stripping crosses address spaces.");
        Program.Require(ReferenceEquals(LlvmValueTracking.StripPointerCastsSameRepresentation(cast), cast),
            "Same-representation stripping still stops at an address-space cast.");
        var offset = LlvmAPInt.GetZero(64);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripAndAccumulateConstantOffsets(cast, new(), ref offset), root) && offset.IsZero,
            "Offset stripping preserves address-space-cast behavior.");
        var i64 = LlvmType.GetInt64Ty(root.Context);
        var global = new LlvmGlobalVariable(i64, false, LlvmLinkage.InternalLinkage, LlvmConstantInt.Get(i64, 0));
        LlvmConstant expression = LlvmConstantExpr.GetAddrSpaceCast(global, addressType);
        Program.Require(expression is LlvmConstantExpr && ReferenceEquals(LlvmValueTracking.StripPointerCasts(expression), expression),
            "The cast-only walk retains its existing ConstantExpr stopping behavior.");
        for (int index = 0; index < 6; ++index) expression = LlvmConstantExpr.GetPtrAdd(expression, LlvmConstantInt.Get(i64, 1));
        Dictionary<string, ulong> metrics = [];
        offset = LlvmAPInt.GetZero(64);
        Program.Require(ReferenceEquals(LlvmValueTracking.StripAndAccumulateConstantOffsets(expression, new(), ref offset, metrics: metrics), global)
            && offset.GetZExtValue() == 6, "ConstantExpr GEPs and address-space casts retain their global root and displacement.");
        Program.Require(metrics["value_tracking.pointer_visited.overflow_promotions"] == 1
            && metrics["value_tracking.pointer_visited.overflow_seed_entries"] == 4,
            "Long ConstantExpr walks migrate exactly the four inline references once.");
    }
}
