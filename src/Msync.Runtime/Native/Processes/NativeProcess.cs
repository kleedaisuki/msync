using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static async ValueTask<Value> ProcessOperationAsync(string name, IReadOnlyList<Value> args, CancellationToken token)
    {
        if (name == "spawn") throw new MsyncException("process.spawn requires an interpreter execution context.");
        ProcessValue value = args[0] as ProcessValue ?? throw new MsyncException("Expected Process");
        if (name == "wait") return value.Completion is null ? value : await value.Completion.WaitAsync(token);
        if (name == "pid") return new NumberValue(value.ProcessId ?? value.Control?.ProcessId
            ?? throw new MsyncException("This Process value has no operating-system identity; use process.spawn for controlled execution."));
        if (name == "running") return Bool(value.Control?.IsRunning ?? value.Completion is { IsCompleted: false });
        if (name is "kill" or "terminate")
        {
            if (value.Control is { } control) return Bool(name == "kill" ? control.Kill() : control.Terminate());
            if (value.Completion is { IsCompleted: false })
                throw new MsyncException("This Process has no control channel; use process.spawn for managed remote execution.");
            return Value.False;
        }
        if (name == "write_stdin")
        {
            IProcessControl control = value.Control ?? throw new MsyncException("This Process has no writable input pipe; use process.spawn.");
            await control.WriteInputAsync(Text(args[1]), token);
            return Value.Null;
        }
        if (name == "close_stdin")
        {
            if (value.Control is { } control) await control.CloseInputAsync(token);
            return Value.Null;
        }
        if (value.Completion is not null) value = await value.Completion.WaitAsync(token);
        return ProcessOperation(name, [value]);
    }
    private static Value ProcessOperation(string name, IReadOnlyList<Value> args)
    {
        ProcessValue process = args[0] as ProcessValue ?? throw new MsyncException("Expected Process");
        return name switch
        {
            "stdout" => new StringValue(process.StandardOutput),
            "stderr" => new StringValue(process.StandardError),
            "success" => Bool(process.ExitCode == 0),
            "exit_code" => new NumberValue(process.ExitCode),
            _ => throw new MsyncException($"Unknown process native '{name}'")
        };
    }
}
