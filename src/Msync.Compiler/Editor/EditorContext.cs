using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public enum EditorReceiverKind { Member, PathJoin }
public sealed record EditorContext(IReadOnlyList<SemanticMember> Bindings, SemanticMember? Receiver,
    EditorReceiverKind ReceiverKind = EditorReceiverKind.Member, ConnectionValue? PhysicalEndpoint = null, bool HasPhysicalOwner = false)
{
    public bool IsTypePosition { get; init; }
    public bool CanDeclareAssociatedType { get; init; }
    public bool IsGlobalImport { get; init; }
}

internal readonly record struct EditorPathFlowStart(int Selections, int Leaves);
