using System.Collections.Immutable;
using System.Reflection;
using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class FormalAliasImportChecks
{
    internal static int Run()
    {
        BoundProgram bound = new SourceBinder().Bind("0", "formal-alias-import.msync");
        SemanticAnalyzer analyzer = new(bound);
        SemanticContext caller = new(new(0)), formal = new(new(1));
        foreach (int region in new[] { 10, 20, 30 })
            caller.Objects[region] = new(region, 0, ImmutableDictionary<string, SemanticValue>.Empty,
                ImmutableDictionary<string, HirLocationId>.Empty, Dynamic: true);
        caller.AliasViews[10] = ImmutableHashSet.Create(20);
        caller.AliasViews[20] = ImmutableHashSet.Create(10, 30);
        caller.AliasViews[30] = ImmutableHashSet.Create(20, -1);
        Type inputType = typeof(SemanticAnalyzer).GetNestedType("FormalInput", BindingFlags.NonPublic)!;
        object input = Activator.CreateInstance(inputType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [analyzer, caller, formal, false], null)!;
        MethodInfo abstractValue = inputType.GetMethod("Abstract", BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo complete = inputType.GetMethod("Complete", BindingFlags.Public | BindingFlags.Instance)!;
        int first = Import(10), second = Import(20);
        complete.Invoke(input, null);
        if (!formal.AliasViews[first].Contains(second)) Fail("An admitted alias relation was dropped.");
        int external = formal.AliasViews[second].Single(region => region != first);
        if (formal.Objects.Count != 2 || formal.Objects.ContainsKey(external))
            Fail("An external alias family eagerly copied an unread value heap.");
        if (!formal.AliasViews[external].Contains(second)) Fail("External family incidence was dropped.");
        if (!formal.AliasViews[external].Contains(-1) || formal.Objects.ContainsKey(-1))
            Fail("An unknown alias marker became an empty set or a fresh allocation.");
        int demanded = (int)inputType.GetMethod("MapSourceRegion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(input, [30])!;
        if (demanded != external || !formal.Objects.ContainsKey(external))
            Fail("A deferred-field receiver demand did not materialize its existing external family.");
        if (Import(30) != external || !formal.Objects.ContainsKey(external))
            Fail("A demanded external value did not materialize at its existing family identity.");
        return 6;

        int Import(int region) => ((SemanticValue)abstractValue.Invoke(input,
            [new SemanticValue(new(1), HirNodeId.None, Scope: caller.Objects[region])])!).Scope!.Region;
        static void Fail(string message) => throw new InvalidOperationException(message);
    }
}
