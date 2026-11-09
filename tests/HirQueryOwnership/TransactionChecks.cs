using Msync.Core;
using Msync.Hir;

internal static class TransactionChecks
{
    internal static int Run()
    {
        HirBuilder builder = new();
        HirTypeId number = builder.Type(TypeRef.Number);
        HirLocation original = new("value", number, default, new(0));
        HirFunction function = new("main", [], [], number, HirNodeId.None);
        builder.Locations.Add(original);
        builder.Functions.Add(function);
        builder.SetExports([new("value", new(0))]);
        int checks = 0;
        using (builder.BeginTransaction())
        {
            builder.SetLocation(new(0), original with { Name = "changed" });
            builder.SetFunction(new(0), function with { Name = "changed" });
            builder.Type(TypeRef.String);
            builder.Locations.Add(original with { Name = "new" });
            builder.SetExports([new("new", new(1))]);
        }
        Check(builder.Locations.Count == 1 && builder.Locations[0] == original);
        Check(builder.Functions[0] == function);
        Check(builder.Exports is [{ Name: "value" }]);
        Check(builder.Types.Count == 1 && builder.Type(TypeRef.String).Value == 1);
        using (var committed = builder.BeginTransaction())
        {
            builder.SetLocation(new(0), original with { Name = "committed" });
            committed.Commit();
        }
        Check(builder.Locations[0].Name == "committed");
        using (builder.BeginTransaction())
        {
            builder.SetLocation(new(0), original with { Name = "outer" });
            using (var nested = builder.BeginTransaction())
            {
                builder.SetLocation(new(0), original with { Name = "inner" });
                nested.Commit();
            }
        }
        Check(builder.Locations[0].Name == "committed");
        long small = Allocation(10), large = Allocation(100_000);
        Check(small == large);
        HirBuilder sites = new();
        sites.Scopes.Add(new(0, []));
        _ = sites.Freeze(new(0));
        HirAllocationSiteId? discarded;
        using (sites.BeginTransaction())
        {
            sites.Scopes.Add(new(0, []));
            _ = sites.Freeze(new(0));
            discarded = sites.Scopes[1].Site;
        }
        sites.Scopes.Add(new(0, []));
        _ = sites.Freeze(new(0));
        Check(sites.Scopes[1].Site is { } renewed && renewed != discarded);
        return checks;

        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Submission transaction did not preserve accepted HIR.");
            checks++;
        }
    }

    private static long Allocation(int exports)
    {
        HirBuilder builder = new();
        builder.SetExports(Enumerable.Range(0, exports).Select(index => new HirExport("value", new(index))));
        using (builder.BeginTransaction()) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (builder.BeginTransaction()) { }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
