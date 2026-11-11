using Msync.Core;

namespace Msync.Runtime;

public static class ExecutionHost
{
    /// <summary>Prepare the entry image once, then hand its selected transports to execution.</summary>
    public static Interpreter Create(Module module, ExecutionOptions options, CancellationToken cancellationToken = default) =>
        Create(module, options, null, cancellationToken);

    public static Interpreter Create(Module module, ExecutionOptions options, CompilationTrace? preparationTrace,
        CancellationToken cancellationToken)
    {
        if (module.Stage != ArtifactStage.Relocatable) return new(module, options, cancellationToken);

        var origin = new RuntimeContext(options);
        BindingContext context = new(origin.WorkingDirectory, origin.HomeDirectory, origin.SourceFile);
        var bound = preparationTrace is null ? Binding.BindInstructions(module, context)
            : preparationTrace.Measure("bind", (module, context),
                static state => Binding.BindInstructions(state.module, state.context));
        var resources = new ContextualResources(cancellationToken);
        try
        {
            var prepared = preparationTrace is null ? ContextualBackend.PrepareInstructions(bound, options, resources, cancellationToken)
                : preparationTrace.Measure("prepare", (bound, options, resources, cancellationToken),
                    static state => ContextualBackend.PrepareInstructions(state.bound, state.options, state.resources, state.cancellationToken));
            return new(prepared, options, null, cancellationToken, resources);
        }
        catch
        {
            resources.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
