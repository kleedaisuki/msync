namespace Msync.Optimization;

public static class ExecutionLowering
{
    public static Module Lower(Module module)
    {
        var lowered = Rewrite(module);
        return lowered.Blocks is null ? BlockBuilder.Form(lowered) : lowered;
    }

    internal static Module Rewrite(Module module)
    {
        if (module.Context is null) throw new MsyncException("Executable lowering requires a bound context");
        if (!module.Functions.Any(NeedsLowering)) return module;
        var constants = module.Constants.ToList();
        var interner = new ConstantInterner(constants);
        var functions = module.Functions.Select(function => LowerFunction(function, constants, module.Context, interner)).ToArray();
        if (functions.Select((function, index) => ReferenceEquals(function, module.Functions[index])).All(same => same))
            return module;
        return module with { Constants = constants, Functions = functions, Blocks = null };
    }
    private static FunctionCode LowerFunction(FunctionCode function, List<Value> constants, BindingContext context, ConstantInterner interner)
    {
        if (!NeedsLowering(function)) return function;
        var instructions = function.Instructions;
        Instruction[]? rewritten = null;
        var hasNops = false;
        void Replace(int index, Instruction instruction)
        {
            rewritten ??= instructions.ToArray();
            rewritten[index] = instruction;
        }
        var targets = instructions.SelectMany(i => i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue ? [i.A] : i.Op == OpCode.IteratorNext ? new[] { i.B } : []).ToHashSet();
        var stack = new List<Value?>();
        var locals = new Value?[function.LocalCount];
        Value? Pop() { if (stack.Count == 0) return null; var result = stack[^1]; stack.RemoveAt(stack.Count - 1); return result; }
        for (var pc = 0; pc < instructions.Count; pc++)
        {
            if (targets.Contains(pc)) { stack.Clear(); Array.Clear(locals); }
            var instruction = instructions[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: hasNops = true; break;
                case OpCode.Constant: stack.Add(constants[instruction.A]); break;
                case OpCode.True: stack.Add(Value.True); break;
                case OpCode.False: stack.Add(Value.False); break;
                case OpCode.Null: stack.Add(Value.Null); break;
                case OpCode.LoadLocal:
                case OpCode.LoadStorage: stack.Add(locals[instruction.A]); break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: stack.Add(stack.Count == 0 ? null : stack[^1]); break;
                case OpCode.LoadBuiltin:
                    {
                        ConnectionValue? value = Builtin(instruction.Text);
                        if (value is not null) Replace(pc, new(OpCode.Constant, interner.Intern(value), Span: instruction.Span));
                        stack.Add(value); break;
                    }
                case OpCode.Convert:
                    {
                        var value = Pop();
                        stack.Add(instruction.Text switch
                        {
                            "Path" when value is StringValue text => new PathValue(text.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory),
                            "Path" when value is PathValue => value,
                            "String" when value is PathValue path => new StringValue(path.NativeText),
                            "String" when value is StringValue => value,
                            _ => null
                        });
                        break;
                    }
                case OpCode.PathJoin:
                    {
                        var right = Pop(); var left = Pop();
                        var path = left is PathValue p ? p : left is StringValue s ? new PathValue(s.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : null;
                        stack.Add(path is null || right is not (PathValue or StringValue) ? null : PathSemantics.JoinValue(path, right)); break;
                    }
                case OpCode.Qualify:
                    {
                        var item = Pop(); var connection = Pop() as ConnectionValue;
                        var path = item is PathValue p ? p : item is StringValue s ? new PathValue(s.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : null;
                        stack.Add(connection is null || path is null ? null : PureEvaluation.Qualify(connection, path, context)); break;
                    }
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    {
                        var destination = Pop(); var source = Pop();
                        if (instruction.Op == OpCode.Transfer && IsLocal(source) && IsLocal(destination)) Replace(pc, instruction with { Op = OpCode.LocalTransfer });
                        stack.Add(null); break;
                    }
                default: stack.Clear(); Array.Clear(locals); break;
            }
        }
        if (!hasNops) return rewritten is null ? function : function with { Instructions = rewritten };
        var code = rewritten ?? instructions.ToArray();
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
    private static bool NeedsLowering(FunctionCode function) => function.Instructions.Any(instruction => instruction.Op is OpCode.LoadBuiltin or OpCode.Transfer or OpCode.Nop);
    internal static ConnectionValue? Builtin(string? spelling) => spelling?.TrimStart('$') switch
    {
        "local" => new(ConnectionKind.Local),
        "pwd" => new(ConnectionKind.Local, "pwd"),
        "file" => new(ConnectionKind.Local, "file"),
        "wsl" => new(ConnectionKind.Wsl),
        _ => null
    };
    private static bool IsLocal(Value? value) => value is StringValue || value is PathValue { Connection: null or { Kind: ConnectionKind.Local } };
}
