using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;

internal static class ProjectionRetention
{
    public static void Run(Action<bool, string> require)
    {
        var retained = Create();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        require(!retained.Module.IsAlive, "Editor metadata does not retain its complete HIR module");
        require(!retained.Nodes.IsAlive, "Editor metadata does not retain HIR expression arenas");
        require(retained.View.Module.Sources[0].Name == "retention.msync", "Source identity remains queryable");
        require(retained.View.Module.Templates[0].Result is { } result
            && retained.View.Module.RuntimeType(result) == TypeRef.Number, "Generic result metadata survives body release");
        GC.KeepAlive(retained.View);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SemanticObservationView View, WeakReference Module, WeakReference Nodes) Create()
    {
        HirBuilder builder = new();
        builder.Sources.Add(new("retention.msync", "fn value() { 7 }"));
        var type = builder.Type(TypeRef.Number);
        HirSourceRange source = new(new(0), 0, 1);
        HirNodeId result = HirNodeId.None;
        for (int index = 0; index < 8192; index++)
            result = builder.Literal(new(HirLiteralKind.Number, Number: index), type, source);
        builder.Functions.Add(new("value", [], [], type, result));
        builder.Templates.Add(new("value", [], [], result, source));
        HirModule module = builder.Freeze(new(0));
        SemanticObservationView view = new(module, new(0), [], [], [], [], [], [], []);
        return (view, new(module), new(ImmutableCollectionsMarshal.AsArray(module.Nodes)!));
    }
}
