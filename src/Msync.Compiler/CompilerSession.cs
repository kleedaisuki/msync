using Msync.Core;
using Msync.Core.Hpir;
using Msync.Compiler.Semantics;
using Msync.Compiler.Session;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public class CompilerSession
{
    private readonly object gate = new();
    private readonly SemanticSession session;
    private readonly string sourceName;
    private readonly CompilationTrace? trace;

    public CompilerSession(string sourceName = "<repl>", int optimization = 2)
        : this(sourceName, optimization, null) { }

    public CompilerSession(string sourceName, int optimization, CompilationTrace? trace)
    {
        this.sourceName = sourceName;
        this.trace = trace;
        session = new(sourceName, optimization);
    }

    internal HpirSessionSubmission CompilePhysical(string submission, Document? document = null,
        CompilationTrace? submissionTrace = null)
    {
        lock (gate) return session.CompilePhysical(submission, document, submissionTrace ?? trace);
    }

    internal void AcceptPhysicalSubmission(Func<int, HpirSessionValue?> observe, CompilationTrace? submissionTrace = null)
    {
        lock (gate) session.AcceptSubmission(observe, submissionTrace ?? trace);
    }

    internal void ReconcilePhysicalSubmission(Func<int, bool> initialized, Func<int, HpirSessionValue?> observe,
        CompilationTrace? submissionTrace = null)
    {
        lock (gate) session.ReconcileSubmission(initialized, observe, submissionTrace ?? trace);
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

    public EditorAnalysis AnalyzeEditor(string submission) => AnalyzeEditor(submission, trace);

    public EditorAnalysis AnalyzeEditor(string submission, CompilationTrace? trace)
    {
        EditorParseResult parsed = trace is null ? Parser.ParseForEditor(submission, sourceName)
            : trace.MeasureDetailed("editor.parse", (submission, sourceName, trace),
                static state => Parser.ParseForEditor(state.submission, state.sourceName, default, state.trace.Detail));
        SemanticEditorResult semantic;
        lock (gate) semantic = session.AnalyzeEditor(submission, parsed.Document, trace);
        return trace is null ? EditorCompiler.ProjectSemantic(submission, sourceName, parsed, semantic)
            : trace.MeasureDetailed("editor.project", (submission, sourceName, parsed, semantic),
                static state => EditorCompiler.ProjectSemantic(state.submission, state.sourceName, state.parsed, state.semantic));
    }
}
