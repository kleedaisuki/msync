using Msync.Core;

namespace Msync.Compiler;

public sealed class NativeCompilerSession : CompilerSession
{
    public NativeCompilerSession(string sourceName = "<repl>", int optimization = 2)
        : base(sourceName, optimization) { }

    public NativeCompilerSession(string sourceName, int optimization, CompilationTrace? trace)
        : base(sourceName, optimization, trace) { }
}
