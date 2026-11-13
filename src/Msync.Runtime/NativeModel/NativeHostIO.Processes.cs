using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static async ValueTask<NativeValue> ProcessOperationAsync(string name, IReadOnlyList<NativeValue> args,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (name == "spawn")
        {
            List<NativeValue> arguments = [];
            await foreach (NativeValue item in Range(args[1], "process.spawn").Items.WithCancellation(token))
            {
                if (item.Kind is not (NativeKind.Path or NativeKind.String or NativeKind.Number or NativeKind.Bool))
                    throw new MsyncException($"Process arguments require Path, String, Number or Bool, received {item.Type}.");
                arguments.Add(item);
            }
            return NativeValue.FromProcess(await NativeHostIO.StartProcessAsync(context.AsPath(args[0]), arguments, context, true, token));
        }
        NativeProcess process = args[0].Kind == NativeKind.Process ? args[0].AsProcess()
            : throw new MsyncException("Expected Process");
        if (name == "wait") return NativeValue.FromProcess(await process.WaitAsync(token));
        if (name == "pid") return NativeValue.FromNumber(process.ProcessId ?? process.Control?.ProcessId
            ?? throw new MsyncException("This Process value has no operating-system identity; use process.spawn for controlled execution."));
        if (name == "running") return Bool(process.Control?.IsRunning ?? process.Completion is { IsCompleted: false });
        if (name is "kill" or "terminate")
        {
            if (process.Control is { } control) return Bool(name == "kill" ? control.Kill() : control.Terminate());
            if (process.Completion is { IsCompleted: false })
                throw new MsyncException("This Process has no control channel; use process.spawn for managed remote execution.");
            return NativeValue.False;
        }
        if (name == "write_stdin")
        {
            IProcessControl control = process.Control ?? throw new MsyncException("This Process has no writable input pipe; use process.spawn.");
            await control.WriteInputAsync(Text(args[1]), token);
            return NativeValue.Null;
        }
        if (name == "close_stdin")
        {
            if (process.Control is { } control) await control.CloseInputAsync(token);
            return NativeValue.Null;
        }
        NativeProcess completed = await process.WaitAsync(token);
        return name switch
        {
            "stdout" => NativeValue.FromString(completed.StandardOutput),
            "stderr" => NativeValue.FromString(completed.StandardError),
            "success" => Bool(completed.ExitCode == 0),
            "exit_code" => NativeValue.FromNumber(completed.ExitCode),
            _ => throw new MsyncException($"Unknown process native '{name}'")
        };
    }
}

public static partial class NativeHostIO
{
    public static async ValueTask<NativeValue> ProcessCallAsync(NativePathValue executable,
        IReadOnlyList<NativeValue> arguments, NativeRuntimeContext context, CancellationToken token) =>
        NativeValue.FromProcess(await StartProcessAsync(executable, arguments, context, false, token));

