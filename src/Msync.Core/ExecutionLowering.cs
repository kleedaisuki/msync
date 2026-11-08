namespace Msync.Core;

public static class ExecutionLowering
{
    public static Module Lower(Module module)
    {
        if (module.Context is null) throw new MsyncException("Executable lowering requires a bound context");
        var constants = module.Constants.ToList();
        var functions = module.Functions.Select(function => LowerFunction(function, constants, module.Context)).ToArray();
        return Optimizer.Optimize(module with { Constants = constants, Functions = functions }, module.OptimizationLevel);
    }
    private static FunctionCode LowerFunction(FunctionCode function, List<Value> constants, BindingContext context)
    {
        var code = function.Instructions.ToArray();
        var targets = code.SelectMany(i => i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue ? [i.A] : i.Op == OpCode.IteratorNext ? new[] { i.B } : []).ToHashSet();
        var stack = new List<Value?>();
        var locals = new Value?[function.LocalCount];
        Value? Pop() { if (stack.Count == 0) return null; var result = stack[^1]; stack.RemoveAt(stack.Count - 1); return result; }
        for (var pc = 0; pc < code.Length; pc++)
        {
            if (targets.Contains(pc)) { stack.Clear(); Array.Clear(locals); }
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Constant: stack.Add(constants[instruction.A]); break;
                case OpCode.True: stack.Add(Value.True); break;
                case OpCode.False: stack.Add(Value.False); break;
                case OpCode.Null: stack.Add(Value.Null); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                case OpCode.StoreLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: stack.Add(stack.Count == 0 ? null : stack[^1]); break;
                case OpCode.LoadBuiltin:
                    {
                        ConnectionValue? value = instruction.Text?.TrimStart('$') switch
                        {
                            "local" => new(ConnectionKind.Local),
                            "pwd" => new(ConnectionKind.Local, "pwd"),
                            "file" => new(ConnectionKind.Local, "file"),
                            "wsl" => new(ConnectionKind.Wsl),
                            _ => null
                        };
                        if (value is not null) { var index = constants.IndexOf(value); if (index < 0) { index = constants.Count; constants.Add(value); } code[pc] = new(OpCode.Constant, index, Span: instruction.Span); }
                        stack.Add(value); break;
                    }
                case OpCode.Convert:
                    {
                        var value = Pop();
                        stack.Add(instruction.Text == "Path" && value is StringValue text ? new PathValue(text.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : instruction.Text == "String" && value is PathValue path ? new StringValue(path.Raw) : value);
                        break;
                    }
                case OpCode.PathJoin:
                    {
                        var right = Pop(); var left = Pop();
                        var path = left is PathValue p ? p : left is StringValue s ? new PathValue(s.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : null;
                        var text = right switch { PathValue rightPath => rightPath.Raw, StringValue rightText => rightText.Data, _ => null };
                        stack.Add(path is null || text is null ? null : path with { Raw = PathSemantics.Join(path, text) }); break;
                    }
                case OpCode.Qualify:
                    {
                        var item = Pop(); var connection = Pop() as ConnectionValue;
                        var path = item is PathValue p ? p : item is StringValue s ? new PathValue(s.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : null;
                        stack.Add(connection is null || path is null ? null : path with { Connection = connection }); break;
                    }
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    {
                        var destination = Pop(); var source = Pop();
                        if (IsLocal(source) && IsLocal(destination)) code[pc] = instruction with { Op = OpCode.LocalTransfer };
                        stack.Add(null); break;
                    }
                default: stack.Clear(); Array.Clear(locals); break;
            }
        }
        var positions = new int[code.Length + 1];
        var position = 0;
        for (var index = 0; index < code.Length; index++) { positions[index] = position; if (code[index].Op != OpCode.Nop) position++; }
        positions[code.Length] = position;
        var compact = code.Where(i => i.Op != OpCode.Nop).Select(i => i.Op switch
        {
            OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => i with { A = positions[i.A] },
            OpCode.IteratorNext => i with { B = positions[i.B] },
            _ => i
        }).ToArray();
        return function with { Instructions = compact };
    }
    private static bool IsLocal(Value? value) => value is StringValue || value is PathValue { Connection: null or { Kind: ConnectionKind.Local } };
}
