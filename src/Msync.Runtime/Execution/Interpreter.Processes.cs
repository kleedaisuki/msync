using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private async ValueTask<Value> RunProcessAsync(PathValue executable, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken, int scheduling, SourceSpan? origin)
    {
        var process = await StartProcessAsync(executable, await ResolveArgumentsAsync(arguments), cancellationToken, origin);
        return (scheduling & 1) != 0 && (scheduling & 2) == 0 ? process : await AcquireAsync(process);
    }

    private async ValueTask<ProcessValue> StartProcessAsync(PathValue executable, IReadOnlyList<Value> arguments, CancellationToken cancellationToken, SourceSpan? origin = null, bool pipeInput = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ordering;
        await JoinTransfersAsync(includeProcessFlows: false);
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"process {executable}({string.Join(", ", arguments)})");
            return new ProcessValue(0, "", "") { ProcessId = 0 };
        }
        using var activity = context.Timeline?.Begin("process.start");
        if (activity is not null)
        {
            TransferTimeline.Path(activity, "executable", executable);
            activity.SetSource(origin);
            activity.SetMetric("process.arguments", arguments.Count);
            activity.SetAttribute("process.stdin_redirected", pipeInput ? "true" : "false");
        }
        try
        {
            ProcessStartInfo info;
            bool controlledRemote = pipeInput && executable.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl;
            if (executable.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
            {
                var argv = arguments.Select(argument => argument is PathValue path ? path.Raw : Text(argument)).ToArray();
                info = controlledRemote ? RemoteProcessControl.StartInfo(remote, executable.Raw, argv)
                    : TransferEngine.CreateRemoteProcessStartInfo(remote, executable.Raw, argv);
            }
            else
            {
                info = new(executable.LocalPath(context.WorkingDirectory, context.HomeDirectory));
                foreach (var argument in arguments) info.ArgumentList.Add(argument is PathValue path ? path.LocalPath(context.WorkingDirectory, context.HomeDirectory) : Text(argument));
            }
            info.WorkingDirectory = context.WorkingDirectory; info.UseShellExecute = false; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.RedirectStandardInput = pipeInput;
            if (pipeInput) info.StandardInputEncoding = new UTF8Encoding(false);
            var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
            var process = Process.Start(info) ?? throw new MsyncException($"Could not start '{executable}'");
            activity?.SetMetric("transport.pid", process.Id);
            IProcessControl? control = executable.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl)
                ? new LocalProcessControl(process, pipeInput) : null;
            string initialDiagnostic = "";
            if (controlledRemote)
            {
                using CancellationTokenRegistration startupCancel = cancellationToken.Register(() => StopProcess(process));
                try
                {
                    var identity = await RemoteProcessControl.ReadIdentityAsync(process, cancellationToken);
                    control = new RemoteProcessControl(process, identity.Id);
                    initialDiagnostic = identity.Diagnostic;
                }
                catch { StopProcess(process); process.Dispose(); throw; }
            }
            context.Observe?.Invoke(new("process", "started", executable.ToString(), Detail: control is null
                ? $"transport_pid={process.Id}" : $"pid={control.ProcessId}"));
            if (control is not null) activity?.SetMetric("process.pid", control.ProcessId);
            async Task<ProcessValue> Complete()
            {
                using var collect = context.Timeline?.Begin("process.collect");
                if (collect is not null)
                {
                    collect.SetMetric("transport.pid", process.Id);
                    if (control is not null) collect.SetMetric("process.pid", control.ProcessId);
                }
                using CancellationTokenRegistration cancel = cancellationToken.Register(() => StopProcess(process));
                using (process)
                    try
                    {
                        var budget = new SpoolBudget(context.SpoolLimit);
                        var stdout = ReadProcessStreamAsync(process.StandardOutput.BaseStream, budget, CancellationToken.None,
                            context.Timeline, "process.stdout");
                        var stderr = ReadProcessStreamAsync(process.StandardError.BaseStream, budget, CancellationToken.None,
                            context.Timeline, "process.stderr");
                        using (var wait = context.Timeline?.Begin("process.exit_wait"))
                        {
                            Task exited = process.WaitForExitAsync(CancellationToken.None);
                            wait?.SetAttribute("wait.pending_on_entry", exited.IsCompleted ? "false" : "true");
                            try { await exited; }
                            catch (Exception error) { wait?.Fail(error); throw; }
                        }
                        var output = await Task.WhenAll(stdout, stderr);
                        collect?.SetAttribute("process.exit_code", process.ExitCode.ToString(CultureInfo.InvariantCulture));
                        collect?.Complete(process.ExitCode == 0);
                        context.Observe?.Invoke(new("process", process.ExitCode == 0 ? "success" : "failure", executable.ToString(), Detail: $"exit={process.ExitCode}", DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
                        return new(process.ExitCode, output[0], initialDiagnostic + output[1]) { ProcessId = control?.ProcessId, Control = control };
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        collect?.Fail(error);
                        context.Observe?.Invoke(new("process", "failure", executable.ToString(), Detail: error.ToString()));
                        throw new MsyncException($"Cannot collect the result of '{executable}'.", origin, error);
                    }
                    finally
                    {
                        if (control is LocalProcessControl local) local.Complete();
                        if (control is RemoteProcessControl endpoint) endpoint.Complete();
                    }
            }
            Task<ProcessValue> completion = Complete();
            if (activity is not null) context.Timeline!.Register(completion, activity);
            var value = new ProcessValue(0, "", "")
            {
                Completion = completion, ProcessId = control?.ProcessId, Control = control,
                AbortOwnedTransport = control is null ? () => StopProcess(process) : null
            };
            (currentOwner.Processes ??= []).Add(value);
            sessionProcesses.Add(value);
            return value;
        }
        catch (Exception error) { activity?.Fail(error); throw; }
    }
    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    private async ValueTask<Value> SpawnProcessAsync(IReadOnlyList<Value> arguments, CancellationToken token)
    {
        arguments = await ResolveArgumentsAsync(arguments);
        if (arguments[1] is not RangeValue range) throw new MsyncException("process.spawn expects a Range of arguments.");
        List<Value> values = [];
        await foreach (Value value in range.Items.WithCancellation(token))
        {
            if (value is not (PathValue or StringValue or NumberValue or BoolValue))
                throw new MsyncException($"Process arguments require Path, String, Number or Bool, received {value.Type}.");
            values.Add(value);
        }
        return await StartProcessAsync(context.AsPath(arguments[0]), await ResolveArgumentsAsync(values), token, pipeInput: true);
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
    private sealed class SpoolBudget(long? limit)
    {
        private long used;
        public void Account(int count)
        {
            var total = Interlocked.Add(ref used, count);
            if (limit is { } maximum && total > maximum) throw new IOException($"Process output exceeded configured spool limit of {maximum} bytes (observed {total})");
        }
    }
    private static async Task<string> ReadProcessStreamAsync(Stream stream, SpoolBudget budget, CancellationToken cancellationToken,
        RuntimeTimeline? timeline, string phase)
    {
        using var activity = timeline?.Begin(phase);
        try
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
            activity?.SetMetric("bytes.read", output.Length);
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (Exception error) { activity?.Fail(error); throw; }
    }
}
