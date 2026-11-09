using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record IteratorValue(IAsyncEnumerator<Value> Iterator) : Value { public override TypeRef Type => TypeRef.Any; }

    private async ValueTask<Value> RunOwnedCodeAsync(int functionIndex, Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        var function = module.Functions[functionIndex];
        OperandStack stack = new();
        var pc = startInstruction;
        while (pc < function.Instructions.Count)
        {
            var block = blocks[functionIndex][pc];
            var outcome = await RunBlockAsync(function, locals, stack, pc, block, cancellationToken);
            if (outcome.Returned) return outcome.Value!;
            pc = outcome.Next;
        }
        return stack.Count == 0 ? Value.Null : stack[^1];
    }
    private readonly record struct BlockOutcome(int Next, Value? Value = null, bool Returned = false);
    private static Instruction SpecializeBlockInstruction(Instruction instruction, OperandStack stack)
    {
        if (instruction.Op != OpCode.Transfer || stack.Count < 2) return instruction;
        static bool Local(Value value) => value is StringValue or PathValue { Connection: null or { Kind: ConnectionKind.Local } };
        return Local(stack[^1]) && Local(stack[^2]) ? instruction with { Op = OpCode.LocalTransfer } : instruction;
    }
    private async ValueTask<BlockOutcome> RunBlockAsync(FunctionCode function, Value[] locals, OperandStack stack, int startInstruction, BasicBlock block, CancellationToken cancellationToken)
    {
        var code = function.Instructions;
        var pc = startInstruction;
        for (; pc < block.End; pc++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instruction = pc == block.Start ? SpecializeBlockInstruction(code[pc], stack) : code[pc];
            try
            {
                switch (instruction.Op)
                {
                    case OpCode.Nop: break;
                    case OpCode.Constant: stack.Add(CaptureConstant(module.Constants[instruction.A])); break;
                    case OpCode.Null: stack.Add(Value.Null); break;
                    case OpCode.True: stack.Add(Value.True); break;
                    case OpCode.False: stack.Add(Value.False); break;
                    case OpCode.LoadLocal: stack.Add(ReadLocal(locals, instruction.A)); break;
                    case OpCode.StoreLocal: WriteLocal(locals, instruction.A, stack.Pop()); break;
                    case OpCode.InitializeLocal: locals[instruction.A] = stack.Pop(); break;
                    case OpCode.LoadCell: stack.Add(CaptureLocal(locals, instruction.A)); break;
                    case OpCode.LoadStorage: stack.Add(locals[instruction.A]); break;
                    case OpCode.GetCapture:
                        {
                            var captures = ((FunctionValue)stack.Pop()).Captures;
                            stack.Add(captures is not null && instruction.A < captures.Count ? captures[instruction.A] : Value.Null); break;
                        }
                    case OpCode.SetMember:
                        { var value = stack.Pop(); StoreMember(stack.Pop(), instruction.Text!, value, instruction.A); stack.Add(Value.Null); break; }
                    case OpCode.Pop: stack.Discard(); break;
                    case OpCode.Dup: stack.Duplicate(); break;
                    case OpCode.Jump:
                        pc = instruction.A - 1; break;
                    case OpCode.JumpIfFalse:
                    case OpCode.JumpIfTrue:
                        if (AsBool(await ResolveAsync(stack.Pop())) == (instruction.Op == OpCode.JumpIfTrue)) pc = instruction.A - 1;
                        break;
                    case OpCode.MakeClosure:
                        {
                            // Function equality includes capture storage identity, even without captures.
                            var captures = instruction.B == 0 ? new Value[0] : stack.PopMany(instruction.B);
                            stack.Add(new FunctionValue(instruction.A, captures)); break;
                        }
                    case OpCode.SpecializeClosure:
                        stack.Add(Specialize((FunctionValue)stack.Pop(), instruction)); break;
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        {
                            var arguments = ValueArguments(stack.PopMany(instruction.A)); var callee = ReadValue(stack.Pop());
                            int? target = null;
                            if (instruction.Op == OpCode.Call && instruction.B < 0)
                            {
                                if (callee is not FunctionValue selected) throw new MsyncException("An open session call requires a Function value.", instruction.Span);
                                if (selected.NativeName is null) target = SessionDispatchTarget(selected, -instruction.B - 1, instruction.Span);
                            }
                            else if (instruction.Op == OpCode.Call && instruction.Text is not null && callee is FunctionValue { NativeName: null } dynamicFunction)
                                target = DispatchTarget(dynamicFunction, instruction);
                            else if (instruction.Op == OpCode.Call && instruction.B != 0)
                            {
                                var selected = (FunctionValue)callee;
                                if (selected.NativeName is null) target = instruction.B - 1;
                            }
                            stack.Add(await InvokeScheduledAsync(callee, arguments, instruction.B, instruction.Span, cancellationToken, target)); break;
                        }
                    case OpCode.NativeCall:
                        if (instruction.Text == "math.pow" && instruction.A == 2
                            && stack.TryPopNumbers(out double baseNumber, out double exponent))
                            stack.AddNumber(NativeRegistry.Power(baseNumber, exponent));
                        else stack.Add(await InvokeNativeAsync(instruction.Text!, stack.PopMany(instruction.A), cancellationToken));
                        break;
                    case OpCode.Barrier: await FenceAsync(instruction.A, instruction.B, instruction.Text); break;
                    case OpCode.Acquire:
                        if (instruction.A == 0) await JoinAllAsync();
                        else stack.Add(await AcquireAsync(stack.Pop()));
                        break;
                    case OpCode.Return:
                        return new(pc, stack.Count == 0 ? Value.Null : stack.Pop(), true);
                    case OpCode.LoadBuiltin: stack.Add(Builtin(instruction.Text!)); break;
                    case OpCode.Convert:
                        {
                            stack.Add(ConvertValue(stack.Pop(), instruction.Text!)); break;
                        }
                    case OpCode.MakeRange:
                        {
                            var values = stack.PopMany(instruction.A);
                            stack.Add(new RangeValue(values, values.Select(v => v.Type).Aggregate(new TypeRef(TypeKind.Never), TypeRef.Union))); break;
                        }
                    case OpCode.MakeLazyRange:
                        {
                            var values = stack.PopMany(instruction.A);
                            var type = instruction.B > 0 && module.Constants[instruction.B - 1] is TypeValue typed ? typed.Data : TypeRef.Any;
                            stack.Add(new RangeValue(LazyItemsAsync(values, instruction.Text, cancellationToken), type)
                            { RetainedValues = values }); break;
                        }
                    case OpCode.DeferRange:
                        stack.Add(CreateDeferredRange(stack.Pop())); break;
                    case OpCode.GetSingleton: stack.Add(GetSingleton(instruction.A)); break;
                    case OpCode.SetSingleton: stack.Add(SetSingleton(instruction.A, stack.Pop())); break;
                    case OpCode.MakeScope:
                        stack.Add(MakeScope(instruction, stack.PopMany(instruction.A))); break;
                    case OpCode.GetMember: stack.Add(Member(stack.Pop(), instruction.Text!, instruction.A)); break;
                    case OpCode.Qualify:
                        {
                            var item = stack.Pop(); var scope = stack.Pop();
                            stack.Add(scope is ConnectionValue connection
                                ? MapValue(item, TypeRef.Path, value => Qualify(connection, value), connection.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl ? connection.ToString() : "local")
                                : Member(scope, Text(await ResolveAsync(item)))); break;
                        }
                    case OpCode.PathJoin:
                        {
                            var right = stack.Pop(); var left = stack.Pop();
                            stack.Add(CombineValues(left, right, TypeRef.Path, (root, item) => PathSemantics.JoinValue(context.AsPath(root), item), PathEndpoint(left))); break;
                        }
                    case OpCode.SelectPath:
                        {
                            var directory = context.AsPath(stack.Pop());
                            stack.Add(new RangeValue(SelectAsync(directory, instruction.Text!, cancellationToken), TypeRef.Path, new PathSelection(directory, instruction.Text!))); break;
                        }
                    case OpCode.Transfer:
                    case OpCode.LocalTransfer:
                        {
                            var destination = stack.Pop(); var source = stack.Pop();
                            bool discarded = pc + 1 < code.Count && code[pc + 1].Op == OpCode.Pop
                                && source is PathValue or StringValue or DeferredValue
                                && destination is PathValue or StringValue or DeferredValue;
                            if (!discarded && source is not (PathValue or StringValue or DeferredValue)) await DrainAsync();
                            var completion = await ScheduleTransferAsync(source, destination, instruction.Span, instruction.Op == OpCode.LocalTransfer, cancellationToken, discarded);
                            if (discarded) pc++;
                            else stack.Add(await completion ? Value.True : Value.False);
                            break;
                        }
                    case OpCode.Not: stack.Add(MapValue(stack.Pop(), TypeRef.Bool, value => AsBool(value) ? Value.False : Value.True)); break;
                    case OpCode.IsNull: stack.Add(stack.Pop() is NullValue ? Value.True : Value.False); break;
                    case OpCode.IsType:
                        {
                            var expected = stack.Pop() as TypeValue ?? throw new MsyncException("Type identity requires a type on the right");
                            var value = stack.Pop(); var type = value is TypeValue typeValue ? typeValue.Data : value.Type;
                            stack.Add(expected.Data.Accepts(type) ? Value.True : Value.False); break;
                        }
                    case OpCode.RegexMatch:
                        {
                            var pattern = instruction.Text ?? Text(stack.Pop()); var value = stack.Pop();
                            stack.Add(value is PathValue or StringValue && Regex.IsMatch(Text(value), pattern, RegexOptions.CultureInvariant) ? Value.True : Value.False); break;
                        }
                    case OpCode.Equal:
                    case OpCode.NotEqual:
                    case OpCode.Less:
                    case OpCode.LessEqual:
                    case OpCode.Greater:
                    case OpCode.GreaterEqual:
                        {
                            if (stack.TryPopNumbers(out double leftNumber, out double rightNumber))
                                stack.Add(CompareNumbers(leftNumber, rightNumber, instruction.Op));
                            else
                            {
                                var right = stack.Pop(); var left = stack.Pop();
                                stack.Add(CompareValues(left, right, instruction.Op));
                            }
                            break;
                        }
                    case OpCode.GetIterator:
                        {
                            var range = stack.Pop() as RangeValue ?? throw new MsyncException("Iteration requires Range");
                            var iterator = range.Items.GetAsyncEnumerator(cancellationToken); iterators.Add(iterator); stack.Add(new IteratorValue(iterator)); break;
                        }
                    case OpCode.IteratorNext:
                        {
                            var iterator = ((IteratorValue)locals[instruction.A]).Iterator;
                            if (await iterator.MoveNextAsync()) stack.Add(iterator.Current);
                            else { await iterator.DisposeAsync(); iterators.Remove(iterator); pc = instruction.B - 1; }
                            break;
                        }
                    case OpCode.Add:
                    case OpCode.Subtract:
                    case OpCode.Multiply:
                    case OpCode.Divide:
                    case OpCode.Remainder:
                        {
                            if (stack.TryPopNumbers(out double leftNumber, out double rightNumber))
                                stack.AddNumber(Arithmetic(leftNumber, rightNumber, instruction.Op));
                            else
                            {
                                var right = stack.Pop(); var left = stack.Pop();
                                stack.Add(ArithmeticValues(left, right, instruction.Op));
                            }
                            break;
                        }
                    case OpCode.Negate:
                        if (stack.TryPopNumber(out double number)) stack.AddNumber(-number);
                        else stack.Add(MapValue(stack.Pop(), TypeRef.Number, value => new NumberValue(-((NumberValue)value).Data)));
                        break;
                    default: throw new MsyncException($"Unsupported bytecode operation {instruction.Op}");
                }
            }
            catch (MsyncException error) when (error.Span is null)
            { throw new MsyncException(error.Message, instruction.Span, error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                string action = instruction.Op switch
                {
                    OpCode.ProcessCall => "Cannot run the requested process.",
                    OpCode.NativeCall => $"Native operation '{instruction.Text}' failed.",
                    OpCode.Call => "The function call failed.",
                    _ => "The operation failed."
                };
                throw new MsyncException(action, instruction.Span, error);
            }
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext)
                return new(pc + 1);
        }
        return new(pc);
    }
}
