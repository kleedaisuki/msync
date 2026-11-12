using System.Diagnostics.CodeAnalysis;

namespace Msync.Optimization.Execution;

/// <summary>Prepares pure scalar methods on the same bound graph and worker ABI as indexed code.</summary>
public static class ExecutionScalarWorker
{
    public static bool MayPrepare(FunctionCode body) => body.Instructions.Any(instruction =>
        ExecutionNumericOperation.Bind(instruction) is { Representation: NumericRepresentation.Word32 });

    public static bool TryPrepare(Module image, int function, [NotNullWhen(true)] out IndexedMethodPlan? plan,
        out IndexedMethodFailure failure)
    {
        plan = null;
        failure = new(-1, default);
        if (image.PreserveLocals || image.SessionDispatches is { Count: > 0 }
            || !ExecutionRegionImporter.TryImport(image, function, out var graph, out _))
            return false;
        return new IndexedMethodImporter(image, graph, null, BoundWorkerMode.Scalar).Prepare(out plan, out failure);
    }
}
