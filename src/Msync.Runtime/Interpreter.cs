using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public sealed class Interpreter
{
    private readonly Module module;
    private readonly RuntimeContext context;
    private readonly BasicBlock[][] blocks;
    private readonly List<PendingTransfer> transfers = [];
    private readonly SemaphoreSlim slots;
    private readonly HashSet<IAsyncDisposable> iterators = [];
    private readonly Dictionary<string, IReadOnlyDictionary<int, int>> dispatchTables = [];
    private int failedTransfers;
    public int FailedTransfers => failedTransfers;
    private sealed record ResourceKey(string Endpoint, string Path);
    private sealed record PendingTransfer(Task<bool> Task, ResourceKey? Source, ResourceKey Destination);
    private sealed record IteratorValue(IAsyncEnumerator<Value> Iterator) : Value { public override TypeRef Type => TypeRef.Any; }

    public Interpreter(Module module, ExecutionOptions? options = null)
    {
        context = new(options, module.Context);
        this.module = module.Stage == ArtifactStage.Relocatable
            ? Binding.Bind(module, new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile)) : module;
        if (this.module.Blocks is null) this.module = Optimizer.FormBlocks(this.module);
        blocks = this.module.Functions.Select(function => new BasicBlock[function.Instructions.Count]).ToArray();
        foreach (var block in this.module.Blocks!)
            for (var pc = block.Start; pc < block.End; pc++) blocks[block.Function][pc] = block;
        slots = new(Math.Max(1, Math.Min(context.Jobs, (int)Math.Min(int.MaxValue, context.MemoryLimit / Math.Max(1, context.BufferSize)))));
    }
    public static ValueTask<Value> ExecuteAsync(Module module, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => new Interpreter(module, options).ExecuteAsync(cancellationToken);
    public async ValueTask<Value> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try { return await RunFunctionAsync(module.EntryPoint, [], [], cancellationToken); }
        finally
        {
            await DrainAsync();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
        }
    }
    internal async ValueTask<Value> ExecuteEntryAsync(Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        try { return await RunCodeAsync(module.EntryPoint, locals, startInstruction, cancellationToken); }
        finally { await DrainAsync(); foreach (var iterator in iterators) await iterator.DisposeAsync(); }
    }
    private async ValueTask<Value> InvokeAsync(Value callee, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
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
        var function = module.Functions[functionIndex];
        if (arguments.Count != function.ParameterCount) throw new MsyncException($"Function '{function.Name}' expects {function.ParameterCount} arguments, received {arguments.Count}");
        var locals = new Value[function.LocalCount];
        Array.Fill(locals, Value.Null);
        for (var i = 0; i < captures.Count; i++) locals[i] = captures[i];
        for (var i = 0; i < arguments.Count; i++) locals[function.CaptureCount + i] = arguments[i];
        return RunCodeAsync(functionIndex, locals, 0, cancellationToken);
    }
    private async ValueTask<Value> RunCodeAsync(int functionIndex, Value[] locals, int startInstruction, CancellationToken cancellationToken)
    {
        var function = module.Functions[functionIndex];
        List<Value> stack = [];
        var pc = startInstruction;
        while (pc < function.Instructions.Count)
        {
            var block = blocks[functionIndex][pc];
            var outcome = await RunBlockAsync(function, locals, stack, pc, block, cancellationToken);
            if (outcome.Returned) return outcome.Value!;
            pc = outcome.Next;
        }
        await DrainAsync();
        return stack.Count == 0 ? Value.Null : stack[^1];
    }
    private readonly record struct BlockOutcome(int Next, Value? Value = null, bool Returned = false);
    private static Instruction SpecializeBlockInstruction(Instruction instruction, List<Value> stack)
    {
        if (instruction.Op != OpCode.Transfer || stack.Count < 2) return instruction;
        static bool Local(Value value) => value is StringValue or PathValue { Connection: null or { Kind: ConnectionKind.Local } };
        return Local(stack[^1]) && Local(stack[^2]) ? instruction with { Op = OpCode.LocalTransfer } : instruction;
    }
    private async ValueTask<BlockOutcome> RunBlockAsync(FunctionCode function, Value[] locals, List<Value> stack, int startInstruction, BasicBlock block, CancellationToken cancellationToken)
    {
        Value Pop() { var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
        Value[] PopMany(int count)
        {
            var values = new Value[count];
            for (var i = count - 1; i >= 0; i--) values[i] = Pop();
            return values;
        }
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
                    case OpCode.Constant: stack.Add(Capture(module.Constants[instruction.A])); break;
                    case OpCode.Null: stack.Add(Value.Null); break;
                    case OpCode.True: stack.Add(Value.True); break;
                    case OpCode.False: stack.Add(Value.False); break;
                    case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                    case OpCode.StoreLocal: locals[instruction.A] = Pop(); break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: stack.Add(stack[^1]); break;
                    case OpCode.Jump:
                        await DrainAsync(); pc = instruction.A - 1; break;
                    case OpCode.JumpIfFalse:
                    case OpCode.JumpIfTrue:
                        await DrainAsync();
                        if (AsBool(Pop()) == (instruction.Op == OpCode.JumpIfTrue)) pc = instruction.A - 1;
                        break;
                    case OpCode.MakeClosure: stack.Add(new FunctionValue(instruction.A, PopMany(instruction.B))); break;
                    case OpCode.SpecializeClosure:
                        stack.Add(Specialize((FunctionValue)Pop(), instruction)); break;
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        {
                            await DrainAsync();
                            var arguments = PopMany(instruction.A); var callee = Pop();
                            if (instruction.Op == OpCode.Call && instruction.Text is not null && callee is FunctionValue { NativeName: null } dynamicFunction) callee = Specialize(dynamicFunction, instruction);
                            else if (instruction.Op == OpCode.Call && instruction.B != 0) callee = ((FunctionValue)callee) with { Function = instruction.B - 1 };
                            stack.Add(await InvokeAsync(callee, arguments, cancellationToken)); break;
                        }
                    case OpCode.NativeCall:
                        if (RequiresFrontierJoin(instruction.Text!)) await DrainAsync();
                        stack.Add(await InvokeNativeAsync(instruction.Text!, PopMany(instruction.A), cancellationToken)); break;
                    case OpCode.Return: await DrainAsync(); return new(pc, stack.Count == 0 ? Value.Null : Pop(), true);
                    case OpCode.LoadBuiltin: stack.Add(Builtin(instruction.Text!)); break;
                    case OpCode.Convert:
                        {
                            var value = Pop();
                            stack.Add(instruction.Text == "Path" ? context.AsPath(value) : instruction.Text == "String" ? new StringValue(Text(value)) : throw new MsyncException($"Unknown conversion {instruction.Text}")); break;
                        }
                    case OpCode.MakeRange:
                        {
                            var values = PopMany(instruction.A);
                            stack.Add(new RangeValue(values, values.Select(v => v.Type).Aggregate(new TypeRef(TypeKind.Never), TypeRef.Union))); break;
                        }
                    case OpCode.MakeLazyRange:
                        {
                            var values = PopMany(instruction.A);
                            var type = instruction.B > 0 && module.Constants[instruction.B - 1] is TypeValue typed ? typed.Data : TypeRef.Any;
                            stack.Add(new RangeValue(LazyItemsAsync(values, instruction.Text, cancellationToken), type)); break;
                        }
                    case OpCode.DeferRange:
                        stack.Add(CreateDeferredRange(Pop())); break;
                    case OpCode.MakeScope:
                        {
                            var values = PopMany(instruction.A); var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                            stack.Add(new ScopeValue(names.Select((name, i) => KeyValuePair.Create(name, values[i])).ToDictionary())); break;
                        }
                    case OpCode.GetMember: stack.Add(Member(Pop(), instruction.Text!)); break;
                    case OpCode.Qualify:
                        {
                            var item = Pop(); var scope = Pop();
                            stack.Add(scope is ConnectionValue connection ? Qualify(connection, item) : Member(scope, Text(item))); break;
                        }
                    case OpCode.PathJoin:
                        {
                            var right = Text(Pop()); var left = context.AsPath(Pop());
                            stack.Add(left with { Raw = PathSemantics.Join(left, right) }); break;
                        }
                    case OpCode.SelectPath:
                        {
                            var directory = context.AsPath(Pop());
                            stack.Add(new RangeValue(SelectAsync(directory, instruction.Text!, cancellationToken), TypeRef.Path, new PathSelection(directory, instruction.Text!))); break;
                        }
                    case OpCode.Transfer:
                    case OpCode.LocalTransfer:
                        {
                            var destination = Pop(); var source = Pop();
                            if (pc + 1 < code.Count && code[pc + 1].Op == OpCode.Pop && source is PathValue or StringValue && destination is PathValue or StringValue)
                            { QueueTransfer(source, destination, instruction.Span, instruction.Op == OpCode.LocalTransfer, cancellationToken); pc++; }
                            else { await DrainAsync(); stack.Add(await TransferAsync(source, destination, instruction.Span, instruction.Op == OpCode.LocalTransfer, cancellationToken) ? Value.True : Value.False); }
                            break;
                        }
                    case OpCode.Not: stack.Add(AsBool(Pop()) ? Value.False : Value.True); break;
                    case OpCode.IsNull: stack.Add(Pop() is NullValue ? Value.True : Value.False); break;
                    case OpCode.IsType:
                        {
                            var expected = Pop() as TypeValue ?? throw new MsyncException("Type identity requires a type on the right");
                            var value = Pop(); var type = value is TypeValue typeValue ? typeValue.Data : value.Type;
                            stack.Add(expected.Data.Accepts(type) ? Value.True : Value.False); break;
                        }
                    case OpCode.RegexMatch:
                        {
                            var pattern = instruction.Text ?? Text(Pop()); var value = Pop();
                            stack.Add(value is PathValue or StringValue && Regex.IsMatch(Text(value), pattern, RegexOptions.CultureInvariant) ? Value.True : Value.False); break;
                        }
                    case OpCode.Equal:
                    case OpCode.NotEqual:
                    case OpCode.Less:
                    case OpCode.LessEqual:
                    case OpCode.Greater:
                    case OpCode.GreaterEqual:
                        {
                            var right = Pop(); var left = Pop();
                            var comparison = Compare(left, right);
                            var result = instruction.Op switch { OpCode.Equal => comparison == 0, OpCode.NotEqual => comparison != 0, OpCode.Less => comparison < 0, OpCode.LessEqual => comparison <= 0, OpCode.Greater => comparison > 0, _ => comparison >= 0 };
                            stack.Add(result ? Value.True : Value.False); break;
                        }
                    case OpCode.GetIterator:
                        {
                            var range = Pop() as RangeValue ?? throw new MsyncException("Iteration requires Range");
                            var iterator = range.Items.GetAsyncEnumerator(cancellationToken); iterators.Add(iterator); stack.Add(new IteratorValue(iterator)); break;
                        }
                    case OpCode.IteratorNext:
                        {
                            await DrainAsync();
                            var iterator = ((IteratorValue)locals[instruction.A]).Iterator;
                            if (await iterator.MoveNextAsync()) stack.Add(iterator.Current);
                            else { await iterator.DisposeAsync(); iterators.Remove(iterator); pc = instruction.B - 1; }
                            break;
                        }
                    case OpCode.Add:
                    case OpCode.Subtract:
                    case OpCode.Multiply:
                    case OpCode.Divide:
                        {
                            var right = Pop(); var left = Pop();
                            if (instruction.Op == OpCode.Add && left is StringValue a && right is StringValue b) { stack.Add(new StringValue(a.Data + b.Data)); break; }
                            var l = ((NumberValue)left).Data; var r = ((NumberValue)right).Data;
                            stack.Add(new NumberValue(instruction.Op switch { OpCode.Add => l + r, OpCode.Subtract => l - r, OpCode.Multiply => l * r, _ => l / r })); break;
                        }
                    case OpCode.Negate: stack.Add(new NumberValue(-((NumberValue)Pop()).Data)); break;
                    default: throw new MsyncException($"Unsupported bytecode operation {instruction.Op}");
                }
            }
            catch (MsyncException error) when (error.Span is null)
            { throw new MsyncException(error.Message, instruction.Span, error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { throw new MsyncException($"{instruction.Op} in '{function.Name}': {error.Message}", instruction.Span, error); }
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext)
                return new(pc + 1);
        }
        return new(pc);
    }
    private FunctionValue Specialize(FunctionValue function, Instruction instruction)
    {
        if (function.NativeName is not null) return function;
        if (instruction.Text is null) return function with { Function = instruction.A };
        if (!dispatchTables.TryGetValue(instruction.Text, out var targets))
        {
            targets = FunctionDispatch.Parse(instruction.Text);
            dispatchTables.Add(instruction.Text, targets);
        }
        return targets.TryGetValue(function.Function, out var target) ? function with { Function = target }
            : throw new MsyncException($"Function #{function.Function} has no compiled specialization in this dispatch table", instruction.Span);
    }
    private Value Capture(Value value) => value is PathValue path ? path with { WorkingDirectory = path.WorkingDirectory ?? context.WorkingDirectory, HomeDirectory = path.HomeDirectory ?? context.HomeDirectory } : value;
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
            "view" or "views" or "ranges" => NativeScope("ranges"),
            _ => throw new MsyncException($"Unknown builtin '${name}'")
        };
    }
    private static Value NativeScope(string name) => new ScopeValue(NativeSignatures.All.Where(s => s.Name.StartsWith(name + ".", StringComparison.Ordinal)).ToDictionary(s => s.Name[(name.Length + 1)..], s => (Value)new FunctionValue(-1, NativeName: s.Name)));
    private Value Member(Value value, string name) => value switch
    {
        ScopeValue scope when scope.Members.TryGetValue(name, out var member) => member,
        ProcessValue process => name switch { "stdout" => new StringValue(process.StandardOutput), "stderr" => new StringValue(process.StandardError), "exit_code" => new NumberValue(process.ExitCode), "success" => process.ExitCode == 0 ? Value.True : Value.False, _ => throw new MsyncException($"Process has no member '{name}'") },
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
            path = path with { Raw = path.Raw switch { "this" => source, "parent" => Path.GetDirectoryName(source)!, _ => Path.Combine(Path.GetDirectoryName(source)!, path.Raw) } };
        }
        return path with { Connection = connection, WorkingDirectory = path.WorkingDirectory ?? context.WorkingDirectory, HomeDirectory = connection.Kind == ConnectionKind.Local ? path.HomeDirectory ?? context.HomeDirectory : null };
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
    private async ValueTask<Value> RunProcessAsync(PathValue executable, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        if (context.DryRun) { await context.Output.WriteLineAsync($"process {executable}({string.Join(", ", arguments)})"); return new ProcessValue(0, "", ""); }
        ProcessStartInfo info;
        if (executable.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
        {
            info = TransferEngine.CreateRemoteProcessStartInfo(remote, executable.Raw, arguments.Select(argument => argument is PathValue path ? path.Raw : Text(argument)).ToArray());
        }
        else
        {
            info = new(executable.LocalPath(context.WorkingDirectory, context.HomeDirectory));
            foreach (var argument in arguments) info.ArgumentList.Add(argument is PathValue path ? path.LocalPath(context.WorkingDirectory, context.HomeDirectory) : Text(argument));
        }
        info.WorkingDirectory = context.WorkingDirectory; info.UseShellExecute = false; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
        using var process = Process.Start(info) ?? throw new MsyncException($"Could not start '{executable}'");
        context.Observe?.Invoke(new("process", "started", executable.ToString(), Detail: $"pid={process.Id}"));
        using var registration = cancellationToken.Register(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); });
        var budget = new SpoolBudget(context.SpoolLimit);
        var stdout = ReadProcessStreamAsync(process.StandardOutput.BaseStream, process, budget, cancellationToken);
        var stderr = ReadProcessStreamAsync(process.StandardError.BaseStream, process, budget, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout; var error = await stderr;
        context.Observe?.Invoke(new("process", process.ExitCode == 0 ? "success" : "failure", executable.ToString(), Detail: $"exit={process.ExitCode}", DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
        return new ProcessValue(process.ExitCode, output, error);
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
        Task<RangeValue>? resolved = null;
        async Task<RangeValue> Evaluate(CancellationToken cancellationToken) => await InvokeAsync(thunk, [], cancellationToken) as RangeValue
            ?? throw new MsyncException("Deferred range function did not return Range");
        ValueTask<RangeValue> Resolve(CancellationToken cancellationToken) => new(resolved ??= Evaluate(cancellationToken));
        return new RangeValue(EnumerateDeferredRangeAsync(Resolve), TypeRef.Any, Resolve: Resolve);
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
    private static async Task<string> ReadProcessStreamAsync(Stream stream, Process process, SpoolBudget budget, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            { budget.Account(count); await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
    private static bool RequiresFrontierJoin(string nativeName) => !NativeSignatures.TryGet(nativeName, out var signature)
        || signature.Effect != NativeEffect.Pure
        || signature.Parameters.Any(parameter => (parameter.Kind & (TypeKind.Range | TypeKind.Function)) != 0);
    private async ValueTask<Value> InvokeNativeAsync(string name, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        try { return await NativeRegistry.InvokeAsync(name, arguments, context, InvokeAsync, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Observe?.Invoke(new("native", "failure", Detail: $"{name}: {error}"));
            throw;
        }
    }
    private async IAsyncEnumerable<Value> SelectAsync(PathValue directory, string pattern, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var values = await NativeRegistry.InvokeAsync("fs.directory_iterator", [directory], context, InvokeAsync, cancellationToken) as RangeValue ?? throw new MsyncException("Directory iterator did not return a Range");
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        await foreach (var item in values.Items.WithCancellation(cancellationToken))
            if (regex.IsMatch(item is PathValue itemPath ? PathSemantics.FileName(itemPath) : Path.GetFileName(Text(item)))) yield return item;
    }
    private async Task<bool> TransferAsync(Value source, Value destination, SourceSpan? span, bool local, CancellationToken cancellationToken)
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
            var location = span is { } origin ? $"{origin.Source}:{origin.Start}: " : "";
            await context.Error.WriteLineAsync($"{location}transfer {SourceLabel(source)} -> {destination}: {error.Message}");
            for (var cause = error.InnerException; cause is not null; cause = cause.InnerException)
                await context.Error.WriteLineAsync($"  caused by {cause.Message}");
            return false;
        }
    }
    private void QueueTransfer(Value source, Value destination, SourceSpan? span, bool local, CancellationToken cancellationToken)
    {
        var target = context.AsPath(destination);
        var sourceKey = source is PathValue pathSource ? Key(pathSource) : null;
        var targetKey = Key(target);
        var dependencies = transfers.Where(previous => Overlaps(previous.Destination, targetKey) || sourceKey is not null && Overlaps(previous.Destination, sourceKey) || previous.Source is not null && Overlaps(previous.Source, targetKey)).Select(p => p.Task).ToArray();
        async Task<bool> Run()
        {
            await Task.WhenAll(dependencies);
            await slots.WaitAsync(cancellationToken);
            try { return await TransferAsync(source, target, span, local, cancellationToken); }
            finally { slots.Release(); }
        }
        transfers.Add(new(Run(), sourceKey, targetKey));
    }
    private ResourceKey Key(PathValue path)
    {
        if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote) return new(remote.ToString(), path.Raw);
        return new("local", ResourcePath.Resolve(path.LocalPath(context.WorkingDirectory, context.HomeDirectory)));
    }
    private static bool Overlaps(ResourceKey left, ResourceKey right)
    {
        if (left.Endpoint != right.Endpoint) return false;
        if (left.Endpoint != "local") return true;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = Path.DirectorySeparatorChar;
        return string.Equals(left.Path, right.Path, comparison)
            || left.Path.StartsWith(right.Path.TrimEnd(separator) + separator, comparison)
            || right.Path.StartsWith(left.Path.TrimEnd(separator) + separator, comparison);
    }
    private async ValueTask DrainAsync()
    {
        if (transfers.Count == 0) return;
        var pending = transfers.ToArray(); transfers.Clear();
        var results = await Task.WhenAll(pending.Select(p => p.Task));
        if (results.Any(result => !result)) throw new MsyncException("One or more file transfers failed; completed files remain in place and the operation can be retried");
    }
    private static string SourceLabel(Value value) => value is StringValue text ? $"literal ({Encoding.UTF8.GetByteCount(text.Data)} UTF-8 bytes)" : value.ToString()!;
    private static bool AsBool(Value value) => value is BoolValue boolean ? boolean.Data : throw new MsyncException($"Condition requires Bool, received {value.Type}");
    private static string Text(Value value) => value switch { StringValue s => s.Data, PathValue p => p.Raw, NumberValue n => n.ToString(), BoolValue b => b.ToString(), _ => throw new MsyncException($"Cannot convert {value.Type} to String") };
    private static int Compare(Value left, Value right) => (left, right) switch
    {
        (NumberValue l, NumberValue r) => l.Data.CompareTo(r.Data),
        (BoolValue l, BoolValue r) => l.Data.CompareTo(r.Data),
        (TypeValue l, TypeValue r) => l.Data == r.Data ? 0 : 1,
        (StringValue or PathValue, StringValue or PathValue) => TextSemantics.CompareOrdinal(Text(left), Text(right)),
        _ => throw new MsyncException($"Cannot compare {left.Type} and {right.Type}; null supports only 'is' and 'is not'")
    };
}

public sealed class ExecutionSession(ExecutionOptions? options = null)
{
    private Value[] locals = [];
    public ValueTask<Value> ExecuteAsync(Module module, int startInstruction = 0, CancellationToken cancellationToken = default)
    {
        var count = module.Functions[module.EntryPoint].LocalCount;
        var previousLength = locals.Length; Array.Resize(ref locals, count);
        for (var i = previousLength; i < count; i++) locals[i] = Value.Null;
        return new Interpreter(module, options).ExecuteEntryAsync(locals, startInstruction, cancellationToken);
    }
}
