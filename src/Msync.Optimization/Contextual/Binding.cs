namespace Msync.Optimization;

public static class Binding
{
    public static Module Bind(Module module, BindingContext context)
    {
        var executable = BindInstructions(module, context);
        return ReferenceEquals(executable, module) ? module : BlockBuilder.Form(executable);
    }

    public static Module BindInstructions(Module module, BindingContext context)
    {
        if (module.Stage == ArtifactStage.Executable) return module;
        var bound = Relocate(module, context);
        var optimized = Optimizer.OptimizeInstructions(ExecutionLowering.Rewrite(bound), module.OptimizationLevel);
        // The final lowering selects native transfers exposed by folding; it introduces no new pure expressions.
        return ExecutableLayout.Arrange(ExecutionLowering.Rewrite(optimized));
    }

    public static Module Relocate(Module module, BindingContext context)
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
        return module with { Constants = module.Constants.Select(BindValue).ToArray(), Context = full, Stage = ArtifactStage.Executable };
    }
}
