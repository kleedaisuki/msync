using System.Diagnostics;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private async ValueTask<Value> InvokeScheduledAsync(Value callee, IReadOnlyList<Value> arguments, int scheduling, SourceSpan? origin, CancellationToken cancellationToken)
    {
        callee = ReadValue(callee);
        arguments = ValueArguments(arguments);
        if (callee is PathValue or StringValue)
        {
            var process = await StartProcessAsync(context.AsPath(callee), await ResolveArgumentsAsync(arguments), cancellationToken, origin);
            return (scheduling & 1) != 0 && (scheduling & 2) == 0 ? process : await AcquireAsync(process);
        }
        return await InvokeAsync(callee, arguments, cancellationToken);
    }
    private async ValueTask<Value> RunProcessAsync(PathValue executable, IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        => await AcquireAsync(await StartProcessAsync(executable, await ResolveArgumentsAsync(arguments), cancellationToken));

    private async ValueTask<ProcessValue> StartProcessAsync(PathValue executable, IReadOnlyList<Value> arguments, CancellationToken cancellationToken, SourceSpan? origin = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ordering;
        await JoinTransfersAsync(includeProcessFlows: false);
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"process {executable}({string.Join(", ", arguments)})");
            return new ProcessValue(0, "", "");
        }
        ProcessStartInfo info;
        if (executable.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
            info = TransferEngine.CreateRemoteProcessStartInfo(remote, executable.Raw, arguments.Select(argument => argument is PathValue path ? path.Raw : Text(argument)).ToArray());
        else
        {
            info = new(executable.LocalPath(context.WorkingDirectory, context.HomeDirectory));
            foreach (var argument in arguments) info.ArgumentList.Add(argument is PathValue path ? path.LocalPath(context.WorkingDirectory, context.HomeDirectory) : Text(argument));
        }
        info.WorkingDirectory = context.WorkingDirectory; info.UseShellExecute = false; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
        var process = Process.Start(info) ?? throw new MsyncException($"Could not start '{executable}'");
        context.Observe?.Invoke(new("process", "started", executable.ToString(), Detail: $"pid={process.Id}"));
        async Task<ProcessValue> Complete()
        {
            using (process)
                try
                {
                    var budget = new SpoolBudget(context.SpoolLimit);
                    var stdout = ReadProcessStreamAsync(process.StandardOutput.BaseStream, budget, CancellationToken.None);
                    var stderr = ReadProcessStreamAsync(process.StandardError.BaseStream, budget, CancellationToken.None);
                    await process.WaitForExitAsync(CancellationToken.None);
                    var output = await Task.WhenAll(stdout, stderr);
                    context.Observe?.Invoke(new("process", process.ExitCode == 0 ? "success" : "failure", executable.ToString(), Detail: $"exit={process.ExitCode}", DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
                    return new(process.ExitCode, output[0], output[1]);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    context.Observe?.Invoke(new("process", "failure", executable.ToString(), Detail: error.ToString()));
                    throw new MsyncException($"Cannot collect the result of '{executable}'.", origin, error);
                }
        }
        var value = new ProcessValue(0, "", "") { Completion = Complete() };
        (currentOwner.Processes ??= []).Add(value);
        sessionProcesses.Add(value);
        return value;
    }
    private static Value ProcessMember(ProcessValue process, string name)
    {
        static Value Completed(ProcessValue completed, string member) => member switch
        {
            "stdout" => new StringValue(completed.StandardOutput),
            "stderr" => new StringValue(completed.StandardError),
            "exit_code" => new NumberValue(completed.ExitCode),
            "success" => completed.ExitCode == 0 ? Value.True : Value.False,
            _ => throw new MsyncException($"Process has no member '{member}'")
        };
        if (process.Completion is null) return Completed(process, name);
        async Task<Value> Resolve() => Completed(await process.Completion, name);
        var type = name switch { "stdout" or "stderr" => TypeRef.String, "exit_code" => TypeRef.Number, "success" => TypeRef.Bool, _ => throw new MsyncException($"Process has no member '{name}'") };
        return new DeferredValue(Resolve(), type, [process]);
    }
}
