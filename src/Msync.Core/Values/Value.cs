namespace Msync.Core;

public abstract record Value
{
    public abstract TypeRef Type { get; }
    public static readonly Value Null = new NullValue();
    public static readonly Value True = new BoolValue(true);
    public static readonly Value False = new BoolValue(false);
}
