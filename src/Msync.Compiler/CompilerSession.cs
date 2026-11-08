using Msync.Core;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed class CompilerSession
{
    private readonly Compilation compilation;
    public CompilerSession(string sourceName = "<repl>", int optimization = 1)
    {
        compilation = new("", sourceName, optimization);
        compilation.EnableSessionBindings();
    }
    public Module Compile(string submission) => compilation.Submit(submission);
    public Module Compile(string submission, Document? document) => compilation.Submit(submission, document);
    public IReadOnlyCollection<string> BindingNames => compilation.BindingNames;
    public IReadOnlyList<SemanticMember> DescribeBindings() => EditorCompiler.DescribeBindings(compilation.EditorBindings);
    public EditorAnalysis AnalyzeEditor(string submission) => EditorCompiler.Analyze(submission, compilation);
    public void AcceptSubmission() => compilation.AcceptSubmission();
    public void ReconcileSubmission(Func<int, bool> initialized) => compilation.ReconcileSubmission(initialized);
}
