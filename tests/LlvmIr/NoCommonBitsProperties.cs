using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class NoCommonBitsProperties
{
    internal static int Run()
    {
        LlvmContext context = new(); LlvmDataLayout layout = new();
        var type = LlvmType.GetInt8Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type, type]), LlvmLinkage.InternalLinkage, "no-common-bits");
        var x = function.Arguments[0]; var y = function.Arguments[1]; var shift = function.Arguments[2];
        var undef = LlvmUndefValue.Get(type);
        var mask = new LlvmFreezeInstruction(undef);
        var ones = LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(8));
        var notMask = new LlvmBinaryOperator(LlvmOpcode.Xor, ones, mask);
        var left = new LlvmBinaryOperator(LlvmOpcode.And, notMask, x);
        var right = new LlvmBinaryOperator(LlvmOpcode.And, y, mask);
        var metrics = new Dictionary<string, ulong>();
        Program.Require(LlvmValueTracking.HaveNoCommonBitsSet(left, right, layout, metrics: metrics),
            "The source complementary-mask pattern must handle either not/and operand order.");
        var undefNot = new LlvmBinaryOperator(LlvmOpcode.Xor, undef, ones);
        var undefLeft = new LlvmBinaryOperator(LlvmOpcode.And, x, undefNot);
        var undefRight = new LlvmBinaryOperator(LlvmOpcode.And, undef, y);
        Program.Require(LlvmValueTracking.GetNoCommonBitsSetResult(undefLeft, undefRight, layout, metrics: metrics) == LlvmNoCommonBitsSetResult.OnlyIfUndefIgnored,
            "Correlated undef masks must not be promoted to an unconditional disjoint fact.");
        Program.Require(LlvmValueTracking.HaveNoCommonBitsSet(LlvmConstantInt.Get(type, 15), LlvmConstantInt.Get(type, 240), layout, metrics: metrics),
            "The cached known-bits path did not prove disjoint constants.");
        var lhs = new LlvmBinaryOperator(LlvmOpcode.Shl, x, shift);
        var reverse = new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantInt.Get(type, 8), shift);
        var rhs = new LlvmBinaryOperator(LlvmOpcode.LShr, y, reverse);
        Program.Require(LlvmValueTracking.HaveNoCommonBitsSet(lhs, rhs, layout, metrics: metrics),
            "The source complementary shift-range pattern was replaced by independent known bits.");
        Program.Require(metrics.GetValueOrDefault("value_tracking.with_cache.miss") > 0 && metrics.GetValueOrDefault("value_tracking.no_common_bits.queries") == 4,
            "The source cache/query work disappeared from telemetry.");
        return 5;
    }
}
