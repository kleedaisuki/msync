// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/FloatingPointMode.h.
namespace Msync.Core.Llvm;

[Flags]
public enum LlvmFPClassTest : ushort
{
    None = 0, SNaN = 1, QNaN = 2, NegInfinity = 4, NegNormal = 8,
    NegSubnormal = 16, NegZero = 32, PosZero = 64, PosSubnormal = 128,
    PosNormal = 256, PosInfinity = 512,
    NaN = SNaN | QNaN, Infinity = NegInfinity | PosInfinity,
    Normal = NegNormal | PosNormal, Subnormal = NegSubnormal | PosSubnormal,
    Zero = NegZero | PosZero, PosFinite = PosNormal | PosSubnormal | PosZero,
    NegFinite = NegNormal | NegSubnormal | NegZero, Finite = PosFinite | NegFinite,
    Positive = PosFinite | PosInfinity, Negative = NegFinite | NegInfinity,
    All = NaN | Infinity | Finite
}

public enum LlvmDenormalMode : sbyte { Invalid = -1, IEEE, PreserveSign, PositiveZero, Dynamic }

public readonly record struct LlvmDenormalModeInfo
{
    private readonly byte output, input;
    public LlvmDenormalModeInfo(LlvmDenormalMode output, LlvmDenormalMode input)
    { this.output = (byte)((int)output + 1); this.input = (byte)((int)input + 1); }
    public LlvmDenormalMode Output => (LlvmDenormalMode)(output - 1);
    public LlvmDenormalMode Input => (LlvmDenormalMode)(input - 1);
    public bool IsSimple => Output == Input;
    public bool IsValid => Output != LlvmDenormalMode.Invalid && Input != LlvmDenormalMode.Invalid;
    public bool InputsAreZero => Input is LlvmDenormalMode.PreserveSign or LlvmDenormalMode.PositiveZero;
    public bool InputsMayBeZero => InputsAreZero || Input == LlvmDenormalMode.Dynamic;
    public bool OutputsAreZero => Output is LlvmDenormalMode.PreserveSign or LlvmDenormalMode.PositiveZero;
    public bool OutputsMayBeZero => OutputsAreZero || Output == LlvmDenormalMode.Dynamic;
    public bool InputsMayBePositiveZero => Input is LlvmDenormalMode.PositiveZero or LlvmDenormalMode.Dynamic;
    public bool OutputsMayBePositiveZero => Output is LlvmDenormalMode.PositiveZero or LlvmDenormalMode.Dynamic;
    public static LlvmDenormalModeInfo Default => IEEE;
    public static LlvmDenormalModeInfo IEEE => new(LlvmDenormalMode.IEEE, LlvmDenormalMode.IEEE);
    public static LlvmDenormalModeInfo Dynamic => new(LlvmDenormalMode.Dynamic, LlvmDenormalMode.Dynamic);
    public static LlvmDenormalModeInfo PreserveSign => new(LlvmDenormalMode.PreserveSign, LlvmDenormalMode.PreserveSign);
    public static LlvmDenormalModeInfo PositiveZero => new(LlvmDenormalMode.PositiveZero, LlvmDenormalMode.PositiveZero);
    public uint ToIntValue() { System.Diagnostics.Debug.Assert(IsValid); return ((uint)Input << 2) | (uint)Output; }
    public static LlvmDenormalModeInfo CreateFromIntValue(uint value) => new((LlvmDenormalMode)(value & 3), (LlvmDenormalMode)((value >> 2) & 3));
    public LlvmDenormalModeInfo MergeCalleeMode(LlvmDenormalModeInfo callee) => new(
        callee.Output == LlvmDenormalMode.Dynamic ? Output : callee.Output,
        callee.Input == LlvmDenormalMode.Dynamic ? Input : callee.Input);
}

public readonly record struct LlvmDenormalFPEnv
{
    public LlvmDenormalFPEnv(LlvmDenormalModeInfo defaultMode, LlvmDenormalModeInfo f32Mode = default)
    {
        DefaultMode = defaultMode;
        F32Mode = new(f32Mode.Output == LlvmDenormalMode.Invalid ? defaultMode.Output : f32Mode.Output,
            f32Mode.Input == LlvmDenormalMode.Invalid ? defaultMode.Input : f32Mode.Input);
    }
    public LlvmDenormalModeInfo DefaultMode { get; }
    public LlvmDenormalModeInfo F32Mode { get; }
    public static LlvmDenormalFPEnv Default => new(LlvmDenormalModeInfo.IEEE);
    public uint ToIntValue() => DefaultMode.ToIntValue() | (F32Mode.ToIntValue() << 4);
    public static LlvmDenormalFPEnv CreateFromIntValue(uint value) => new(
        LlvmDenormalModeInfo.CreateFromIntValue(value), LlvmDenormalModeInfo.CreateFromIntValue(value >> 4));
}
