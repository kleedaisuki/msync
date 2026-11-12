using System.Numerics;
using System.Runtime.InteropServices;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.ReadonlyNumberFold.Operation;

namespace Msync.Runtime;

internal static partial class NativeScopeRecurrence
{
    // XMM0–5 are volatile under both x64 ABIs; AAPCS64 also has D16–31.
    private const uint X64Registers = 0x0000003e, Arm64Registers = 0xffff00fe;

    internal static NativeCodePage? Compile(ReadonlyNumberFold plan, out int byteCount)
    {
        byteCount = 0;
        if (!NumericLoop.NativeEnabled) return null;
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        int[]? registers = AssignRegisters(plan, arm64 ? Arm64Registers : X64Registers);
        if (registers is null) return null;
        byte[]? bytes = arm64
            ? new Arm64(plan, registers).Emit()
            : new X64(plan, registers, OperatingSystem.IsWindows()).Emit();
        if (bytes is null || NativeCodePage.Create(bytes) is not { } page) return null;
        byteCount = bytes.Length;
        return page;
    }

    private static int[]? AssignRegisters(ReadonlyNumberFold plan, uint free)
    {
        var steps = plan.Steps;
        int[] uses = new int[steps.Length];
        uses[plan.Result] = 1;
        for (int i = steps.Length - 1; i >= 0; i--)
        {
            if (uses[i] == 0) continue;
            var step = steps[i];
            if (IsArithmetic(step.Op)) uses[step.Left]++;
            if (IsBinary(step.Op)) uses[step.Right]++;
        }

        int[] registers = new int[steps.Length];
        Array.Fill(registers, -1);
        for (int i = 0; i < steps.Length; i++)
        {
            if (uses[i] == 0) continue;
            var step = steps[i];
            if (step.Op == Op.Accumulator)
            {
                registers[i] = 0;
                continue;
            }
            int left = IsArithmetic(step.Op) ? step.Left : -1;
            int right = IsBinary(step.Op) ? step.Right : -1;
            if (left >= 0) uses[left]--;
            if (right >= 0) uses[right]--;
            int destination = left >= 0 && uses[left] == 0 ? registers[left] : 0;
            if (destination == 0)
            {
                if (free == 0) return null;
                destination = BitOperations.TrailingZeroCount(free);
                free &= ~(1u << destination);
            }
            registers[i] = destination;
            Release(left);
            Release(right);

            void Release(int operand)
            {
                if (operand < 0 || uses[operand] != 0) return;
                int register = registers[operand];
                if (register != 0 && register != destination) free |= 1u << register;
            }
        }
        return registers;
    }

    private static bool IsArithmetic(Op op) => op == Op.Negate || IsBinary(op);

    private static bool IsBinary(Op op) => op is Op.Add or Op.Subtract or Op.Multiply or Op.Divide;
}
