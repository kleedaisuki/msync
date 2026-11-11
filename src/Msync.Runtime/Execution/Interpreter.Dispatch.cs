using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record IteratorValue(IAsyncEnumerator<Value> Iterator) : Value { public override TypeRef Type => TypeRef.Any; }

    private static bool LocalTransferOperand(Value value) =>
        value is StringValue or PathValue { Connection: null or { Kind: ConnectionKind.Local } };

    private ValueTask<Value> RunOwnedCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, RuntimeFrameTrace? trace)
    {
        Module image = module;
        if (profile is { } recorder)
            return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, recorder.Enter(image, functionIndex, trace));
        if (trace is not null)
            return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, new RuntimeProfile.Frame(null, null, trace));
        return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, new RuntimeProfile.DisabledFrame());
    }

    // The disabled AOT specialization has no per-opcode profiling branch.
    private async ValueTask<Value> RunOwnedCodeAsync<TFrame>(Module image, int functionIndex, Value[] locals,
        int startInstruction, CancellationToken cancellationToken, TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
    {
        var code = image.Functions[functionIndex].Instructions;
        var array = code as Instruction[];
        int arrayLength = array?.Length ?? 0;
        OperandStack<TFrame> stack = new(frame);
        Dictionary<int, PlanSubmission>? submissions = null;
        for (int pc = startInstruction; pc < (array is null ? code.Count : arrayLength); pc++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instruction = array is null ? code[pc] : array[pc];
            frame.Visit(pc, instruction.Op);
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
                        if (AsBool(await frame.Observe(ResolveAsync(stack.Pop()), pc, instruction, "condition"))
                            == (instruction.Op == OpCode.JumpIfTrue)) pc = instruction.A - 1;
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
                                frame.Call(pc, module, target);
                                Value value = await frame.Observe(
                                    RunStackFunctionAsync(target, function, instruction.A, ref stack, cancellationToken),
                                    pc, instruction, "source_call");
                                stack.CompleteCall(instruction.A, value);
                                break;
                            }
                            if (instruction.Op == OpCode.Call && instruction.B < 0 && callee is not FunctionValue)
                                throw new MsyncException("An open session call requires a Function value.", instruction.Span);
                            var arguments = stack.PopArguments(instruction.A);
                            stack.Discard();
                            stack.Add(await frame.Observe(InvokeValueAsync(callee, arguments, cancellationToken,
                                scheduling: instruction.B, origin: instruction.Span), pc, instruction, "callable"));
                            break;
                        }
                    case OpCode.NativeCall:
                        if (instruction.Text == "math.pow"
                            && stack.TryPopNumbers(out double baseNumber, out double exponent))
                            stack.AddNumber(frame.Power(baseNumber, exponent, image, instruction));
                        else stack.Add(await frame.Observe(InvokeNativeAsync(instruction, stack.PopArguments(instruction.A),
                            cancellationToken), pc, instruction, "native_call"));
                        break;
                    case OpCode.Barrier:
                        await frame.Observe(FenceAsync(instruction.A, instruction.B, instruction.Text), pc, instruction, "fence"); break;
                    case OpCode.Acquire:
                        if (instruction.A == 0) await frame.Observe(JoinAllAsync(), pc, instruction, "acquire_all");
                        else stack.Add(await frame.Observe(AcquireAsync(stack.Pop()), pc, instruction, "acquire_process"));
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
                            var type = instruction.B == 0 ? TypeRef.Any : ((TypeValue)image.Constants[instruction.B - 1]).Data;
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
                                : Member(scope, Text(await frame.Observe(ResolveAsync(item), pc, instruction, "qualified_member")))); break;
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
                            bool discarded = pc + 1 < (array is null ? code.Count : arrayLength)
                                && (array is null ? code[pc + 1] : array[pc + 1]).Op == OpCode.Pop
                                && source is PathValue or StringValue or DeferredValue
                                && destination is PathValue or StringValue or DeferredValue;
                            bool local = instruction.Op == OpCode.LocalTransfer
                                || LocalTransferOperand(source) && LocalTransferOperand(destination);
                            var admission = source is RangeValue range
                                ? ScheduleCohortAsync(range, destination, instruction.Span, local, cancellationToken)
                                : ScheduleTransferAsync(source, destination, instruction.Span, local, cancellationToken, discarded);
                            var completion = await frame.Observe(admission, pc, instruction, "transfer_admission");
                            if (discarded) pc++;
                            else stack.Add(await frame.Observe(completion, pc, instruction, "transfer_completion") ? Value.True : Value.False);
                            break;
                        }
                    case OpCode.ExecutePlan:
                        for (int item = 0; item < instruction.B; item++) stack.Discard();
                        (submissions ??= [])[instruction.A] = SubmitPlan(image.ExecutionPlans![instruction.A], cancellationToken);
                        break;
                    case OpCode.PlanResult:
                        for (int item = 0; item < instruction.B; item++) stack.Discard();
                        stack.Add(await frame.Observe(ReadPlanResultAsync(image.ExecutionPlans![instruction.A], submissions![instruction.A]),
                            pc, instruction, "plan_result")
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
                            if (await frame.Observe(iterator.MoveNextAsync(), pc, instruction, "iterator_next")) stack.Add(iterator.Current);
                            else
                            {
                                await frame.Observe(iterator.DisposeAsync(), pc, instruction, "iterator_dispose");
                                iterators.Remove(iterator);
                                pc = instruction.B - 1;
                            }
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
        if (instruction.Op == OpCode.Call && instruction.B < 0)
            return SessionDispatchTarget(function, -instruction.B - 1, instruction.Span);
        RefreshSessionImage();
        if (instruction.Op == OpCode.Call)
        {
            if (instruction.Text is not null) return DispatchTarget(function, instruction);
            if (instruction.B > 0) return instruction.B - 1;
        }
        RequireArity(module.Functions[function.Function], instruction.A);
        return function.Function;
    }
}
