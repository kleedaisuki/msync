// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// SwitchReplacement constructor/replaceSwitch/wouldFitInRegister and table policy.
using System.Diagnostics;
using System.Numerics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private enum SwitchReplacementKind : byte { Invalid, SingleValue, LinearMap, BitMap, LookupTable }

        private sealed class SwitchReplacement
        {
            private readonly Work owner;
            private readonly LlvmType valueType;
            private SwitchReplacementKind kind;
            private LlvmConstant? singleValue, initializer;
            private LlvmConstantInt? bitMap, linearOffset, linearMultiplier;
            private LlvmIntegerType? bitMapElementType;
            private bool linearMapWrapped;
            internal LlvmConstant? DefaultValue { get; }
            internal bool IsLookupTable => kind == SwitchReplacementKind.LookupTable;
            internal bool IsBitMap => kind == SwitchReplacementKind.BitMap;
            internal bool IsValid => kind != SwitchReplacementKind.Invalid;

            internal SwitchReplacement(Work owner, ulong tableSize, LlvmConstantInt offset,
                IReadOnlyList<(LlvmConstantInt Case, LlvmConstant Result)> values, LlvmConstant? defaultValue)
            {
                this.owner = owner;
                DefaultValue = defaultValue;
                singleValue = values[0].Result;
                valueType = singleValue.Type;
                LlvmConstant?[] contents = new LlvmConstant?[checked((int)tableSize)];
                foreach ((LlvmConstantInt indexValue, LlvmConstant result) in values)
                {
                    ulong index = (indexValue.Value - offset.Value).GetLimitedValue();
                    contents[checked((int)index)] = result;
                    if (singleValue is not null && result is not LlvmPoisonValue && result != singleValue)
                        singleValue = singleValue is LlvmPoisonValue ? result : null;
                }
                if ((ulong)values.Count < tableSize)
                {
                    Debug.Assert(defaultValue is not null);
                    for (int i = 0; i < contents.Length; ++i) contents[i] ??= defaultValue;
                    if (defaultValue != singleValue && defaultValue is not LlvmPoisonValue) singleValue = null;
                }
                if (singleValue is not null) { kind = SwitchReplacementKind.SingleValue; return; }
                if (valueType is LlvmIntegerType && TryLinearMap(contents!, values)) { kind = SwitchReplacementKind.LinearMap; return; }
                if (SwitchWouldFitInRegister(owner.Layout, tableSize, valueType) && contents.All(value => value is LlvmConstantInt or LlvmUndefValue))
                {
                    LlvmIntegerType element = (LlvmIntegerType)valueType;
                    LlvmAPInt table = LlvmAPInt.GetZero((uint)(tableSize * element.BitWidth));
                    for (int i = contents.Length; i > 0; --i)
                    {
                        table = table.Shl(element.BitWidth);
                        if (contents[i - 1] is not LlvmUndefValue)
                            table |= ((LlvmConstantInt)contents[i - 1]!).Value.ZExt(table.BitWidth);
                    }
                    bitMap = LlvmConstantInt.Get(valueType.Context, table);
                    bitMapElementType = element;
                    kind = SwitchReplacementKind.BitMap;
                    return;
                }
                if (!contents.All(value => owner.ValidLookupTableConstant(value!))) return;
                LlvmConstant[] actual = contents.Select(value => value!).ToArray();
                if (valueType is LlvmIntegerType integer)
                {
                    LlvmConstantRange range = LlvmConstantRange.GetEmpty(integer.BitWidth);
                    foreach (LlvmConstant value in actual)
                        if (value is not LlvmUndefValue) range = range.UnionWith(new LlvmConstantRange(((LlvmConstantInt)value).Value));
                    uint active = range.IsEmptySet ? 0 : range.GetUnsignedMax().GetActiveBits();
                    uint width = Math.Max(owner.TargetInfo.GetMinimumLookupTableEntryBitWidth(), (uint)SwitchPowerOf2Ceil(active));
                    if (width < integer.BitWidth)
                    {
                        LlvmIntegerType destination = LlvmIntegerType.Get(integer.Context, width);
                        for (int i = 0; i < actual.Length; ++i)
                            actual[i] = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.Trunc, actual[i], destination, owner.Layout, owner.Metrics)!;
                    }
                }
                initializer = LlvmConstantArray.Get(LlvmArrayType.Get(actual[0].Type, tableSize), actual);
                kind = SwitchReplacementKind.LookupTable;
            }

            private bool TryLinearMap(LlvmConstant?[] contents, IReadOnlyList<(LlvmConstantInt Case, LlvmConstant Result)> values)
            {
                LlvmAPInt previous = default, distance = default;
                bool nonMonotonic = false;
                for (int i = 0; i < contents.Length; ++i)
                {
                    LlvmConstantInt? constant = contents[i] as LlvmConstantInt;
                    if (constant is null && contents[i] is LlvmPoisonValue) constant = values[0].Result as LlvmConstantInt;
                    if (constant is null) return false;
                    LlvmAPInt value = constant.Value;
                    if (i != 0)
                    {
                        LlvmAPInt delta = value - previous;
                        if (i == 1) distance = delta;
                        else if (delta != distance) return false;
                        nonMonotonic |= delta.IsStrictlyPositive ? value.Sle(previous) : value.Sgt(previous);
                    }
                    previous = value;
                }
                linearOffset = (LlvmConstantInt)contents[0]!;
                linearMultiplier = LlvmConstantInt.Get(valueType.Context, distance);
                bool mayWrap = true;
                long maximumIndex = contents.LongLength - 1;
                if (SwitchIsIntN(distance.BitWidth, maximumIndex))
                    distance.SMulOverflow(new LlvmAPInt(distance.BitWidth, unchecked((ulong)maximumIndex), isSigned: true), out mayWrap);
                linearMapWrapped = nonMonotonic || mayWrap;
                return true;
            }

            internal LlvmValue ReplaceSwitch(LlvmValue index, LlvmTransformBuilder builder, LlvmFunction function)
            {
                switch (kind)
                {
                    case SwitchReplacementKind.SingleValue:
                        owner.Count("switch.replacement.single_value"); return singleValue!;
                    case SwitchReplacementKind.LinearMap:
                    {
                        owner.Count("switch.replacement.linear_map");
                        LlvmValue result = SwitchIntCast(index, (LlvmIntegerType)linearMultiplier!.Type, builder, "switch.idx.cast");
                        if (!linearMultiplier.IsOne) result = builder.CreateBinary(LlvmOpcode.Mul, result, linearMultiplier, "switch.idx.mult", nsw: !linearMapWrapped);
                        if (!linearOffset!.IsZero) result = builder.CreateBinary(LlvmOpcode.Add, result, linearOffset, "switch.offset", nsw: !linearMapWrapped);
                        return result;
                    }
                    case SwitchReplacementKind.BitMap:
                    {
                        owner.Count("switch.replacement.bitmap");
                        LlvmIntegerType mapType = (LlvmIntegerType)bitMap!.Type;
                        LlvmValue shift = SwitchIntCast(index, mapType, builder, "switch.cast");
                        shift = builder.CreateBinary(LlvmOpcode.Mul, shift, LlvmConstantInt.Get(mapType, bitMapElementType!.BitWidth), "switch.shiftamt", nuw: true, nsw: true);
                        LlvmValue down = builder.CreateBinOp(LlvmOpcode.LShr, bitMap, shift, "switch.downshift");
                        return builder.CreateCast(LlvmOpcode.Trunc, down, bitMapElementType, "switch.masked");
                    }
                    case SwitchReplacementKind.LookupTable:
                    {
                        owner.Count("switch.replacement.lookup_table");
                        LlvmGlobalVariable table = new(function.Parent!, initializer!.Type, true, LlvmLinkage.PrivateLinkage,
                            initializer, "switch.table." + function.Name) { UnnamedAddr = LlvmUnnamedAddr.Global, Alignment = owner.Layout.GetPrefTypeAlign(valueType) };
                        LlvmIntegerType indexType = owner.Layout.GetIndexType(table.Context, table.AddressSpace);
                        LlvmArrayType array = (LlvmArrayType)table.ValueType;
                        if (index.Type != indexType)
                        {
                            uint oldWidth = index.Type.GetIntegerBitWidth();
                            index = SwitchIntCast(index, indexType, builder);
                            if (index is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension)
                                extension.IsNonNegative = oldWidth > 64 || array.NumElements - 1 < (1UL << (int)(oldWidth - 1));
                        }
                        LlvmValue gep = builder.CreateGep(array, table, [LlvmConstantInt.Get(indexType, 0), index], "switch.gep", LlvmGepNoWrapFlags.InBounds);
                        LlvmValue loaded = builder.CreateLoad(array.ElementType, gep, "switch.load");
                        return loaded.Type == valueType ? loaded : builder.CreateCast(LlvmOpcode.ZExt, loaded, valueType, "switch.ext");
                    }
                    default: throw new InvalidOperationException("Cannot use an invalid switch replacement.");
                }
            }
        }

        private static LlvmValue SwitchIntCast(LlvmValue value, LlvmIntegerType destination, LlvmTransformBuilder builder, string name = "") =>
            value.Type == destination ? value : builder.CreateCast(value.Type.GetIntegerBitWidth() < destination.BitWidth ? LlvmOpcode.ZExt : LlvmOpcode.Trunc, value, destination, name);

        private static ulong SwitchPowerOf2Ceil(ulong value) => value <= 1 ? value : BitOperations.RoundUpToPowerOf2(value);
        private static bool SwitchIsIntN(uint width, long value) => width >= 64 || (-(1L << (int)(width - 1)) <= value && value <= (1L << (int)(width - 1)) - 1);

        private static bool SwitchWouldFitInRegister(LlvmDataLayout layout, ulong tableSize, LlvmType elementType)
        {
            if (elementType is not LlvmIntegerType integer || tableSize >= uint.MaxValue / integer.BitWidth) return false;
            return layout.FitsInLegalInteger((uint)(tableSize * integer.BitWidth));
        }

        private bool ValidLookupTableConstant(LlvmConstant constant)
        {
            if (SwitchConstantDependsOnGlobal(constant, true) || SwitchConstantDependsOnGlobal(constant, false)) return false;
            if (constant is not (LlvmConstantFP or LlvmConstantInt or LlvmConstantPointerNull or LlvmGlobalValue or LlvmUndefValue)) return false;
            return !constant.Type.IsScalableTy() && TargetInfo.ShouldBuildLookupTablesForConstant(constant);
        }

        private static bool SwitchConstantDependsOnGlobal(LlvmConstant constant, bool threadDependent)
        {
            if (constant is LlvmGlobalValue global)
                return threadDependent ? global.ThreadLocalMode != LlvmThreadLocalMode.NotThreadLocal : global.DllStorageClass == LlvmDllStorageClass.DLLImportStorageClass;
            foreach (LlvmValue? operand in constant.Operands)
                if (operand is LlvmConstant nested && SwitchConstantDependsOnGlobal(nested, threadDependent)) return true;
            return false;
        }

        private bool IsTypeLegalForSwitchLookup(LlvmType type)
        {
            if (TargetInfo.IsTypeLegal(type)) return true;
            return type is LlvmIntegerType integer && integer.BitWidth >= 8 && BitOperations.IsPow2(integer.BitWidth) && Layout.FitsInLegalInteger(integer.BitWidth);
        }

        private static bool IsSwitchDense(ulong count, ulong range, bool optimizeForSize) =>
            range < ulong.MaxValue / 100 && unchecked(count * 100) >= unchecked(range * (optimizeForSize ? 40UL : 10UL));

        private bool ShouldBuildSwitchLookup(LlvmSwitchInstruction selection, ulong tableSize, IEnumerable<LlvmType> resultTypes)
        {
            if ((ulong)selection.CaseCount > tableSize) return false;
            bool allFit = true, illegal = false;
            foreach (LlvmType type in resultTypes)
            {
                illegal |= !IsTypeLegalForSwitchLookup(type);
                allFit &= SwitchWouldFitInRegister(Layout, tableSize, type);
                if (illegal && !allFit) break;
            }
            if (allFit) return true;
            return !illegal && IsSwitchDense((ulong)selection.CaseCount, tableSize, selection.Parent!.Parent!.HasOptSize);
        }
    }
}
