namespace Msync.Core.Hpir;

internal readonly record struct HpirInstructionShape(HpirWidth D, HpirWidth A = HpirWidth.Void, HpirWidth B = HpirWidth.Void)
{
    private const HpirWidth Byte = HpirWidth.U8, Word = HpirWidth.U64;
    private const HpirWidth Short = HpirWidth.U16, Int = HpirWidth.U32;

    internal static HpirInstructionShape Of(HpirOp op) => op switch
    {
        HpirOp.CopyU8 => new(Byte, Byte),
        HpirOp.SignExtendI8 or HpirOp.ConvertU8ToF64 => new(Word, Byte),
        HpirOp.BoolFromF64 => new(Byte, Word),
        HpirOp.SignExtendI16 => new(Word, Short),
        HpirOp.SignExtendI32 => new(Word, Int),
        HpirOp.CopyU16 => new(Short, Short),
        HpirOp.CopyU32 => new(Int, Int),
        HpirOp.CopyU64 or HpirOp.OrdinalF64ToU64 or HpirOp.ConvertI64ToF64 => new(Word, Word),
        HpirOp.ConstU8 => new(Byte),
        HpirOp.ConstU16 => new(Short),
        HpirOp.ConstU32 => new(Int),
        HpirOp.ConstU64 or HpirOp.AddressOf or HpirOp.AddressOfStatic or HpirOp.AddressOfStaticCell or HpirOp.AddressOfConnection
            or HpirOp.AddressOfGlobal or HpirOp.AddressOfTypeCell => new(Word),
        HpirOp.OffsetPointer or HpirOp.NegF64 => new(Word, Word),
        HpirOp.AddPointer or HpirOp.AddF64 or HpirOp.SubF64 or HpirOp.MulF64 or HpirOp.DivF64 or HpirOp.RemF64
            or HpirOp.AddU64 or HpirOp.SubU64 or HpirOp.MulU64 or HpirOp.AndU64 or HpirOp.OrU64
            or HpirOp.XorU64 or HpirOp.ShlU64 or HpirOp.ShrU64 or HpirOp.SarI64 =>
            new(Word, Word, Word),
        HpirOp.EqF64 or HpirOp.NeF64 or HpirOp.LtF64 or HpirOp.LeF64 or HpirOp.GtF64 or HpirOp.GeF64 or HpirOp.EqU64
            or HpirOp.LtU64 or HpirOp.LeU64 or HpirOp.LtI64 or HpirOp.LeI64 =>
            new(Byte, Word, Word),
        HpirOp.AndU8 or HpirOp.OrU8 or HpirOp.XorU8 or HpirOp.EqU8 => new(Byte, Byte, Byte),
        HpirOp.Branch => new(HpirWidth.Void, Byte),
        _ => default
    };
}
