namespace Msync.Core.Hpir;

public static class HpirLiveTransferImports
{
    public static HpirImport Create(bool text, int completionLayout, bool pending = false) =>
        Create(pending ? HpirService.LiveTransferFutureText : HpirService.LiveTransfer, text || pending, completionLayout);

    public static HpirImport CreateBoolText(int completionLayout) =>
        Create(HpirService.LiveTransferBoolText, true, completionLayout);

    public static HpirImport CreateNumberText(int completionLayout) =>
        Create(HpirService.LiveTransferNumberText, true, completionLayout);

    public static HpirImport CreateSelection(int completionLayout) =>
        Create(HpirService.LiveTransferSelection, false, completionLayout);

    private static HpirImport Create(HpirService service, bool text, int completionLayout)
    {
        if (completionLayout < -1 || completionLayout >= int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(completionLayout));
        return new(service, ((completionLayout + 1) << 1) | (text ? 1 : 0),
            completionLayout < 0 ? HpirWidth.Void : HpirWidth.U64,
            service == HpirService.LiveTransferNumberText
                ? [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64] : [HpirWidth.U64, HpirWidth.U64]);
    }

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        var (text, layout) = Decode(import.Binding);
        if (import.Binding < 0) return false;
        if (import.Service == HpirService.LiveTransferNumberText)
        {
            if (!text || import.Arguments is not [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]) return false;
        }
        else if (import.Arguments is not [HpirWidth.U64, HpirWidth.U64]) return false;
        if (import.Service == HpirService.LiveTransferFutureText && (!text || program.TextLayout < 0)) return false;
        if (import.Service == HpirService.LiveTransferBoolText && !text) return false;
        if (import.Service == HpirService.LiveTransferSelection && (text || program.TextLiterals.Count == 0)) return false;
        if (layout < 0) return import.Result == HpirWidth.Void;
        return import.Result == HpirWidth.U64 && (uint)layout < (uint)program.Layouts.Count
            && program.Layouts[layout] is { Size: 32, Alignment: 8, PointerOffsets.Count: 0, BorrowOffsets.Count: 0 };
    }

    public static (bool Text, int Layout) Decode(int binding) => ((binding & 1) != 0, (binding >> 1) - 1);
}
