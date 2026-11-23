using System.Runtime.Intrinsics.X86;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirMachineImage
{
    private sealed partial class X64
    {
        internal int InlineServiceSites { get; private set; }

        private bool InlineService(HpirImport import, IReadOnlyList<int> arguments, int result)
        {
            if (import.Service != HpirService.HasLayout) return ReadyMath(import, arguments, result);
            Load(arguments[0], 8);
            Emit(0x48, 0x85, 0xc0); // test rax, rax; a null payload already has a zero AL
            int isNull = Conditional(0x84);
            Memory(0x81, 7, A, HpirHeap.LayoutFromPayload);
            Dword(import.Binding);
            Emit(0x0f, 0x94, 0xc0); // sete al
            Patch(isNull, Length);
            Store(result, 1);
            return true;
        }

        private bool ReadyMath(HpirImport import, IReadOnlyList<int> arguments, int result)
        {
            if (import.Service != HpirService.MathF64) return false;
            switch ((HpirMathOp)import.Binding)
            {
                case HpirMathOp.Abs:
                    Load(arguments[0], 8);
                    Emit(0x48, 0x0f, 0xba, 0xf0, 0x3f); // btr rax, 63
                    Store(result, 8);
                    return true;
                case HpirMathOp.Sqrt:
                    FloatMemory(0x51, 0, arguments[0]);
                    FloatMemory(0x11, 0, result);
                    return true;
            }

            int mode = (HpirMathOp)import.Binding switch
            {
                HpirMathOp.RoundEven => 0,
                HpirMathOp.Floor => 1,
                HpirMathOp.Ceil => 2,
                HpirMathOp.Trunc => 3,
                _ => -1
            };
            if (mode < 0 || !Sse41.IsSupported) return false;
            FloatMemory(0x10, 0, arguments[0]);
            Emit(0x66, 0x0f, 0x3a, 0x0b, 0xc0, (byte)(mode | 8)); // roundsd xmm0, xmm0, mode; suppress precision exception
            FloatMemory(0x11, 0, result);
            return true;
        }
    }
}
