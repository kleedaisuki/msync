namespace Msync.Optimization;

internal static class LinearEffectOptimization
{
    private readonly record struct Operand(TypeKind Kind, int Literal);

    internal static bool TryOptimize(Module module, int level, out Module result)
    {
        result = module;
        var functions = new FunctionCode[module.Functions.Count];
        var changed = false;
        for (var index = 0; index < functions.Length; index++)
        {
            if (!TryNormalize(module.Functions[index], module.Constants, out var function)) return false;
            functions[index] = function;
            changed |= !ReferenceEquals(function, module.Functions[index]);
        }
        var normalized = changed ? module with { Functions = functions, Blocks = null, OptimizationLevel = level }
            : module with { OptimizationLevel = level };
        var compact = ModuleOptimization.Compact(normalized);
        result = compact.Blocks is null ? BlockBuilder.Form(compact) : compact;
        return true;
    }

    private static bool TryNormalize(FunctionCode function, IReadOnlyList<Value> constants, out FunctionCode result)
    {
        result = function;
        if (function.LocalCount != 0 || function.ParameterCount != 0 || function.CaptureCount != 0) return false;
        var code = function.Instructions;
        if (code.Count == 0 || code[^1].Op != OpCode.Return) return false;
        var stack = new List<Operand>(2);
        bool[]? removed = null;
        for (var pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Constant:
                    var literal = constants[instruction.A];
                    if (literal is not (NullValue or BoolValue or NumberValue or StringValue or PathValue or ConnectionValue or TypeValue)) return false;
                    stack.Add(new(literal.Type.Kind, pc)); break;
                case OpCode.Null: stack.Add(new(TypeKind.Null, pc)); break;
                case OpCode.True: case OpCode.False: stack.Add(new(TypeKind.Bool, pc)); break;
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    if (stack.Count < 2 || !PathOrString(stack[^1].Kind) || !PathOrString(stack[^2].Kind)) return false;
                    stack.RemoveRange(stack.Count - 2, 2); stack.Add(new(TypeKind.Bool, -1)); break;
                case OpCode.Pop:
                    if (stack.Count == 0) return false;
                    var operand = stack[^1]; stack.RemoveAt(stack.Count - 1);
                    if (operand.Literal < 0) break;
                    removed ??= new bool[code.Count]; removed[operand.Literal] = removed[pc] = true; break;
                case OpCode.Return:
                    if (pc != code.Count - 1 || stack.Count > 1) return false;
                    break;
                default: return false;
            }
        }
        if (removed is not null) result = function with { Instructions = code.Where((_, index) => !removed[index]).ToArray() };
        return true;
    }

    private static bool PathOrString(TypeKind kind) => kind is TypeKind.Path or TypeKind.String;
}
