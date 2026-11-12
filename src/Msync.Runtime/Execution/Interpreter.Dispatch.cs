using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private static bool LocalTransferOperand(Value value) =>
        value is StringValue or PathValue { Connection: null or { Kind: ConnectionKind.Local } };

    private ValueTask<Value> RunOwnedCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, RuntimeFrameTrace? trace, int borrowedAt, ClosureCaptures? split,
        double[]? raw, ArraySegment<Value?> arguments)
    {
        Module image = module;
        if (profile is { } recorder)
            return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, recorder.Enter(image, functionIndex, trace, borrowedAt >= 0, split is not null, raw is null ? 0 : CountRawArguments(arguments)), borrowedAt, split, raw);
        if (trace is not null)
            return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, new RuntimeProfile.Frame(null, null, trace), borrowedAt, split, raw);
        return RunOwnedCodeAsync(image, functionIndex, locals, startInstruction, cancellationToken, new RuntimeProfile.DisabledFrame(), borrowedAt, split, raw);
    }

    private ValueTask<Value> RunOwnedCodeAsync<TFrame>(Module image, int functionIndex, Value[] locals,
        int startInstruction, CancellationToken cancellationToken, TFrame frame, int borrowedAt,
        ClosureCaptures? split, double[]? numbers) where TFrame : struct, RuntimeProfile.IFrame
    {
        var transport = sessionImage is null ? executionTargets.Frame(functionIndex) : default;
        int firstNumericLocal = transport.FirstNumericLocal;
        // Native and contextual continuations arrive with canonical locals.
        if (transport.Words && startInstruction == 0)
            return RunOwnedCodeAsync<TFrame, FrameSlots.RawWord>(image, functionIndex, locals, startInstruction,
                cancellationToken, frame, borrowedAt, split, numbers, firstNumericLocal);
        if (numbers is not null || firstNumericLocal >= 0)
            return RunOwnedCodeAsync<TFrame, FrameSlots.Raw>(image, functionIndex, locals, startInstruction,
                cancellationToken, frame, borrowedAt, split, numbers, firstNumericLocal);
        return split is null
            ? RunOwnedCodeAsync<TFrame, FrameSlots.Contiguous>(image, functionIndex, locals, startInstruction,
                cancellationToken, frame, borrowedAt, null, null)
            : RunOwnedCodeAsync<TFrame, FrameSlots.Split>(image, functionIndex, locals, startInstruction,
                cancellationToken, frame, borrowedAt, split, null);
    }

    private ValueTask<Value> RunOwnedCodeAsync<TFrame, TLayout>(Module image, int functionIndex, Value[] locals,
        int startInstruction, CancellationToken cancellationToken, TFrame frame, int borrowedAt,
        ClosureCaptures? split, double[]? numbers, int firstNumericLocal = -1)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        DispatchState<TFrame>? state = null;
        var value = Dispatch<TFrame, TLayout>(image, functionIndex, locals, startInstruction, cancellationToken,
            frame, borrowedAt, split, numbers, firstNumericLocal, ref state);
        return state is null ? new(value!) : ResumeDispatchAsync<TFrame, TLayout>(image, functionIndex, state, cancellationToken);
    }

    private Value? Dispatch<TFrame, TLayout>(Module image, int functionIndex, Value[]? locals, int pc,
        CancellationToken cancellationToken, TFrame frame, int borrowedAt, ClosureCaptures? split,
        double[]? numbers, int firstNumericLocal, ref DispatchState<TFrame>? state)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        var body = image.Functions[functionIndex];
        var code = body.Instructions;
        FrameSlots slots;
        OperandStack<TFrame> stack;
        Instruction[]? array;
        Dictionary<int, PlanSubmission>? submissions;
        ExecutionTargets.NumericBackedges? numericBackedges;
        ExecutionTargets.EffectTails? effectTails;
        NumericLoopSites? numericLoops;
        if (state is null)
        {
            if (pc == 0 && body.CodeSignature?.RequiredStatics is { Count: > 0 } requiredStatics)
                staticScopes.RequireReady(requiredStatics);
            slots = new(locals!, borrowedAt, split, body.LocalCount, numbers, firstNumericLocal);
            // Native prefixes and contextual continuations may enter with
            // portable locals at a nonzero PC. Ordinary suspension retains
            // the already selected dispatch and its word bank.
            bool words = typeof(TLayout) == typeof(FrameSlots.RawWord);
            var lowered = typeof(TFrame) == typeof(RuntimeProfile.DisabledFrame) ? executionTargets.Dispatch(functionIndex, words)
                : context.Timeline is null && frame.NativeLoopObservation == NativeLoopObservation.Counts
                    ? executionTargets.CountedDispatch(functionIndex, words) : executionTargets.BoundDispatch(functionIndex, words);
            array = (lowered ?? code) as Instruction[];
            stack = new(frame);
            submissions = null;
            numericBackedges = executionTargets.Numeric(functionIndex);
            effectTails = executionTargets.Tails(functionIndex);
            if (effectTails is not null && !NumericLoop.CanRunTail(frame.NativeLoopObservation)) effectTails = null;
            numericLoops = null;
        }
        else
        {
            slots = state.Slots;
            stack = state.Stack;
            frame = state.Frame;
            array = state.Array;
            submissions = state.Submissions;
            numericBackedges = state.NumericBackedges;
            effectTails = state.EffectTails;
            numericLoops = state.NumericLoops;
        }
        int arrayLength = array?.Length ?? 0;
        BoundScopePlaces? boundPlaces = null;
        Instruction instruction;
        for (; pc < (array is null ? code.Count : arrayLength); pc++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            instruction = array is null ? code[pc] : array[pc];
            if (typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame))
                frame.Visit(pc, instruction.Op switch
                {
                    ExecutionTargets.DirectSourceCall or ExecutionTargets.NativeWorkerCall => OpCode.Call,
                    ExecutionTargets.NumericPrimitiveCall or ExecutionTargets.ArenaPrimitiveCall => OpCode.NativeCall,
                    ExecutionTargets.LoadWordLocal => OpCode.LoadLocal,
                    ExecutionTargets.StoreWordLocal => code[pc].Op,
                    ExecutionTargets.ScopeComparison => OpCode.LoadLocal,
                    ExecutionTargets.ReadonlyNumberFoldEntry => OpCode.GetIterator,
                    _ => instruction.Op
                });
            try
            {
                FunctionValue sourceFunction;
                int sourceTarget;
                Value callable;
                ValueTask<Value> sourceInvocation;
                switch (instruction.Op)
                {
                    case OpCode.Nop: break;
                    case OpCode.Constant: stack.Add(CaptureConstant(image.Constants[instruction.A])); break;
                    case OpCode.Null: stack.Add(Value.Null); break;
                    case OpCode.True: stack.Add(Value.True); break;
                    case OpCode.False: stack.Add(Value.False); break;
                    case ExecutionTargets.ScopeComparison:
                        if (!TryGuardedScopeRegion<TFrame, TLayout>(functionIndex, pc, (Instruction[])code, Places(image, ref boundPlaces), ref slots, ref stack,
                            ref frame, cancellationToken, out int scopeNext))
                        {
                            frame.GuardedScopeMiss();
                            instruction = code[pc];
                            goto case OpCode.LoadLocal;
                        }
                        pc = scopeNext - 1;
                        break;
                    case OpCode.LoadLocal:
                        {
                            var value = slots.Peek<TLayout>(instruction.A);
                            if (FrameSlots.IsRaw<TLayout>() && value is null)
                            {
                                // Returning an ordinary Value cannot preserve
                                // this raw lane. Avoid growing a callee stack
                                // sidecar merely to box the same local.
                                int next = pc + 1;
                                if (next < (array is null ? code.Count : arrayLength)
                                    && (array is null ? code[next] : array[next]).Op == OpCode.Return)
                                {
                                    var result = slots.Materialize(instruction.A, ref frame);
                                    cancellationToken.ThrowIfCancellationRequested();
                                    frame.Visit(next, OpCode.Return);
                                    return result;
                                }
                                stack.AddNumber(slots.RawNumber(instruction.A));
                            }
                            else stack.Add(value is CellValue cell ? cell.Data : value!);
                            break;
                        }
                    case OpCode.StoreLocal:
                        if (!FrameSlots.IsRaw<TLayout>()
                            || !slots.TryStoreRawNumber(instruction.A, ref stack, ref frame, initialize: false))
                            slots.Store<TFrame, TLayout>(instruction.A, stack.Pop(), ref frame);
                        break;
                    case ExecutionTargets.LoadWordLocal:
                        if (typeof(TLayout) == typeof(FrameSlots.RawWord))
                        {
                            slots.LoadWord(instruction.A, (instruction.B & (int)WordTransport.Result) != 0, ref stack, ref frame);
                            break;
                        }
                        goto default;
                    case ExecutionTargets.StoreWordLocal:
                        if (typeof(TLayout) == typeof(FrameSlots.RawWord))
                        {
                            slots.StoreWord(instruction.A, (instruction.B & (int)WordTransport.First) != 0, ref stack, ref frame);
                            break;
                        }
                        goto default;
                    case OpCode.InitializeLocal:
                        if (!FrameSlots.IsRaw<TLayout>()
                            || !slots.TryStoreRawNumber(instruction.A, ref stack, ref frame, initialize: true))
                            slots.Initialize(instruction.A, stack.Pop(), ref frame);
                        break;
                    case OpCode.LoadCell:
                        stack.Add(slots.Capture<TFrame, TLayout>(instruction.A, Places(image, ref boundPlaces), instruction.B - 1, ref frame));
                        break;
                    case OpCode.LoadStorage:
                        if (FrameSlots.IsRaw<TLayout>())
                            LoadScopeStorage(image, functionIndex, pc, instruction.A, ref slots, ref stack, ref frame);
                        else stack.Add(slots.Storage<TFrame, TLayout>(instruction.A, ref frame));
                        break;
                    case OpCode.GetCapture:
                        {
                            var captures = ((FunctionValue)stack.Pop()).Captures;
                            stack.Add(captures is not null && instruction.A < captures.Count ? captures[instruction.A] : Value.Null); break;
                        }
                    case OpCode.StoreField:
                        {
                            if (stack.TryPopNumber(out double value))
                            {
                                var owner = (ScopeValue)stack.Pop();
                                owner.SetNumber(Places(image, ref boundPlaces).WriteField(owner, instruction.A, null), value);
                            }
                            else
                            {
                                var assigned = stack.Pop();
                                var owner = (ScopeValue)stack.Pop();
                                owner.SetField(Places(image, ref boundPlaces).WriteField(owner, instruction.A, assigned), assigned);
                            }
                            goto MemberStored;
                        }
                    case OpCode.StoreStaticField:
                        if (stack.TryPopNumber(out double staticNumber))
                            staticScopes.Write(instruction.A, instruction.B, staticNumber);
                        else staticScopes.Write(instruction.A, instruction.B, stack.Pop());
                        goto MemberStored;
                    MemberStored:
                        stack.Add(Value.Null);
                        if (effectTails is not null)
                            RunEffectTail(effectTails, image, functionIndex, code, ref slots, ref numericLoops,
                                ref stack, ref frame, ref pc, cancellationToken);
                        break;
                    case OpCode.Pop: stack.Discard(); break;
                    case OpCode.Dup: stack.Duplicate(); break;
                    case OpCode.JumpIfStaticReady:
                        if (staticScopes.Ready(instruction.B)) pc = instruction.A - 1;
                        break;
                    case OpCode.Jump:
                        int targetPc = instruction.A;
                        if (numericBackedges is not null && numericBackedges.Contains(pc))
                        {
                            numericLoops ??= GetNumericLoopSites(image, functionIndex);
                            if (numericLoops.Backedge(targetPc, pc) is { } loop
                                && loop.TryRun(ref slots, code, ref stack, ref frame, cancellationToken, out int continuation))
                                targetPc = continuation;
                        }
                        pc = targetPc - 1; break;
                    case OpCode.JumpIfFalse:
                    case OpCode.JumpIfTrue:
                        {
                            var pending = frame.Observe(ResolveAsync(stack.Pop()), pc, instruction, "condition");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Condition).Value = pending;
                                goto Suspended;
                            }
                            if (AsBool(pending.GetAwaiter().GetResult()) == (instruction.Op == OpCode.JumpIfTrue))
                                pc = instruction.A - 1;
                            break;
                        }
                    case OpCode.MakeClosure:
                        {
                            // Function equality includes capture storage identity, even without captures.
                            var captures = instruction.B == 0 ? new Value[0] : stack.PopMany(instruction.B);
                            stack.Add(new FunctionValue(instruction.A, ClosureCaptures.Own(captures))); break;
                        }
                    case OpCode.SpecializeClosure:
                        stack.Add(Specialize((FunctionValue)stack.Pop(), instruction)); break;
                    case ExecutionTargets.DirectSourceCall:
                        {
                            bool rawResult = instruction.A != 0;
                            instruction = code[pc];
                            var callee = ReadValue(stack.Peek(1));
                            if (callee is not FunctionValue { NativeName: null } function)
                            {
                                callable = callee;
                                goto InvokeCallable;
                            }
                            sourceFunction = function;
                            sourceTarget = SourceCallTarget(function, instruction);
                            frame.Call(pc, module, sourceTarget);
                            if (!TryDirectSourceCall(image, sourceTarget, function, ref stack, cancellationToken, out double directNumber))
                                goto InvokeSource;

                            if (rawResult) stack.CompleteNumberCall(1, directNumber);
                            else
                            {
                                frame.Box();
                                stack.CompleteCall(1, new NumberValue(directNumber));
                            }
                            goto CallCompleted;
                        }
                    case ExecutionTargets.NativeWorkerCall:
                        {
                            instruction = code[pc];
                            var callee = ReadValue(stack.Peek(instruction.A));
                            if (callee is not FunctionValue { NativeName: null } function)
                            {
                                callable = callee;
                                goto InvokeCallable;
                            }
                            sourceFunction = function;
                            sourceTarget = instruction.B - 1;
                            if (sourceTarget != function.Function)
                                RequireCaptureContract(function, sourceTarget, instruction.Span);
                            frame.Call(pc, image, sourceTarget);
                            if (TryNativeWorkerCall(image, sourceTarget, function, instruction.A, ref stack, cancellationToken,
                                out var continuation))
                            {
                                if (continuation is not { } selected) goto CallCompleted;
                                sourceInvocation = selected;
                                goto AwaitSource;
                            }
                            goto InvokePredicateOrSource;
                        }
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        {
                            var callee = ReadValue(stack.Peek(instruction.A));
                            if (callee is not FunctionValue { NativeName: null } function)
                            {
                                callable = callee;
                                goto InvokeCallable;
                            }
                            sourceFunction = function;
                            sourceTarget = SourceCallTarget(function, instruction);
                            if (instruction.Op != OpCode.Call || instruction.B <= 0 || instruction.Text is not null)
                            {
                                RequireExecutable(module.Functions[sourceTarget], instruction.Span);
                                var places = CurrentPlaces;
                                foreach (var parameter in places.Parameters(sourceTarget))
                                {
                                    var argument = stack.PeekReference(instruction.A - parameter.Position - 1);
                                    places.AdmitArgument(parameter, argument is CellValue cell ? cell.Data : argument, instruction.Span);
                                }
                            }
                            frame.Call(pc, module, sourceTarget);
                        }
                    InvokePredicateOrSource:
                        {
                            if (instruction.A == 2 && executionTargets.Predicate(sourceTarget) is { } predicate
                                && TryPredicateCall(predicate, sourceTarget, sourceFunction, ref stack, cancellationToken))
                                goto CallCompleted;
                        }
                    InvokeSource:
                        sourceInvocation = RunStackFunctionAsync(sourceTarget, sourceFunction, instruction.A, ref stack, cancellationToken);
                    AwaitSource:
                        {
                            var pending = frame.Observe(sourceInvocation, pc, instruction, "source_call");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.SourceCall).Value = pending;
                                goto Suspended;
                            }
                            stack.CompleteCall(instruction.A, pending.GetAwaiter().GetResult());
                            goto CallCompleted;
                        }
                    InvokeCallable:
                        {
                            var pending = frame.Observe(InvokeStackValueAsync(callable, instruction, ref stack, cancellationToken),
                                pc, instruction, "callable");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.ResultAndTail).Value = pending;
                                goto Suspended;
                            }
                            stack.Add(pending.GetAwaiter().GetResult());
                        }
                    CallCompleted:
                        if (effectTails is not null)
                            RunEffectTail(effectTails, image, functionIndex, code, ref slots, ref numericLoops,
                                ref stack, ref frame, ref pc, cancellationToken);
                        break;
                    case ExecutionTargets.NumericPrimitiveCall:
                        {
                            int primitive = instruction.B;
                            instruction = code[pc];
                            if (TryRunPrimitive<TFrame, TLayout>(image, instruction, primitive, ref frame, ref stack, cancellationToken))
                                goto NativeCompleted;
                            goto case OpCode.NativeCall;
                        }
                    case ExecutionTargets.ArenaPrimitiveCall:
                        {
                            int arenaOperation = instruction.B;
                            instruction = code[pc];
                            if (TryInlineArena<TFrame, TLayout>(image, instruction, frame, ref stack, cancellationToken, arenaOperation))
                                goto NativeCompleted;
                            goto case OpCode.NativeCall;
                        }
                    case OpCode.NativeCall:
                        if (!(instruction is { A: 1, Text: "fs.filename" or "fs.stem" or "fs.extension" }
                            && TryPathTextView(image, code, ref pc, ref frame, ref stack, cancellationToken)))
                        {
                            if (!TryInlineNative(image, instruction, frame, ref stack, cancellationToken)
                                && !(instruction.Text == "cast" && TryBoundNumberText(image, functionIndex, pc, instruction,
                                    frame, ref stack, cancellationToken)))
                            {
                                var pending = frame.Observe(InvokeNativeAsync(instruction, stack.PopArguments(instruction.A),
                                    cancellationToken), pc, instruction, "native_call");
                                if (!pending.IsCompleted)
                                {
                                    Pause(ref state, ref frame, DispatchResume.ResultAndTail).Value = pending;
                                    goto Suspended;
                                }
                                stack.Add(pending.GetAwaiter().GetResult());
                            }
                        }
                    NativeCompleted:
                        if (effectTails is not null)
                            RunEffectTail(effectTails, image, functionIndex, code, ref slots, ref numericLoops,
                                ref stack, ref frame, ref pc, cancellationToken);
                        break;
                    case OpCode.Barrier:
                        {
                            var pending = frame.Observe(FenceAsync(instruction.A, instruction.B, instruction.Text), pc, instruction, "fence");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Completion).Completion = pending;
                                goto Suspended;
                            }
                            pending.GetAwaiter().GetResult();
                            break;
                        }
                    case OpCode.Acquire:
                        if (instruction.A == 0)
                        {
                            var pending = frame.Observe(JoinAllAsync(), pc, instruction, "acquire_all");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Completion).Completion = pending;
                                goto Suspended;
                            }
                            pending.GetAwaiter().GetResult();
                        }
                        else
                        {
                            var pending = frame.Observe(AcquireAsync(stack.Pop()), pc, instruction, "acquire_process");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Result).Value = pending;
                                goto Suspended;
                            }
                            stack.Add(pending.GetAwaiter().GetResult());
                        }
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
                            if (instruction.A == 1)
                            {
                                stack.Add(RangeValue.OwnSingleton(stack.Pop()));
                                break;
                            }
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
                    case OpCode.NewScope:
                        {
                            var layout = Places(image, ref boundPlaces).Layout(instruction.A);
                            stack.Add(stack.PopScope(scopeAllocator ??= new(), layout, layout.Count, instruction.B));
                            break;
                        }
                    case OpCode.PublishStatic:
                        {
                            var layout = Places(image, ref boundPlaces).StaticLayout(instruction.A);
                            if (staticScopes.Publish(instruction.A, layout, scopeAllocator ??= new(), ref stack))
                                frame.StaticScopePublished();
                            break;
                        }
                    case OpCode.LoadStaticScope:
                        stack.Add(staticScopes.Identity(instruction.A, Places(image, ref boundPlaces).StaticLayout(instruction.A), out bool materialized));
                        if (materialized) frame.StaticScopeMaterialized();
                        break;
                    case OpCode.AdmitScope:
                        if (stack.PeekReference(0) is not ScopeValue selectedScope)
                            throw new MsyncException("The selected value is not a Scope with an admitted layout.");
                        Places(image, ref boundPlaces).Admit(selectedScope, instruction.A);
                        break;
                    case OpCode.LoadStaticField:
                        {
                            var field = staticScopes.Read(instruction.A, instruction.B, out double staticValue);
                            if (field is null) stack.AddNumber(staticValue);
                            else stack.Add(field);
                            goto MemberTail;
                        }
                    case OpCode.LoadField:
                        {
                            var owner = (ScopeValue)stack.Pop();
                            LoadFieldPlace(ref stack, owner, Places(image, ref boundPlaces).Field(owner, instruction.A));
                            goto MemberTail;
                        }
                    case ExecutionTargets.LoadLocalField:
                        {
                            var receiver = slots.Peek<TLayout>(instruction.A);
                            var owner = (ScopeValue)(receiver is CellValue cell ? cell.Data : receiver!);
                            cancellationToken.ThrowIfCancellationRequested();
                            pc++;
                            LoadFieldPlace(ref stack, owner, Places(image, ref boundPlaces).Field(owner, instruction.B));
                            goto MemberTail;
                        }
                    MemberTail:
                        if (effectTails is not null)
                            RunEffectTail(effectTails, image, functionIndex, code, ref slots, ref numericLoops,
                                ref stack, ref frame, ref pc, cancellationToken);
                        break;
                    case OpCode.Qualify:
                        {
                            var item = stack.Pop(); var scope = stack.Pop();
                            if (scope is ConnectionValue connection)
                            {
                                stack.Add(MapValue(item, TypeRef.Path, value => Qualify(connection, value), connection.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl ? connection.ToString() : "local"));
                                break;
                            }
                            var pending = frame.Observe(ResolveAsync(item), pc, instruction, "qualified_member");
                            if (!pending.IsCompleted)
                            {
                                var pause = Pause(ref state, ref frame, DispatchResume.QualifiedMember);
                                pause.Value = pending;
                                pause.Receiver = scope;
                                goto Suspended;
                            }
                            stack.Add(Member(scope, Text(pending.GetAwaiter().GetResult())));
                            break;
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
                            var selected = frame.Observe(admission, pc, instruction, "transfer_admission");
                            if (!selected.IsCompleted)
                            {
                                Pause(ref state, ref frame, discarded ? DispatchResume.DiscardedTransfer : DispatchResume.TransferAdmission).Admission = selected;
                                goto Suspended;
                            }
                            var completion = selected.GetAwaiter().GetResult();
                            if (discarded) { pc++; break; }
                            var pending = frame.Observe(completion, pc, instruction, "transfer_completion");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Boolean).Boolean = new(pending);
                                goto Suspended;
                            }
                            stack.Add(pending.GetAwaiter().GetResult() ? Value.True : Value.False);
                            break;
                        }
                    case OpCode.ExecutePlan:
                        for (int item = 0; item < instruction.B; item++) stack.Discard();
                        (submissions ??= [])[instruction.A] = SubmitPlan(image.ExecutionPlans![instruction.A], cancellationToken);
                        break;
                    case OpCode.PlanResult:
                        {
                            for (int item = 0; item < instruction.B; item++) stack.Discard();
                            var pending = frame.Observe(ReadPlanResultAsync(image.ExecutionPlans![instruction.A], submissions![instruction.A]),
                                pc, instruction, "plan_result");
                            if (!pending.IsCompleted)
                            {
                                Pause(ref state, ref frame, DispatchResume.Boolean).Boolean = pending;
                                goto Suspended;
                            }
                            stack.Add(pending.GetAwaiter().GetResult() ? Value.True : Value.False);
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
                    case ExecutionTargets.ReadonlyNumberFoldEntry:
                        if (TryReadonlyNumberFold<TFrame, TLayout>(image, functionIndex, pc, (Instruction[])code,
                            ref slots, ref stack, ref frame, cancellationToken, out int numberNext))
                        {
                            pc = numberNext - 1;
                            break;
                        }
                        frame.NumberFoldMiss();
                        instruction = code[pc];
                        goto case OpCode.GetIterator;
                    case ExecutionTargets.ReadonlyScopeFoldEntry:
                        if (TryReadonlyScopeFold<TFrame, TLayout>(image, functionIndex, pc, ref slots, ref stack, ref frame,
                            cancellationToken, out int foldNext))
                        {
                            pc = foldNext - 1;
                            break;
                        }
                        instruction = code[pc];
                        goto case OpCode.GetIterator;
                    case OpCode.GetIterator:
                        {
                            var range = stack.Pop() as RangeValue ?? throw new MsyncException("Iteration requires Range");
                            stack.Add(OpenIterator(range, cancellationToken));
                            break;
                        }
                    case OpCode.IteratorNext:
                        {
                            var cursor = (IteratorValue)slots.Storage<TFrame, TLayout>(instruction.A, ref frame);
                            var iterator = cursor.Iterator;
                            if (iterator is null)
                            {
                                if (cursor.Pending is { } deferred)
                                {
                                    var source = frame.Observe(DeferredRangeSource.Demand(deferred, cancellationToken), pc, instruction, "iterator_source");
                                    if (!source.IsCompleted)
                                    {
                                        var pause = Pause(ref state, ref frame, DispatchResume.IteratorSource);
                                        pause.RangeSource = source;
                                        pause.Cursor = cursor;
                                        goto Suspended;
                                    }
                                    BindIterator(cursor, source.GetAwaiter().GetResult(), cancellationToken);
                                    iterator = cursor.Iterator;
                                }
                                if (iterator is null)
                                {
                                    if (cursor.Numbers.TryNext(out double itemNumber)) stack.AddNumber(itemNumber);
                                    else pc = instruction.B - 1;
                                    break;
                                }
                            }
                            var pending = frame.Observe(iterator.MoveNextAsync(), pc, instruction, "iterator_next");
                            if (!pending.IsCompleted)
                            {
                                var pause = Pause(ref state, ref frame, DispatchResume.IteratorNext);
                                pause.Boolean = pending;
                                pause.Iterator = iterator;
                                goto Suspended;
                            }
                            if (pending.GetAwaiter().GetResult()) { stack.Add(iterator.Current); break; }
                            var dispose = frame.Observe(iterator.DisposeAsync(), pc, instruction, "iterator_dispose");
                            if (!dispose.IsCompleted)
                            {
                                var pause = Pause(ref state, ref frame, DispatchResume.IteratorDispose);
                                pause.Completion = dispose;
                                pause.Iterator = iterator;
                                goto Suspended;
                            }
                            dispose.GetAwaiter().GetResult();
                            iterators.Remove(iterator);
                            pc = instruction.B - 1;
                            break;
                        }
                    case OpCode.Add:
                    case OpCode.Subtract:
                    case OpCode.Multiply:
                    case OpCode.Divide:
                    case OpCode.Remainder:
                        {
                            if (stack.TryPopNumbers(out double leftNumber, out double rightNumber))
                                stack.AddNumber(NumberArithmetic.Apply(leftNumber, rightNumber, instruction.Op));
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
            catch (Exception error) when (IsDispatchError(error))
            {
                var source = instruction.Op is ExecutionTargets.LoadLocalField or ExecutionTargets.ScopeComparison
                    ? code[pc] : instruction;
                throw DispatchError(error, source);
            }
        }
        return stack.Count == 0 ? Value.Null : stack.Pop();

    Suspended:
        state!.Pc = pc;
        state.Instruction = instruction;
        state.Frame = frame;
        state.Slots = slots;
        state.Stack = stack;
        state.Array = array;
        state.Submissions = submissions;
        state.NumericBackedges = numericBackedges;
        state.EffectTails = effectTails;
        state.NumericLoops = numericLoops;
        return null;
    }

    private ValueTask<Value> InvokeStackValueAsync<TFrame>(Value callee, Instruction instruction, ref OperandStack<TFrame> stack,
        CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (instruction.Op == OpCode.Call && instruction.B > 0)
            throw new MsyncException("A direct specialization requires a source Function.", instruction.Span);
        if (instruction.Op == OpCode.Call && instruction.B < 0 && callee is not FunctionValue)
            throw new MsyncException("An open session call requires a Function value.", instruction.Span);
        var arguments = stack.PopArguments(instruction.A);
        stack.Discard();
        return InvokeValueAsync(callee, arguments, cancellationToken, scheduling: instruction.B, origin: instruction.Span);
    }

    private int SourceCallTarget(FunctionValue function, Instruction instruction)
    {
        if (instruction.Op == OpCode.Call)
        {
            int target;
            if (instruction.B < 0) target = SessionDispatchTarget(function, -instruction.B - 1, instruction.Span);
            else
            {
                RefreshSessionImage();
                target = instruction.Text is not null ? DispatchTarget(function, instruction)
                    : instruction.B > 0 ? instruction.B - 1 : function.Function;
            }
            if (target != function.Function)
            {
                RequireCaptureContract(function, target, instruction.Span);
                return target;
            }
            if (instruction.B != 0 || instruction.Text is not null) return target;
        }
        else RefreshSessionImage();
        RequireArity(module.Functions[function.Function], instruction.A);
        return function.Function;
    }
}
