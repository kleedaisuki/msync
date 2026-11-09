using Msync.Core;

namespace Msync.Compiler;

internal sealed record Info(
    TypeRef Type,
    Template? Function = null,
    ScopeInfo? Scope = null,
    TypeRef? TypeValue = null,
    string? NativeName = null,
    IReadOnlyList<Info>? Alternatives = null,
    Info? Element = null,
    bool? KnownBool = null,
    ConnectionValue? KnownConnection = null,
    bool EditorUnknown = false,
    PathValue? KnownPath = null,
    string? KnownText = null,
    StorageConstraint? Storage = null,
    bool EagerRange = false,
    IndexedStorage? Indexed = null,
    object? MemoryVersion = null);
