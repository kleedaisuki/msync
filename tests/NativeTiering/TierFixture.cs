using Msync.Core.Native;
using Msync.Optimization;
using Msync.Runtime.Native;

internal static class TierFixture
{
    internal static NativeProgram Plan(NativeProgram program) => NativeOptimizer.PlanStorage(program);

    internal static NativeImage Link(NativeProgram program)
    {
        NativeRuntimeContext context = new();
        return new(NativeBackendCompiler.Compile(program, context), context);
    }
}
