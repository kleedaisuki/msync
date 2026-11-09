namespace Msync.Core;

public enum ConnectionKind : byte { Local, Ssh, Wsl, Environment, SearchPath }
public sealed record ConnectionValue(ConnectionKind Kind, string Address = "") : Value
{
    public override TypeRef Type => TypeRef.Connection;
    public override string ToString() => Kind == ConnectionKind.Ssh ? $"ssh://{Address}" : Kind == ConnectionKind.Wsl ? $"wsl://{Address}" : Kind.ToString().ToLowerInvariant();
}
