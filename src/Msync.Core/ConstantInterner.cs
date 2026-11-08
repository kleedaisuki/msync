namespace Msync.Core;

internal sealed class ConstantInterner(List<Value> values)
{
    private Dictionary<Value, int>? indices;
    private int indexed;

    internal int Intern(Value value)
    {
        if (!IsImmutable(value)) { values.Add(value); return values.Count - 1; }
        indices ??= new(ValueRepresentation.Comparer);
        while (indexed < values.Count)
        {
            if (IsImmutable(values[indexed])) indices.TryAdd(values[indexed], indexed);
            indexed++;
        }
        if (indices.TryGetValue(value, out var index)) return index;
        index = values.Count; values.Add(value); indices.Add(value, index); indexed++;
        return index;
    }

    internal static bool IsImmutable(Value value) => value switch
    {
        NullValue or BoolValue or NumberValue or StringValue or PathValue or ConnectionValue or TypeValue => true,
        FunctionValue function => function.Captures is null || function.Captures.All(IsImmutable),
        _ => false
    };
}
