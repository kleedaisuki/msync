using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    internal static ValueTask<NativeProcess> StartTextProcessAsync(NativePathValue executable, string[] arguments,
        NativeRuntimeContext context, bool pipeInput, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return context.DryRun
            ? DryRunProcessAsync(executable, string.Join(", ", arguments), context)
            : StartProcessCoreAsync(executable, arguments, context, executable.Connection ?? default,
                RemoteProcess(executable), pipeInput, token);
    }

    internal static int ProcessId(NativeProcess process) => process.ProcessId ?? process.Control?.ProcessId
        ?? throw new MsyncException("This Process value has no operating-system identity; use process.spawn for controlled execution.");

    internal static bool ProcessRunning(NativeProcess process) => process.Control?.IsRunning ?? process.Completion is { IsCompleted: false };

    internal static bool StopProcess(NativeProcess process, bool force)
    {
        if (process.Control is { } control) return force ? control.Kill() : control.Terminate();
        if (process.Completion is { IsCompleted: false })
            throw new MsyncException("This Process has no control channel; use process.spawn for managed remote execution.");
        return false;
    }

    internal static ValueTask WriteProcessInput(NativeProcess process, string text, CancellationToken token) =>
        (process.Control ?? throw new MsyncException("This Process has no writable input pipe; use process.spawn.")).WriteInputAsync(text, token);

    internal static ValueTask CloseProcessInput(NativeProcess process, CancellationToken token) =>
        process.Control is { } control ? control.CloseInputAsync(token) : ValueTask.CompletedTask;

    private static bool RemoteProcess(NativePathValue executable) =>
        executable.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl;

    private static async ValueTask<NativeProcess> DryRunProcessAsync(NativePathValue executable, string arguments, NativeRuntimeContext context)
    {
        await context.Output.WriteLineAsync($"process {executable}({arguments})");
        return new(0, "", "") { ProcessId = 0 };
    }

    private static async ValueTask<NativeProcess> StartProcessCoreAsync(NativePathValue executable, string[] argv,
        NativeRuntimeContext context, NativeConnection connection, bool remote, bool pipeInput, CancellationToken token)
    {
        ProcessStartInfo info = remote
            ? pipeInput ? RemoteProcessControl.StartInfo(connection.Kind, connection.Address, executable.Raw, argv, context)
                : NativeTransferProtocol.CreateRemoteProcessStartInfo(connection.Kind, connection.Address, executable.Raw, argv, context)
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
        return value;
    }

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
