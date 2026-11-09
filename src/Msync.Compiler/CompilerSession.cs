using Msync.Core;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed class CompilerSession
{
    private readonly Compilation compilation;
    public CompilerSession(string sourceName = "<repl>", int optimization = 2)
    {
        compilation = new("", sourceName, optimization);
        compilation.EnableSessionBindings();
    }
    public Module Compile(string submission) => compilation.Submit(submission);
    public Module Compile(string submission, Document? document) => compilation.Submit(submission, document);
    public IReadOnlyCollection<string> BindingNames => compilation.BindingNames;
    public IReadOnlyList<SemanticMember> DescribeBindings() => EditorCompiler.DescribeBindings(compilation.EditorBindings);
    public IReadOnlyList<SemanticMember> DescribeConcepts() => compilation.DescribeConcepts();
    public EditorAnalysis AnalyzeEditor(string submission, Func<int, Value?>? resolveAcceptedValue = null) =>
        EditorCompiler.Analyze(submission, compilation, resolveAcceptedValue);
    public void AcceptSubmission() => compilation.AcceptSubmission();
    public void AcceptSubmission(Func<int, Value?> resolveAcceptedValue) => compilation.AcceptSubmission(resolveAcceptedValue);
    public void ReconcileSubmission(Func<int, bool> initialized) => compilation.ReconcileSubmission(initialized);
    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, Value?> resolveAcceptedValue) =>
        compilation.ReconcileSubmission(initialized, resolveAcceptedValue);
}
