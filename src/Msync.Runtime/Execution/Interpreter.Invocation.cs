using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private ValueTask<Value> InvokeAsync(Value callee, IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
        InvokeValueAsync(ReadValue(callee), ValueArguments(arguments), cancellationToken);

    private ValueTask<Value> InvokeValueAsync(Value callee, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken, int? target = null, int scheduling = 0, SourceSpan? origin = null) => callee switch
    {
        FunctionValue { NativeName: { } name } => InvokeNativeAsync(name, arguments, cancellationToken),
        FunctionValue function => RunFunctionAsync(target ?? function.Function, function.Captures ?? [], arguments, cancellationToken),
        PathValue executable => RunProcessAsync(executable, arguments, cancellationToken, scheduling, origin),
        StringValue executable => RunProcessAsync(context.AsPath(executable), arguments, cancellationToken, scheduling, origin),
        _ => throw new MsyncException($"{callee.Type} is not callable")
    };

    private FunctionValue Specialize(FunctionValue function, Instruction instruction)
    {
        if (function.NativeName is not null) return function;
        var target = instruction.A < 0 ? SessionDispatchTarget(function, -instruction.A - 1, instruction.Span)
            : instruction.Text is null ? instruction.A : DispatchTarget(function, instruction);
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
    private int SessionDispatchTarget(FunctionValue function, int slot, SourceSpan? source)
    {
        RefreshSessionImage();
        if (module.SessionDispatches is not { } tables || (uint)slot >= (uint)tables.Count
            || tables[slot].Slot != slot)
            throw new MsyncException($"Session call signature #{slot} is unavailable. Execute this deferred value in its active REPL session.", source);
        return tables[slot].Targets.TryGetValue(function.Function, out int target) ? target
            : throw new MsyncException($"Function #{function.Function} has no compiled specialization for session call signature #{slot}.", source);
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
    private async ValueTask<Value> InvokeNativeAsync(string name, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (name == "_core.iterator_close") return await CloseIteratorAsync(arguments[0]);
            if (name is "io.read_lines" or "io.read_bytes") return ReadIORange(name, arguments, cancellationToken);
            if (name == "process.spawn") return await SpawnProcessAsync(arguments, cancellationToken);
            if (name is "process.stdout" or "process.stderr" or "process.success" or "process.exit_code" && arguments[0] is ProcessValue process)
                return ProcessMember(process, name[8..]);
            var operation = NativeRegistry.GetOperation(name);
            int deferred = FirstDeferredArgument(arguments);
            if (deferred >= 0
                && (operation.Effects & (InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke)) == 0
                && !arguments.Any(value => (value.Type.Kind & (TypeKind.Range | TypeKind.Function)) != 0))
            {
                var inputs = arguments;
                RuntimeContext origin = context.CaptureDirectory();
                async Task<Value> Complete() => await NativeRegistry.InvokeAsync(operation, await ResolveDeferredArgumentsAsync(inputs, deferred), origin, NativeCallback, cancellationToken);
                var signature = operation.Signature;
                TypeRef resultType = signature.ResultRule == NativeResultRule.CastTarget && inputs[1] is TypeValue target
                    ? target.Data : signature.Result;
                string? endpoint = resultType.WithoutNull == TypeRef.Path
                    ? inputs[0].Type == TypeRef.String ? "local" : PathEndpoint(inputs[0]) : null;
                return new DeferredValue(Complete(), resultType, inputs.SelectMany(Processes).Distinct().ToArray(), endpoint);
            }
            if (deferred >= 0) arguments = await ResolveDeferredArgumentsAsync(arguments, deferred);
            if (operation.UsesDependencies)
                await AwaitNativeDependenciesAsync(operation.Signature, arguments, cancellationToken);
            using var selectedSession = operation.OpensPathSession ? EnterPathSession(arguments, cancellationToken) : null;
            if (name == "text.format_fields") return await TextFormatting.FormatAsync("format_fields", arguments, ResolveAsync);
            return await NativeRegistry.InvokeAsync(operation, arguments, name == "cast" ? context.CaptureDirectory() : context, NativeCallback, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Observe?.Invoke(new("native", "failure", Detail: $"{name}: {error}"));
            throw;
        }
    }
}
