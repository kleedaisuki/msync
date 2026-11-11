using Msync.Core;

namespace Msync.Runtime;

public static class ExecutionHost
{
    /// <summary>Prepare the entry image once, then hand its selected transports to execution.</summary>
    public static Interpreter Create(Module module, ExecutionOptions options, CancellationToken cancellationToken = default)
    {
        if (module.Stage != ArtifactStage.Relocatable) return new(module, options, cancellationToken);

        var origin = new RuntimeContext(options);
        var bound = Binding.Bind(module, new(origin.WorkingDirectory, origin.HomeDirectory, origin.SourceFile));
        var resources = new ContextualResources(cancellationToken);
        try
        {
            var prepared = ContextualBackend.Prepare(bound, options, resources, cancellationToken);
            return new(prepared, options, null, cancellationToken, resources);
        }
        catch
        {
            resources.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
