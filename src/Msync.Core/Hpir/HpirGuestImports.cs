namespace Msync.Core.Hpir;

public static class HpirGuestImports
{
    // The final lanes carry availability and the exact completion layout.
    // Earlier words match the selected guest calling convention.
    public static HpirImport Number(int entry, int arguments)
    {
        var widths = new HpirWidth[checked(arguments + 2)];
        Array.Fill(widths, HpirWidth.U64);
        widths[^2] = HpirWidth.U8;
        return new(HpirService.GuestNumber, entry, HpirWidth.U64, widths);
    }

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if ((uint)import.Binding >= (uint)program.Functions.Count || import.Result != HpirWidth.U64)
            return false;
        HpirFunction target = program.Functions[import.Binding];
        if (target.Result != HpirWidth.U64 || import.Arguments.Length != target.Parameters.Count + 2
            || import.Arguments[^2] != HpirWidth.U8 || import.Arguments[^1] != HpirWidth.U64) return false;
        for (int index = 0; index < target.Parameters.Count; index++)
            if (import.Arguments[index] != HpirWidth.U64 || target.Parameters[index].Width != HpirWidth.U64) return false;
        return true;
    }
}
