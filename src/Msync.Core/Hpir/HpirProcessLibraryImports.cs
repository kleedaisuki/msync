namespace Msync.Core.Hpir;

public enum HpirProcessControl : byte { Invalid, Pid, Running, Wait, Kill, Terminate, WriteInput, CloseInput }

public static class HpirProcessLibraryImports
{
    private const int LayoutShift = 4;

    public static HpirProcessControl Control(string name) => name switch
    {
        "process.pid" => HpirProcessControl.Pid,
        "process.running" => HpirProcessControl.Running,
        "process.wait" => HpirProcessControl.Wait,
        "process.kill" => HpirProcessControl.Kill,
        "process.terminate" => HpirProcessControl.Terminate,
        "process.write_stdin" => HpirProcessControl.WriteInput,
        "process.close_stdin" => HpirProcessControl.CloseInput,
        _ => HpirProcessControl.Invalid
    };

    public static string Name(HpirProcessControl operation) => operation switch
    {
        HpirProcessControl.Pid => "process.pid", HpirProcessControl.Running => "process.running",
        HpirProcessControl.Wait => "process.wait", HpirProcessControl.Kill => "process.kill",
        HpirProcessControl.Terminate => "process.terminate", HpirProcessControl.WriteInput => "process.write_stdin",
        HpirProcessControl.CloseInput => "process.close_stdin",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static HpirImport Spawn(int layout, HpirProcessArgument items, bool textPath) =>
        new(HpirService.ProcessSpawn, checked(layout * (1 << LayoutShift)) | ((int)items << 1) | (textPath ? 1 : 0),
            HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);

    public static (int Layout, HpirProcessArgument Items, bool TextPath) DecodeSpawn(int binding) =>
        (binding >> LayoutShift, (HpirProcessArgument)((binding & ((1 << LayoutShift) - 1)) >> 1), (binding & 1) != 0);

    public static HpirImport Invoke(HpirProcessControl operation) =>
        new(HpirService.ProcessControl, (int)operation, ControlResult(operation),
            operation == HpirProcessControl.WriteInput ? [HpirWidth.U64, HpirWidth.U64] : [HpirWidth.U64]);

    private static HpirWidth ControlResult(HpirProcessControl operation) => operation switch
    {
        HpirProcessControl.Wait or HpirProcessControl.Pid => HpirWidth.U64,
        HpirProcessControl.Running or HpirProcessControl.Kill or HpirProcessControl.Terminate => HpirWidth.U8,
        _ => HpirWidth.Void
    };

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if (import.Service == HpirService.ProcessControl)
        {
            if (import.Binding < (int)HpirProcessControl.Pid || import.Binding > (int)HpirProcessControl.CloseInput)
                return false;
            var expected = Invoke((HpirProcessControl)import.Binding);
            return import.Result == expected.Result && import.Arguments.AsSpan().SequenceEqual(expected.Arguments);
        }
        var (layout, items, text) = DecodeSpawn(import.Binding);
        return items <= HpirProcessArgument.Bool && (!text || program.PathAnchors)
            && HpirFutureImports.ValidProcessHandle(program, layout)
            && import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64];
    }
}
