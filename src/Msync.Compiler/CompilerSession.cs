using Msync.Core;

namespace Msync.Compiler;

public sealed class CompilerSession(string sourceName = "<repl>", int optimization = 1)
{
    private readonly Compilation compilation = new("", sourceName, optimization);
    public Module Compile(string submission) => compilation.Submit(submission);
}
