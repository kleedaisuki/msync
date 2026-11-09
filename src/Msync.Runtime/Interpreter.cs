using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Module module;
    private readonly RuntimeContext context;
    private BasicBlock[][] blocks;
    private readonly SemaphoreSlim slots;
    private readonly HashSet<IAsyncDisposable> iterators;
    private readonly Dictionary<string, IReadOnlyDictionary<int, int>> dispatchTables = [];
    private int failedTransfers;
    private int callDepth;
    public int FailedTransfers => failedTransfers;
    private sealed record IteratorValue(IAsyncEnumerator<Value> Iterator) : Value { public override TypeRef Type => TypeRef.Any; }

    public Interpreter(Module module, ExecutionOptions? options = null) : this(module, options, null) { }
    internal Interpreter(Module module, ExecutionOptions? options, Interpreter? previous)
    {
        resources = previous?.resources ?? new();
        pending = previous?.pending ?? [];
        sessionProcesses = previous?.sessionProcesses ?? new(ReferenceEqualityComparer.Instance);
        processDependencies = previous?.processDependencies ?? [];
        namespaceProducers = previous?.namespaceProducers ?? [];
        iterators = previous?.iterators ?? [];
        context = new(options, module.Context);
        (this.module, blocks) = ProjectImage(module);
        slots = previous?.slots ?? new(Math.Max(1, Math.Min(context.Jobs, (int)Math.Min(int.MaxValue, context.MemoryLimit / Math.Max(1, context.BufferSize)))));
    }
    public static ValueTask<Value> ExecuteAsync(Module module, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => new Interpreter(module, options).ExecuteAsync(cancellationToken);
    public async ValueTask<Value> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try { return await ResolveResultAsync(await RunFunctionAsync(module.EntryPoint, [], [], cancellationToken)); }
        finally
        {
            await DrainAsync();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
        }
    }
    internal async ValueTask<Value> ExecuteEntryAsync(Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        try { return await ResolveAsync(await RunCodeAsync(module.EntryPoint, locals, startInstruction, cancellationToken)); }
        finally
        {
            await JoinTransfersAsync(includeProcessFlows: false);
            RetireSessionWork();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
            iterators.Clear();
        }
    }
    private async ValueTask<Value> InvokeAsync(Value callee, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        callee = ReadValue(callee);
        arguments = ValueArguments(arguments);
        switch (callee)
        {
            case FunctionValue { NativeName: not null } native:
                return await InvokeNativeAsync(native.NativeName, arguments, cancellationToken);
            case FunctionValue function:
                return await RunFunctionAsync(function.Function, function.Captures ?? [], arguments, cancellationToken);
            case PathValue executable:
                return await RunProcessAsync(executable, arguments, cancellationToken);
            case StringValue executable:
                return await RunProcessAsync(context.AsPath(executable), arguments, cancellationToken);
            default: throw new MsyncException($"{callee.Type} is not callable");
        }
    }
    private ValueTask<Value> RunFunctionAsync(int functionIndex, IReadOnlyList<Value> captures, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        RefreshSessionImage();
        var function = module.Functions[functionIndex];
        if (arguments.Count != function.ParameterCount) throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {arguments.Count}");
        var locals = new Value[function.LocalCount];
        Array.Fill(locals, Value.Null);
        for (var i = 0; i < captures.Count; i++) locals[i] = captures[i];
        for (var i = 0; i < arguments.Count; i++) locals[function.CaptureCount + i] = ReadValue(arguments[i]);
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken, captures, arguments);
    }
    private async ValueTask<Value> RunCodeAsync(int functionIndex, Value[] locals, int startInstruction,
        CancellationToken cancellationToken, IReadOnlyList<Value>? captures = null, IReadOnlyList<Value>? arguments = null)
    {
        var active = sessionImage?.ActiveInterpreter;
        var outer = active is not null && !ReferenceEquals(active, this) ? active : null;
        var previous = currentOwner;
        var caller = outer?.currentOwner ?? previous;
        bool hasCaller = outer is not null || callDepth != 0;
        callDepth++;
        if (sessionImage is { } session) session.ActiveInterpreter = this;
        currentOwner = default;
        Value? result = null;
        try
        {
            result = await RunOwnedCodeAsync(functionIndex, locals, startInstruction, cancellationToken);
            return result;
        }
        finally
        {
            try
            {
                var roots = sessionImage is not null && functionIndex == module.EntryPoint ? module.SessionRoots : null;
                PromoteReturnedProcesses(result, roots, locals, captures, arguments, ref caller, hasCaller);
                await JoinOwnerAsync(currentOwner);
            }
            finally
            {
                currentOwner = outer is null ? caller : previous;
                if (outer is not null) outer.currentOwner = caller;
                callDepth--;
                if (sessionImage is { } image) image.ActiveInterpreter = active;
            }
        }
    }
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
                            if (instruction.Op == OpCode.Call && instruction.Text is not null && callee is FunctionValue { NativeName: null } dynamicFunction)
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
    private FunctionValue Specialize(FunctionValue function, Instruction instruction)
    {
        if (function.NativeName is not null) return function;
        var target = instruction.Text is null ? instruction.A : DispatchTarget(function, instruction);
        return target == function.Function ? function : function with { Function = target };
    }
    private int DispatchTarget(FunctionValue function, Instruction instruction)
    {
        if (!dispatchTables.TryGetValue(instruction.Text!, out var targets))
        {
            targets = FunctionDispatch.Parse(instruction.Text!);
            dispatchTables.Add(instruction.Text!, targets);
        }
        return targets.TryGetValue(function.Function, out var target) ? target
            : throw new MsyncException($"Function #{function.Function} has no compiled specialization in this dispatch table", instruction.Span);
    }
    private Value Capture(Value value) => value is PathValue path && (path.WorkingDirectory is null || path.HomeDirectory is null)
        ? path with { WorkingDirectory = path.WorkingDirectory ?? context.WorkingDirectory, HomeDirectory = path.HomeDirectory ?? context.HomeDirectory } : value;
    private Value Builtin(string name)
    {
        name = name.TrimStart('$');
        return name switch
        {
            "local" => new ConnectionValue(ConnectionKind.Local),
            "pwd" or "file" => new ConnectionValue(ConnectionKind.Local, name),
            "wsl" => new ConnectionValue(ConnectionKind.Wsl),
            "env" => new ConnectionValue(ConnectionKind.Environment),
            "path" => new ConnectionValue(ConnectionKind.SearchPath),
            "std" => new ScopeValue(new[] { "fs", "path", "bool", "string", "ranges", "process" }.ToDictionary(n => n, n => NativeScope(n))),
            _ => throw new MsyncException($"Unknown builtin '${name}'")
        };
    }
    private static Value NativeScope(string name) => new ScopeValue(NativeSignatures.All.Where(s => s.Name.StartsWith(name + ".", StringComparison.Ordinal)).ToDictionary(s => s.Name[(name.Length + 1)..], s => (Value)new FunctionValue(-1, NativeName: s.Name)));
    private Value Member(Value value, string name, int hint = 0) => value switch
    {
        ScopeValue scope => ScopeMember(scope, name, hint),
        ProcessValue process => ProcessMember(process, name),
        ConnectionValue connection => Qualify(connection, new PathValue(name)),
        _ => throw new MsyncException($"{value.Type} has no member '{name}'")
    };
    private PathValue Qualify(ConnectionValue connection, Value item)
    {
        var path = context.AsPath(item);
        if (connection.Kind == ConnectionKind.Environment)
            return context.AsPath(new StringValue(Environment.GetEnvironmentVariable(path.Raw) ?? throw new MsyncException($"Environment variable '{path.Raw}' is not set")));
        if (connection.Kind == ConnectionKind.SearchPath)
        {
            var executable = FindExecutable(path.Raw);
            return new(executable, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
        }
        if (connection.Kind == ConnectionKind.Local && connection.Address == "pwd" && path.Raw == "pwd") return new(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
        if (connection.Kind == ConnectionKind.Local && connection.Address == "file")
        {
            var source = context.SourceFile ?? throw new MsyncException("$file requires a named source file");
            path = path.WithRaw(path.Raw switch { "this" => source, "parent" => Path.GetDirectoryName(source)!, _ => Path.Combine(Path.GetDirectoryName(source)!, path.NativeText) });
        }
        var qualified = PathSemantics.Qualify(path, connection);
        return connection.Kind == ConnectionKind.Local ? qualified with { WorkingDirectory = qualified.WorkingDirectory ?? context.WorkingDirectory, HomeDirectory = qualified.HomeDirectory ?? context.HomeDirectory } : qualified;
    }
    private string FindExecutable(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) return Path.GetFullPath(name, context.WorkingDirectory);
        var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var extension in extensions)
            { var candidate = Path.Combine(Path.IsPathRooted(directory) ? directory : Path.Combine(context.WorkingDirectory, directory), name + extension); if (File.Exists(candidate)) return candidate; }
        throw new MsyncException($"Executable '{name}' was not found on PATH");
    }
    private async IAsyncEnumerable<Value> LazyItemsAsync(IReadOnlyList<Value> values, string? mask, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < values.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return mask is null || mask[i] == '1' ? await InvokeAsync(values[i], [], cancellationToken) : values[i];
        }
    }
    private RangeValue CreateDeferredRange(Value thunk)
    {
        var itemType = thunk is FunctionValue { NativeName: null } function
            ? module.Functions[function.Function].ReturnType.Element ?? TypeRef.Any : TypeRef.Any;
        Task<RangeValue>? resolved = null;
        async Task<RangeValue> Evaluate(CancellationToken cancellationToken) => await InvokeAsync(thunk, [], cancellationToken) as RangeValue
            ?? throw new MsyncException("Deferred range function did not return Range");
        ValueTask<RangeValue> Resolve(CancellationToken cancellationToken) => new(resolved ??= Evaluate(cancellationToken));
        return new RangeValue(EnumerateDeferredRangeAsync(Resolve), itemType, Resolve: Resolve)
        { RetainedValues = (thunk as FunctionValue)?.Captures };
    }
    private static async IAsyncEnumerable<Value> EnumerateDeferredRangeAsync(Func<CancellationToken, ValueTask<RangeValue>> resolve,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var range = await resolve(cancellationToken);
        await foreach (var value in range.Items.WithCancellation(cancellationToken)) yield return value;
    }
    private sealed class SpoolBudget(long? limit)
    {
        private long used;
        public void Account(int count)
        {
            var total = Interlocked.Add(ref used, count);
            if (limit is { } maximum && total > maximum) throw new IOException($"Process output exceeded configured spool limit of {maximum} bytes (observed {total})");
        }
    }
    private static async Task<string> ReadProcessStreamAsync(Stream stream, SpoolBudget budget, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        Exception? failure = null;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (failure is not null) continue;
            try { budget.Account(count); await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
            catch (IOException error) { failure = error; }
        }
        if (failure is not null) throw failure;
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
    private async ValueTask<Value> InvokeNativeAsync(string name, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (name == "_core.iterator_close") return await CloseIteratorAsync(arguments[0]);
            if (name is "io.read_lines" or "io.read_bytes") return ReadIORange(name, arguments, cancellationToken);
            if (name == "process.spawn") return await SpawnProcessAsync(arguments, cancellationToken);
            if (name is "process.stdout" or "process.stderr" or "process.success" or "process.exit_code" && arguments[0] is ProcessValue process)
                return ProcessMember(process, name[8..]);
            if (arguments.Any(value => value is DeferredValue)
                && (EffectAnalysis.NativeEffects(name) & (InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke)) == 0
                && NativeSignatures.TryGet(name, out var signature)
                && !arguments.Any(value => (value.Type.Kind & (TypeKind.Range | TypeKind.Function)) != 0))
            {
                var inputs = arguments;
                RuntimeContext origin = context.CaptureDirectory();
                async Task<Value> Complete() => await NativeRegistry.InvokeAsync(name, await ResolveArgumentsAsync(inputs), origin, NativeCallback, cancellationToken);
                TypeRef resultType = signature.ResultRule == NativeResultRule.CastTarget && inputs[1] is TypeValue target
                    ? target.Data : signature.Result;
                string? endpoint = resultType.WithoutNull == TypeRef.Path
                    ? inputs[0].Type == TypeRef.String ? "local" : PathEndpoint(inputs[0]) : null;
                return new DeferredValue(Complete(), resultType, inputs.SelectMany(Processes).Distinct().ToArray(), endpoint);
            }
            arguments = await ResolveArgumentsAsync(arguments);
            await AwaitNativeDependenciesAsync(name, arguments);
            return await NativeRegistry.InvokeAsync(name, arguments, name == "cast" ? context.CaptureDirectory() : context, NativeCallback, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Observe?.Invoke(new("native", "failure", Detail: $"{name}: {error}"));
            throw;
        }
    }
    private async IAsyncEnumerable<Value> SelectAsync(PathValue directory, string pattern, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await AwaitNativeDependenciesAsync("fs.directory_iterator", [directory]);
        var values = await NativeRegistry.InvokeAsync("fs.directory_iterator", [directory], context, NativeCallback, cancellationToken) as RangeValue ?? throw new MsyncException("Directory iterator did not return a Range");
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        await foreach (var item in values.Items.WithCancellation(cancellationToken))
            if (regex.IsMatch(item is PathValue itemPath ? PathSemantics.FileName(itemPath) : Path.GetFileName(Text(item)))) yield return item;
    }
    private async Task<bool> TransferAsync(Value source, Value destination, SourceSpan? span, bool local, RuntimeContext context, CancellationToken cancellationToken)
    {
        var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            var result = local ? await TransferEngine.CopyLocalAsync(source, destination, context, cancellationToken) : await TransferEngine.CopyAsync(source, destination, context, cancellationToken);
            context.Observe?.Invoke(new("transfer", result ? "success" : "failure", SourceLabel(source), destination.ToString(), DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Interlocked.Increment(ref failedTransfers);
            context.Observe?.Invoke(new("transfer", "failure", SourceLabel(source), destination.ToString(), error.ToString(), (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            if (context.ReportError is { } report)
                report(new MsyncException($"Cannot transfer {SourceLabel(source)} -> {destination}.", span, error));
            else
            {
                var location = span is { } origin ? $"{origin.Source}:{origin.Start}: " : "";
                await context.Error.WriteLineAsync($"{location}transfer {SourceLabel(source)} -> {destination}: {error.Message}");
                for (var cause = error.InnerException; cause is not null; cause = cause.InnerException)
                    await context.Error.WriteLineAsync($"  caused by {cause.Message}");
            }
            return false;
        }
    }
    private static string SourceLabel(Value value) => value is StringValue text ? $"literal ({Encoding.UTF8.GetByteCount(text.Data)} UTF-8 bytes)" : value.ToString()!;
    private static bool AsBool(Value value) => value is BoolValue boolean ? boolean.Data : throw new MsyncException($"Condition requires Bool, received {value.Type}");
    private static string Text(Value value) => value switch { StringValue s => s.Data, PathValue p => p.NativeText, NumberValue n => n.ToString(), BoolValue b => b.ToString(), _ => throw new MsyncException($"Cannot convert {value.Type} to String") };
    private static int? Compare(Value left, Value right) => (left, right) switch
    {
        (NumberValue l, NumberValue r) => NumberSemantics.Compare(l.Data, r.Data),
        (BoolValue l, BoolValue r) => l.Data.CompareTo(r.Data),
        (TypeValue l, TypeValue r) => l.Data == r.Data ? 0 : 1,
        (StringValue or PathValue, StringValue or PathValue) => TextSemantics.CompareOrdinal(Text(left), Text(right)),
        _ => throw new MsyncException($"Cannot compare {left.Type} and {right.Type}; null supports only 'is' and 'is not'")
    };
}

public sealed class ExecutionSession(ExecutionOptions? options = null) : IAsyncDisposable
{
    private sealed record UninitializedValue : Value { public override TypeRef Type => new(TypeKind.Never); }
    private static readonly Value Uninitialized = new UninitializedValue();
    private Value[] locals = [];
    private SessionImage? image;
    private Interpreter? interpreter;
    private string workingDirectory = Path.GetFullPath(options?.WorkingDirectory ?? Environment.CurrentDirectory);
    public string WorkingDirectory => workingDirectory;
    public void ChangeWorkingDirectory(string path)
    {
        RuntimeContext context = new((options ?? new()) with { WorkingDirectory = workingDirectory,
            WorkingDirectoryChanged = AcceptWorkingDirectory });
        context.ChangeWorkingDirectory(new StringValue(path));
    }
    private void AcceptWorkingDirectory(string directory)
    {
        workingDirectory = directory;
        options?.WorkingDirectoryChanged?.Invoke(directory);
    }
    private Value ReadSessionLocal(int slot) => locals[slot] is CellValue cell ? cell.Data : locals[slot];
    public bool IsLocalInitialized(int slot) => (uint)slot < (uint)locals.Length && !ReferenceEquals(ReadSessionLocal(slot), Uninitialized);
    public Value? GetLocalValue(int slot) => IsLocalInitialized(slot) ? ReadSessionLocal(slot) : null;
    public ValueTask<Value> ExecuteAsync(Module module, int startInstruction = 0, CancellationToken cancellationToken = default)
    {
        var count = module.Functions[module.EntryPoint].LocalCount;
        var previousLength = locals.Length; Array.Resize(ref locals, count);
        for (var i = previousLength; i < count; i++) locals[i] = Uninitialized;
        module = module with { PreserveLocals = true };
        if (image is null) image = new(module);
        else image.Publish(module);
        interpreter = new Interpreter(module, (options ?? new()) with
        { WorkingDirectory = workingDirectory, WorkingDirectoryChanged = AcceptWorkingDirectory }, interpreter);
        interpreter.Attach(image);
        return interpreter.ExecuteEntryAsync(locals, startInstruction, cancellationToken);
    }
    public ValueTask DisposeAsync() => interpreter?.CloseSessionAsync() ?? ValueTask.CompletedTask;
}
