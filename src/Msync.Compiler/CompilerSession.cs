using Msync.Core;
using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed class CompilerSession
{
    private readonly object gate = new();
    private readonly SemanticSession session;
    private readonly string sourceName;

    public CompilerSession(string sourceName = "<repl>", int optimization = 2)
    {
        this.sourceName = sourceName;
        session = new(sourceName, optimization);
    }

    public Module Compile(string submission) => Compile(submission, null);

    public Module Compile(string submission, Document? document)
    {
        lock (gate) return session.Compile(submission, document);
    }

    public IReadOnlyCollection<string> BindingNames
    {
        get { lock (gate) return session.BindingNames; }
    }

    public IReadOnlyList<SemanticMember> DescribeBindings()
    {
        lock (gate) return session.DescribeBindings();
    }

    public IReadOnlyList<SemanticMember> DescribeConcepts()
    {
        lock (gate) return session.DescribeConcepts();
    }

    public EditorAnalysis AnalyzeEditor(string submission, Func<int, Value?>? resolveAcceptedValue = null)
    {
        EditorParseResult parsed = Parser.ParseForEditor(submission, sourceName);
        SemanticEditorResult semantic;
        lock (gate) semantic = session.AnalyzeEditor(submission, parsed.Document, resolveAcceptedValue);
        return EditorCompiler.ProjectSemantic(submission, sourceName, parsed, semantic);
    }

    public void AcceptSubmission() => AcceptSubmission(null);

    public void AcceptSubmission(Func<int, Value?>? resolveAcceptedValue)
    {
        lock (gate) session.AcceptSubmission(resolveAcceptedValue);
    }

    public void ReconcileSubmission(Func<int, bool> initialized) => ReconcileSubmission(initialized, null);

    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, Value?>? resolveAcceptedValue)
    {
        lock (gate) session.ReconcileSubmission(initialized, resolveAcceptedValue);
    }
}
