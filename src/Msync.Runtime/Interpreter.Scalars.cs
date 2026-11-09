using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Value ConvertValue(Value value, string target) => value is DeferredValue
        ? ConvertDeferred(value, target) : ConvertScalar(value, target);

    private Value ConvertScalar(Value value, string target) => target switch
    {
        "Path" => context.AsPath(value),
        "String" => new StringValue(Text(value)),
        _ => throw new MsyncException($"Unknown conversion {target}")
    };

    // Keep captured callbacks out of scalar entrypoints and the bytecode loop.
    private Value ConvertDeferred(Value value, string target)
    {
        if (target == "String")
            return MapValue(value, TypeRef.String, static item => new StringValue(Text(item)), PathEndpoint(value));
        RuntimeContext origin = context.CaptureDirectory();
        return MapValue(value, TypeRef.Path, origin.AsPath, value.Type == TypeRef.String ? "local" : PathEndpoint(value));
    }

    private static Value CompareValues(Value left, Value right, OpCode operation) =>
        left is DeferredValue || right is DeferredValue ? CompareDeferred(left, right, operation)
            : CompareScalars(left, right, operation);

    private static Value CompareNumbers(double left, double right, OpCode operation)
    {
        bool result = operation switch
        {
            OpCode.Equal => left == right,
            OpCode.NotEqual => left != right,
            OpCode.Less => left < right,
            OpCode.LessEqual => left <= right,
            OpCode.Greater => left > right,
            _ => left >= right
        };
        return result ? Value.True : Value.False;
    }

    private static Value CompareScalars(Value left, Value right, OpCode operation)
    {
        int? comparison = Compare(left, right);
        bool result = operation switch
        {
            OpCode.Equal => comparison == 0,
            OpCode.NotEqual => comparison != 0,
            OpCode.Less => comparison < 0,
            OpCode.LessEqual => comparison <= 0,
            OpCode.Greater => comparison > 0,
            _ => comparison >= 0
        };
        return result ? Value.True : Value.False;
    }

    private static Value CompareDeferred(Value left, Value right, OpCode operation) =>
        CombineValues(left, right, TypeRef.Bool, (first, second) => CompareScalars(first, second, operation));

    private static Value ArithmeticValues(Value left, Value right, OpCode operation) =>
        left is DeferredValue || right is DeferredValue ? ArithmeticDeferred(left, right, operation)
            : ArithmeticScalars(left, right, operation);

    private static Value ArithmeticScalars(Value left, Value right, OpCode operation)
    {
        if (operation == OpCode.Add && left is StringValue first && right is StringValue second)
            return new StringValue(first.Data + second.Data);
        return new NumberValue(Arithmetic(((NumberValue)left).Data, ((NumberValue)right).Data, operation));
    }

    private static double Arithmetic(double left, double right, OpCode operation) => operation switch
    {
        OpCode.Add => left + right,
        OpCode.Subtract => left - right,
        OpCode.Multiply => left * right,
        OpCode.Remainder => left % right,
        _ => left / right
    };

    private static Value ArithmeticDeferred(Value left, Value right, OpCode operation) => CombineValues(left, right,
        left.Type == TypeRef.String ? TypeRef.String : TypeRef.Number,
        (first, second) => ArithmeticScalars(first, second, operation));
}
