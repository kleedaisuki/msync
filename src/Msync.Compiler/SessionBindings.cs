using System.Collections.Immutable;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private List<KeyValuePair<string, Binding>>? submittedBindings;
    private ImmutableDictionary<string, Binding>? startingBindings;

    public void EnableSessionBindings() => submittedBindings = [];

    private void PrepareSessionBindings()
    {
        if (submittedBindings is null) return;
        submittedBindings.Clear();
        startingBindings = sessionEnvironment;
    }

    public void AcceptSubmission()
    {
        startingBindings = null;
        submittedBindings?.Clear();
    }

    public void ReconcileSubmission(Func<int, bool> initialized)
    {
        if (startingBindings is null) return;
        var environment = startingBindings;
        foreach (var (name, binding) in submittedBindings!)
            if (initialized(binding.Slot)) environment = environment.SetItem(name, binding);
        sessionEnvironment = environment;
        AcceptSubmission();
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