    public static async ValueTask<NativeProcess> StartProcessAsync(NativePathValue executable,
        IReadOnlyList<NativeValue> arguments, NativeRuntimeContext context, bool pipeInput, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"process {executable}({string.Join(", ", arguments)})");
            return new(0, "", "") { ProcessId = 0 };
        }
        bool remote = executable.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl;
        NativeConnection connection = executable.Connection ?? default;
        string[] argv = arguments.Select(value => ProcessArgument(value, context, remote)).ToArray();
        ProcessStartInfo info = remote
            ? pipeInput ? RemoteProcessControl.StartInfo(connection.Kind, connection.Address, executable.Raw, argv)
                : NativeTransferProtocol.CreateRemoteProcessStartInfo(connection.Kind, connection.Address, executable.Raw, argv)
            : new(executable.LocalPath(context.WorkingDirectory, context.HomeDirectory));
        if (!remote) foreach (string argument in argv) info.ArgumentList.Add(argument);
        info.WorkingDirectory = context.WorkingDirectory;
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = pipeInput;
        if (pipeInput) info.StandardInputEncoding = new UTF8Encoding(false);
        Process process = Process.Start(info) ?? throw new MsyncException($"Could not start '{executable}'");
        IProcessControl? control = remote ? null : new LocalProcessControl(process, pipeInput);
        string initialDiagnostic = "";
        if (remote && pipeInput)
        {
            using CancellationTokenRegistration cancel = token.Register(() => Stop(process));
            try
            {
                var identity = await RemoteProcessControl.ReadIdentityAsync(process, token);
                control = new RemoteProcessControl(process, identity.Id);
                initialDiagnostic = identity.Diagnostic;
            }
            catch { Stop(process); process.Dispose(); throw; }
        }
        context.Observe?.Invoke(new("process", "started", executable.ToString(), Detail:
            control is null ? $"transport_pid={process.Id}" : $"pid={control.ProcessId}"));
        NativeProcess value = new(0, "", "")
        {
            ProcessId = control?.ProcessId,
            Control = control,
            Completion = CollectProcessAsync(process, control, executable, initialDiagnostic, context, token),
            AbortOwnedTransport = control is null ? () => Stop(process) : null
        };
        context.ProcessCreated?.Invoke(value);
        return value;
    }

    private static string ProcessArgument(NativeValue value, NativeRuntimeContext context, bool remote) => value.Kind switch
    {
        NativeKind.Path => remote ? value.AsPath().Raw : context.LocalPath(value.AsPath()),
        NativeKind.String => value.AsString(),
        NativeKind.Number => value.AsNumber().ToString(CultureInfo.InvariantCulture),
        NativeKind.Bool => value.AsBool() ? "true" : "false",
        _ => throw new MsyncException($"Process arguments require Path, String, Number or Bool, received {value.Type}.")
    };

    private static async Task<NativeProcess> CollectProcessAsync(Process process, IProcessControl? control,
        NativePathValue executable, string initialDiagnostic, NativeRuntimeContext context, CancellationToken token)
    {
        using CancellationTokenRegistration cancel = token.Register(() => Stop(process));
        using (process)
        {
            ProcessOutputBudget budget = new(context.SpoolLimit);
            Task<string> stdout = ReadProcessStreamAsync(process.StandardOutput.BaseStream, budget, context, "process.stdout");
            Task<string> stderr = ReadProcessStreamAsync(process.StandardError.BaseStream, budget, context, "process.stderr");
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                string[] output = await Task.WhenAll(stdout, stderr);
                token.ThrowIfCancellationRequested();
                context.Observe?.Invoke(new("process", process.ExitCode == 0 ? "success" : "failure", executable.ToString(), Detail: $"exit={process.ExitCode}"));
                return new(process.ExitCode, output[0], initialDiagnostic + output[1]) { ProcessId = control?.ProcessId, Control = control };
            }
            catch (Exception error)
            {
                Stop(process);
                await process.WaitForExitAsync(CancellationToken.None);
                await ((Task)Task.WhenAll(stdout, stderr)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (error is OperationCanceledException) throw;
                throw new MsyncException($"Cannot collect the result of '{executable}'.", inner: error);
            }
            finally
            {
                if (control is LocalProcessControl local) local.Complete();
                if (control is RemoteProcessControl endpoint) endpoint.Complete();
            }
        }
    }

    private sealed class ProcessOutputBudget(long? limit)
    {
        private long used;
        internal void Account(int count)
        {
            long total = Interlocked.Add(ref used, count);
            if (limit is { } maximum && total > maximum)
                throw new IOException($"Process output exceeded configured spool limit of {maximum} bytes (observed {total}).");
        }
    }

    private static async Task<string> ReadProcessStreamAsync(Stream input, ProcessOutputBudget budget,
        NativeRuntimeContext context, string phase)
    {
        using var activity = context.Timeline?.Begin(phase);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        Exception? failure = null;
        int count;
        while ((count = await input.ReadAsync(buffer, CancellationToken.None)) != 0)
        {
            if (failure is not null) continue;
            try { budget.Account(count); await output.WriteAsync(buffer.AsMemory(0, count)); }
            catch (IOException error) { failure = error; }
        }
        if (failure is not null) { activity?.Fail(failure); throw failure; }
        activity?.SetMetric("bytes.read", output.Length);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
