using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record IteratorValue(IAsyncEnumerator<Value> Iterator) : Value { public override TypeRef Type => TypeRef.Any; }

    private static bool LocalTransferOperand(Value value) =>
        value is StringValue or PathValue { Connection: null or { Kind: ConnectionKind.Local } };

    private async ValueTask<Value> RunOwnedCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken)
    {
        Module image = module;
        var code = image.Functions[functionIndex].Instructions;
        OperandStack stack = new();
        Dictionary<int, PlanSubmission>? submissions = null;
        for (int pc = startInstruction; pc < code.Count; pc++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instruction = code[pc];
            try
            {
                switch (instruction.Op)
                {
                    case OpCode.Nop: break;
                    case OpCode.Constant: stack.Add(CaptureConstant(image.Constants[instruction.A])); break;
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
                        {
                            if (stack.TryPopNumber(out double assignedNumber))
                                StoreNumberMember(stack.Pop(), instruction.Text!, assignedNumber, instruction.A);
                            else
                            {
                                var value = stack.Pop();
                                StoreMember(stack.Pop(), instruction.Text!, value, instruction.A);
                            }
                            stack.Add(Value.Null); break;
                        }
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
                            var callee = ReadValue(stack.Peek(instruction.A));
                            if (callee is FunctionValue { NativeName: null } function)
                            {
                                int target = SourceCallTarget(function, instruction);
                                Value value = await RunStackFunctionAsync(target, function, instruction.A, ref stack, cancellationToken);
                                stack.CompleteCall(instruction.A, value);
                                break;
                            }
                            if (instruction.Op == OpCode.Call && instruction.B < 0 && callee is not FunctionValue)
                                throw new MsyncException("An open session call requires a Function value.", instruction.Span);
                            var arguments = stack.PopArguments(instruction.A);
                            stack.Discard();
                            stack.Add(await InvokeValueAsync(callee, arguments, cancellationToken, scheduling: instruction.B, origin: instruction.Span));
                            break;
                        }
                    case OpCode.NativeCall:
                        if (instruction.Text == "math.pow" && instruction.A == 2
                            && stack.TryPopNumbers(out double baseNumber, out double exponent))
                            stack.AddNumber(NativeRegistry.Power(baseNumber, exponent));
                        else stack.Add(await InvokeNativeAsync(instruction.Text!, stack.PopArguments(instruction.A), cancellationToken));
                        break;
                    case OpCode.Barrier: await FenceAsync(instruction.A, instruction.B, instruction.Text); break;
                    case OpCode.Acquire:
                        if (instruction.A == 0) await JoinAllAsync();
                        else stack.Add(await AcquireAsync(stack.Pop()));
                        break;
                    case OpCode.Return:
                        return stack.Count == 0 ? Value.Null : stack.Pop();
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
                            var type = instruction.B > 0 && image.Constants[instruction.B - 1] is TypeValue typed ? typed.Data : TypeRef.Any;
                            stack.Add(new RangeValue(LazyItemsAsync(values, instruction.Text, cancellationToken), type)
                            { RetainedValues = values }); break;
                        }
                    case OpCode.DeferRange:
                        stack.Add(CreateDeferredRange(stack.Pop())); break;
                    case OpCode.GetSingleton: stack.Add(GetSingleton(instruction.A)); break;
                    case OpCode.SetSingleton: stack.Add(SetSingleton(instruction.A, stack.Pop())); break;
                    case OpCode.MakeScope:
                        {
                            var scope = MakeScope(instruction, ref stack);
                            stack.Add(scope); break;
                        }
                    case OpCode.GetMember: LoadMember(ref stack, instruction.Text!, instruction.A); break;
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
                            bool local = instruction.Op == OpCode.LocalTransfer
                                || LocalTransferOperand(source) && LocalTransferOperand(destination);
                            var completion = source is RangeValue range
                                ? await ScheduleCohortAsync(range, destination, instruction.Span, local, cancellationToken)
                                : await ScheduleTransferAsync(source, destination, instruction.Span, local, cancellationToken, discarded);
                            if (discarded) pc++;
                            else stack.Add(await completion ? Value.True : Value.False);
                            break;
                        }
                    case OpCode.ExecutePlan:
                        for (int item = 0; item < instruction.B; item++) stack.Discard();
                        (submissions ??= [])[instruction.A] = SubmitPlan(image.ExecutionPlans![instruction.A], cancellationToken);
                        break;
                    case OpCode.PlanResult:
                        for (int item = 0; item < instruction.B; item++) stack.Discard();
                        stack.Add(await ReadPlanResultAsync(image.ExecutionPlans![instruction.A], submissions![instruction.A])
                            ? Value.True : Value.False);
                        break;
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
        }
        return stack.Count == 0 ? Value.Null : stack.Pop();
    }

    private int SourceCallTarget(FunctionValue function, Instruction instruction)
    {
        if (instruction.Op != OpCode.Call) return function.Function;
        if (instruction.B < 0) return SessionDispatchTarget(function, -instruction.B - 1, instruction.Span);
        if (instruction.Text is not null) return DispatchTarget(function, instruction);
        return instruction.B != 0 ? instruction.B - 1 : function.Function;
    }
}
