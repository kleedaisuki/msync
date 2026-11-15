// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/AliasAnalysis.h and Support/ModRef.h.
using System.Diagnostics;
namespace Msync.Optimization.Llvm;

public enum LlvmAliasKind : byte { NoAlias, MayAlias, PartialAlias, MustAlias }
public readonly record struct LlvmAliasResult(LlvmAliasKind Kind, bool HasOffset = false, int Offset = 0)
{
    public static LlvmAliasResult NoAlias => new(LlvmAliasKind.NoAlias);
    public static LlvmAliasResult MayAlias => new(LlvmAliasKind.MayAlias);
    public static LlvmAliasResult PartialAlias => new(LlvmAliasKind.PartialAlias);
    public static LlvmAliasResult MustAlias => new(LlvmAliasKind.MustAlias);
    public int GetOffset() { Debug.Assert(HasOffset); return Offset; }
    public LlvmAliasResult WithOffset(int offset) => offset >= -(1 << 22) && offset < (1 << 22)
        ? new(Kind, true, offset) : this;
    public LlvmAliasResult Swap(bool swap = true) => swap && HasOffset ? WithOffset(-Offset) : this;
    public static implicit operator LlvmAliasResult(LlvmAliasKind kind) => new(kind);
    public static bool operator ==(LlvmAliasResult left, LlvmAliasKind right) => left.Kind == right;
    public static bool operator !=(LlvmAliasResult left, LlvmAliasKind right) => left.Kind != right;
}
[Flags]
public enum LlvmModRefInfo : byte { NoModRef = 0, Ref = 1, Mod = 2, ModRef = Ref | Mod }
public static class LlvmModRef
{
    public static bool IsNoModRef(this LlvmModRefInfo info) => info == LlvmModRefInfo.NoModRef;
    public static bool IsModOrRefSet(this LlvmModRefInfo info) => info != LlvmModRefInfo.NoModRef;
    public static bool IsModAndRefSet(this LlvmModRefInfo info) => info == LlvmModRefInfo.ModRef;
    public static bool IsModSet(this LlvmModRefInfo info) => (info & LlvmModRefInfo.Mod) != 0;
    public static bool IsRefSet(this LlvmModRefInfo info) => (info & LlvmModRefInfo.Ref) != 0;
}
