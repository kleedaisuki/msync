using Msync.Core;
using Msync.Core.Native;
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

    public NativeProgram Compile(string submission) => Compile(submission, null);

    public NativeProgram Compile(string submission, Document? document) => Compile(submission, document, trace);

    public NativeProgram Compile(string submission, Document? document, CompilationTrace? trace)
    {
        lock (gate) return session.Compile(submission, document, trace);
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

    public EditorAnalysis AnalyzeEditor(string submission, Func<int, NativeValue?>? resolveAcceptedValue = null) =>
        AnalyzeEditor(submission, resolveAcceptedValue, trace);

    public EditorAnalysis AnalyzeEditor(string submission, Func<int, NativeValue?>? resolveAcceptedValue, CompilationTrace? trace)
    {
        EditorParseResult parsed = trace is null ? Parser.ParseForEditor(submission, sourceName)
            : trace.MeasureDetailed("editor.parse", (submission, sourceName, trace),
                static state => Parser.ParseForEditor(state.submission, state.sourceName, default, state.trace.Detail));
        SemanticEditorResult semantic;
        lock (gate) semantic = session.AnalyzeEditor(submission, parsed.Document, resolveAcceptedValue, trace);
        return trace is null ? EditorCompiler.ProjectSemantic(submission, sourceName, parsed, semantic)
            : trace.MeasureDetailed("editor.project", (submission, sourceName, parsed, semantic),
                static state => EditorCompiler.ProjectSemantic(state.submission, state.sourceName, state.parsed, state.semantic));
    }

    public void AcceptSubmission() => AcceptSubmission(null);

    public void AcceptSubmission(Func<int, NativeValue?>? resolveAcceptedValue)
    {
        lock (gate) session.AcceptSubmission(resolveAcceptedValue);
    }

    public void ReconcileSubmission(Func<int, bool> initialized) => ReconcileSubmission(initialized, null);

    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, NativeValue?>? resolveAcceptedValue)
    {
        lock (gate) session.ReconcileSubmission(initialized, resolveAcceptedValue);
    }
}
