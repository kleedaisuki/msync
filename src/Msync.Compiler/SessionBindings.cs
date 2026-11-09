using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private List<KeyValuePair<string, Binding>>? submittedBindings;
    private ImmutableDictionary<string, Binding>? startingBindings;
    private readonly HashSet<Info> submittedValues = new(ReferenceEqualityComparer.Instance);

    public void EnableSessionBindings() => submittedBindings = [];

    private void PrepareSessionBindings()
    {
        if (submittedBindings is null) return;
        submittedBindings.Clear();
        submittedValues.Clear();
        startingBindings = sessionEnvironment;
    }

    public void AcceptSubmission(Func<int, Value?>? resolve = null)
    {
        if (resolve is not null) RefreshSessionValues(resolve);
        startingBindings = null;
        submittedBindings?.Clear();
        submittedValues.Clear();
    }

    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, Value?>? resolve = null)
    {
        if (startingBindings is null) return;
        var environment = startingBindings;
        foreach (var (name, binding) in submittedBindings!)
            if (initialized(binding.Slot)) environment = environment.SetItem(name, binding);
        var submittedEnvironment = sessionEnvironment;
        sessionEnvironment = environment;
        if (resolve is not null) RefreshSessionValues(resolve, submittedEnvironment);
        AcceptSubmission();
    }

    private void RecordSessionValue(Info info)
    {
        if (submittedBindings is not null && (info.Scope is not null || info.Function is not null || info.Alternatives is not null))
            submittedValues.Add(info);
    }

    private void RecordSessionBinding(Statement statement, ImmutableDictionary<string, Binding> environment)
    {
        if (statement is LetStatement declaration) { RecordPattern(declaration.Pattern, environment); return; }
        string? name = statement switch
        {
            FunctionDeclaration function => function.Name,
            NativeDeclaration native => native.Name,
            ScopeDeclaration scope => scope.Name,
            _ => null
        };
        if (name is not null) submittedBindings!.Add(KeyValuePair.Create(name, environment[name]));
    }

    private void RecordPattern(Pattern pattern, ImmutableDictionary<string, Binding> environment)
    {
        if (pattern is NamePattern { Name: not "_" } name)
            submittedBindings!.Add(KeyValuePair.Create(name.Name, environment[name.Name]));
        if (pattern is TuplePattern tuple)
            foreach (Pattern item in tuple.Items) RecordPattern(item, environment);
    }
}
