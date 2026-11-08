using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public static class Compiler
{
    public static Module Compile(string source, string? sourceName = null, int optimization = 1) =>
        new Compilation(source, sourceName ?? "<source>", optimization).Run();
}
