using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

internal readonly record struct KnownValue(Value? Value, int Start = -1, int End = -1)
{
    internal bool Known => Value is not null;
    internal KnownValue At(int instruction) => new(Value, instruction, instruction);
    internal static readonly KnownValue Unknown = new(null);
}

internal static class KnownEvaluation
{
    internal static Value? Unary(OpCode operation, Value input) => operation switch
    {
        OpCode.Not when input is BoolValue value => value.Data ? Value.False : Value.True,
        OpCode.IsNull => input is NullValue ? Value.True : Value.False,
        OpCode.Negate when input is NumberValue number => new NumberValue(-number.Data),
        _ => null
    };

    internal static Value? Binary(OpCode operation, Value left, Value right)
    {
        if (left is NumberValue first && right is NumberValue second)
        {
            double a = first.Data, b = second.Data;
            return operation switch
            {
                OpCode.Add => new NumberValue(a + b),
                OpCode.Subtract => new NumberValue(a - b),
                OpCode.Multiply => new NumberValue(a * b),
                OpCode.Divide => new NumberValue(a / b),
                OpCode.Remainder => new NumberValue(a % b),
                OpCode.Equal => Bool(a == b),
                OpCode.NotEqual => Bool(a != b),
                OpCode.Less => Bool(a < b),
                OpCode.LessEqual => Bool(a <= b),
                OpCode.Greater => Bool(a > b),
                OpCode.GreaterEqual => Bool(a >= b),
                _ => null
            };
        }
        if (operation == OpCode.Add && left is StringValue aText && right is StringValue bText)
            return new StringValue(aText.Data + bText.Data);
        if (operation == OpCode.IsType && right is TypeValue type)
            return Bool(type.Data.Accepts(left is TypeValue actual ? actual.Data : left.Type));
        int? comparison = (left, right) switch
        {
            (BoolValue a, BoolValue b) => a.Data.CompareTo(b.Data),
            (TypeValue a, TypeValue b) => a.Data == b.Data ? 0 : 1,
            (StringValue or PathValue, StringValue or PathValue) => TextSemantics.CompareOrdinal(
                left is PathValue a ? a.NativeText : ((StringValue)left).Data,
                right is PathValue b ? b.NativeText : ((StringValue)right).Data),
            _ => null
        };
        if (comparison is null) return null;
        return operation switch
        {
            OpCode.Equal => Bool(comparison == 0),
            OpCode.NotEqual => Bool(comparison != 0),
            OpCode.Less => Bool(comparison < 0),
            OpCode.LessEqual => Bool(comparison <= 0),
            OpCode.Greater => Bool(comparison > 0),
            OpCode.GreaterEqual => Bool(comparison >= 0),
            _ => null
        };
    }

    internal static Value? Convert(Value value, string? target, RuntimeContext context) => target switch
    {
        "Path" when value is PathValue => value,
        "Path" when value is StringValue text => new PathValue(text.Data, context.WorkingDirectory,
            HomeDirectory: context.HomeDirectory),
        "String" when value is PathValue path => new StringValue(path.NativeText),
        "String" when value is StringValue => value,
        _ => null
    };

    internal static Value? Qualify(ConnectionValue connection, Value value, RuntimeContext context)
    {
        PathValue path = context.AsPath(value);
        if (connection.Kind == ConnectionKind.Environment)
        {
            string? environment = Environment.GetEnvironmentVariable(path.Raw);
            return environment is null ? null : new PathValue(environment, context.WorkingDirectory,
                HomeDirectory: context.HomeDirectory);
        }
        if (connection.Kind is not (ConnectionKind.Local or ConnectionKind.Ssh or ConnectionKind.Wsl)) return null;
        if (connection.Kind == ConnectionKind.Local && connection.Address == "file")
        {
            if (context.SourceFile is not { } source) return null;
            path = path.WithRaw(path.Raw switch
            {
                "this" => source,
                "parent" => Path.GetDirectoryName(source)!,
                _ => Path.Combine(Path.GetDirectoryName(source)!, path.NativeText)
            });
        }
        if (connection.Kind == ConnectionKind.Local && connection.Address == "pwd" && path.Raw == "pwd")
            return new PathValue(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
        return PathSemantics.Qualify(path, connection);
    }

    internal static bool Portable(Value value) => value is NullValue or BoolValue or StringValue or NumberValue
        or PathValue or ConnectionValue or TypeValue
        || value is RangeValue { Selection: null, Resolve: null, SnapshotValues: { } values } && values.All(Portable);

    internal static Value Regex(Value value, string pattern) => Bool(value is PathValue or StringValue
        && System.Text.RegularExpressions.Regex.IsMatch(value is PathValue path ? path.NativeText : ((StringValue)value).Data,
            pattern, RegexOptions.CultureInvariant));

    internal static Value Bool(bool result) => result ? Value.True : Value.False;
}
