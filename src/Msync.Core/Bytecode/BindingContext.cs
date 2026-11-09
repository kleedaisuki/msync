namespace Msync.Core;

public sealed record BindingContext(string WorkingDirectory, string HomeDirectory, string? SourceFile = null);
