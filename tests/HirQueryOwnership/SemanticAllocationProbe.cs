using System.Text.Json;
using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class SemanticAllocationProbe
{
    internal static void Run(string path)
    {
        string source = File.ReadAllText(path);
        _ = Probe(source);
        for (int sample = 0; sample < 3; sample++)
        {
            GC.Collect();
            Console.WriteLine(JsonSerializer.Serialize(new { Sample = sample, Result = Probe(source) }));
        }

        _ = Msync.Compiler.Compiler.Analyze(source, "list-allocation-probe.msync");
        long publicStart = GC.GetAllocatedBytesForCurrentThread();
        _ = Msync.Compiler.Compiler.Analyze(source, "list-allocation-probe.msync");
        Console.WriteLine(JsonSerializer.Serialize(new { PublicAnalyzeBytes = GC.GetAllocatedBytesForCurrentThread() - publicStart }));
    }

    private static object Probe(string source)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        BoundProgram bound = new SourceBinder().Bind(source, "list-allocation-probe.msync");
        long boundBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        int boundNodes = bound.Hir.Nodes.Count;
        int boundEdges = bound.Hir.Edges.Count;
        int boundTypes = bound.Hir.Types.Count;
        int boundLocations = bound.Hir.Locations.Count;
        int boundTemplates = bound.Hir.Templates.Count;
        int boundLayouts = bound.Hir.ScopeLayouts.Count;
        start = GC.GetAllocatedBytesForCurrentThread();
        HirModule module = new SemanticAnalyzer(bound).Analyze();
        long analyzeBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        HirModule frozen = bound.Hir.Freeze(bound.Entry);
        long freezeBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        HirModule sealedAgain = HirSealer.Seal(frozen);
        long sealBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        GC.KeepAlive(sealedAgain);
        return new
        {
            BoundBytes = boundBytes, SemanticBytes = analyzeBytes, TotalBytes = boundBytes + analyzeBytes, FreezeBytes = freezeBytes, SealBytes = sealBytes,
            BoundNodes = boundNodes, BoundEdges = boundEdges, BoundTypes = boundTypes,
            BoundLocations = boundLocations, BoundTemplates = boundTemplates, BoundLayouts = boundLayouts,
            ArenaNodes = bound.Hir.Nodes.Count, ArenaTypes = bound.Hir.Types.Count,
            ArenaLocations = bound.Hir.Locations.Count, ArenaLayouts = bound.Hir.ScopeLayouts.Count,
            FinalNodes = module.Nodes.Length, FinalFunctions = module.Functions.Length,
            FinalTemplates = module.Templates.Length
        };
    }

}
