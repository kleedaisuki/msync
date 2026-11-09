namespace Msync.Optimization;

public static class Binding
{
    public static Module Bind(Module module, BindingContext context)
    {
        if (module.Stage == ArtifactStage.Executable) return module;
        var full = context with { WorkingDirectory = Path.GetFullPath(context.WorkingDirectory), HomeDirectory = context.HomeDirectory.Length == 0 ? HostContext.HomeDirectory : Path.GetFullPath(context.HomeDirectory) };
        Value BindValue(Value value) => value switch
        {
            PathValue path => path with { WorkingDirectory = path.WorkingDirectory ?? full.WorkingDirectory, HomeDirectory = path.HomeDirectory ?? full.HomeDirectory },
            FunctionValue function => function with { Captures = function.Captures?.Select(BindValue).ToArray() },
            ScopeValue scope => scope.MapFields(BindValue),
            _ => value
        };
        var bound = module with { Constants = module.Constants.Select(BindValue).ToArray(), Context = full, Stage = ArtifactStage.Executable };
        var optimized = Optimizer.Optimize(ExecutionLowering.Lower(bound), module.OptimizationLevel);
        // The final lowering selects native transfers exposed by folding; it introduces no new pure expressions.
        return ExecutionLowering.Lower(optimized);
    }
}
