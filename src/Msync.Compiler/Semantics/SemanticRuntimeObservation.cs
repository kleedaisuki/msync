using Msync.Core;

namespace Msync.Compiler.Semantics;

internal sealed record SemanticRuntimeConnection(ConnectionKind Kind, string Address);
internal sealed record SemanticRuntimePath(string Raw, string? WorkingDirectory,
    string? HomeDirectory, string? LocalSpelling, SemanticRuntimeConnection? Connection);
internal sealed record SemanticRuntimeObservation(SemanticRuntimePath? Path = null,
    SemanticRuntimeConnection? Connection = null, string? Text = null);
