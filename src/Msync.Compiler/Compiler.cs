using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using Msync.Compiler.Semantics;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public static class Compiler
{
    public static HirModule Analyze(string source, string? sourceName = null) =>
        new SemanticAnalyzer(new SourceBinder().Bind(source, sourceName ?? "<source>")).Analyze();

    public static Module Compile(string source, string? sourceName = null, int optimization = 2) =>
        new Compilation(source, sourceName ?? "<source>", optimization).Run();
}
