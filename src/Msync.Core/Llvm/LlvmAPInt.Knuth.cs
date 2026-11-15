// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/APInt.cpp KnuthDiv.
// D1-D8 retain the upstream base-2^32 normalization, quotient correction,
// multiply/subtract, add-back and remainder unnormalization algorithm.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    private static void KnuthDiv(Span<uint> u, Span<uint> v, Span<uint> q, Span<uint> r, int m, int n)
    {
        Debug.Assert(n > 1);
        const ulong b = 1UL << 32;
        int shift = BitOperations.LeadingZeroCount(v[n - 1]);
        uint vCarry = 0, uCarry = 0;
        unchecked
        {
            if (shift != 0)
            {
                for (int i = 0; i < m + n; ++i)
                {
                    uint temporary = u[i] >> (32 - shift);
                    u[i] = (u[i] << shift) | uCarry;
                    uCarry = temporary;
                }
                for (int i = 0; i < n; ++i)
                {
                    uint temporary = v[i] >> (32 - shift);
                    v[i] = (v[i] << shift) | vCarry;
                    vCarry = temporary;
                }
            }
            u[m + n] = uCarry;
            int j = m;
            do
            {
                ulong dividend = ((ulong)u[j + n] << 32) | u[j + n - 1];
                ulong qp = dividend / v[n - 1], rp = dividend % v[n - 1];
                if (qp == b || qp * v[n - 2] > b * rp + u[j + n - 2])
                {
                    --qp;
                    rp += v[n - 1];
                    if (rp < b && (qp == b || qp * v[n - 2] > b * rp + u[j + n - 2])) --qp;
                }
                long borrow = 0;
                for (int i = 0; i < n; ++i)
                {
                    ulong product = qp * v[i];
                    long subResult = (long)u[j + i] - borrow - (uint)product;
                    u[j + i] = (uint)subResult;
                    // Hi_32 returns uint32_t upstream: subtraction wraps at 32 bits.
                    borrow = (uint)(product >> 32) - (uint)(subResult >> 32);
                }
                bool negative = u[j + n] < borrow;
                u[j + n] -= (uint)borrow;
                q[j] = (uint)qp;
                if (negative)
                {
                    --q[j];
                    bool carry = false;
                    for (int i = 0; i < n; ++i)
                    {
                        uint limit = Math.Min(u[j + i], v[i]);
                        u[j + i] += v[i] + (carry ? 1U : 0);
                        carry = u[j + i] < limit || (carry && u[j + i] == limit);
                    }
                    u[j + n] += carry ? 1U : 0;
                }
            } while (--j >= 0);
            if (r.IsEmpty) return;
            if (shift != 0)
            {
                uint carry = 0;
                for (int i = n - 1; i >= 0; --i)
                {
                    r[i] = (u[i] >> shift) | carry;
                    carry = u[i] << (32 - shift);
                }
            }
            else
                for (int i = n - 1; i >= 0; --i) r[i] = u[i];
        }
    }
}
