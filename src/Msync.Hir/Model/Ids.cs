namespace Msync.Hir;

public readonly record struct HirSourceId(int Value);
public readonly record struct HirTypeId(int Value);
public readonly record struct HirLocationId(int Value);
public readonly record struct HirFunctionId(int Value);
public readonly record struct HirTemplateId(int Value);
public readonly record struct HirCallableId(int Value, bool IsTemplate = false)
{
    public static HirCallableId From(HirFunctionId id) => new(id.Value);
    public static HirCallableId From(HirTemplateId id) => new(id.Value, true);
}
public readonly record struct HirNodeId(int Value)
{
    public static HirNodeId None => new(-1);
    public bool IsNone => Value < 0;
}
public readonly record struct HirLoopId(int Value);
public readonly record struct HirSourceRange(HirSourceId Source, int Start, int Length);
public readonly record struct HirEdgeRange(int Start, int Count);
