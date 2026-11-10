namespace Msync.Core;

public sealed record Module(IReadOnlyList<Value> Constants, IReadOnlyList<FunctionCode> Functions, int EntryPoint = 0,
    ArtifactStage Stage = ArtifactStage.Relocatable, BindingContext? Context = null, int OptimizationLevel = 2,
    IReadOnlyList<BasicBlock>? Blocks = null, ModuleSemantics Semantics = ModuleSemantics.Current, bool PreserveLocals = false)
{
    // Session-only reachability metadata; object/executable files do not export locals.
    public IReadOnlyList<int>? SessionRoots { get; init; }
    public IReadOnlyList<SessionDispatchTable>? SessionDispatches { get; init; }
    public IReadOnlyList<ExecutionPlan>? ExecutionPlans { get; init; }
}
