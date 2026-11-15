// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp AllocaSliceRewriter.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSliceRewriter
    {
        private readonly LlvmDataLayout layout;
        private readonly AllocaSlices slices;
        private readonly State pass;
        private readonly LlvmAllocaInstruction oldAlloca, newAlloca;
        private readonly ulong allocaBegin, allocaEnd;
        private readonly LlvmType allocaType;
        private readonly LlvmIntegerType? integerType;
        private readonly LlvmVectorType? vectorType;
        private readonly LlvmType? elementType;
        private readonly ulong elementSize;
        private readonly OrderedSet<LlvmPhiInstruction> phiUsers;
        private readonly OrderedSet<LlvmSelectInstruction> selectUsers;
        private readonly LlvmTransformBuilder builder;
        private ulong begin, end, newBegin, newEnd, size;
        private bool splittable, split;
        private LlvmUse oldUse = null!;
        private LlvmInstruction oldPointer = null!;
        private string namePrefix = "";

        internal AllocaSliceRewriter(LlvmDataLayout layout, AllocaSlices slices, State pass,
            LlvmAllocaInstruction oldAlloca, LlvmAllocaInstruction newAlloca, LlvmType allocaType,
            ulong allocaBegin, ulong allocaEnd, bool integerPromotable, LlvmVectorType? vectorType,
            OrderedSet<LlvmPhiInstruction> phiUsers, OrderedSet<LlvmSelectInstruction> selectUsers)
        {
            this.layout = layout; this.slices = slices; this.pass = pass;
            this.oldAlloca = oldAlloca; this.newAlloca = newAlloca;
            this.allocaType = allocaType; this.allocaBegin = allocaBegin; this.allocaEnd = allocaEnd;
            integerType = integerPromotable ? LlvmIntegerType.Get(newAlloca.Context,
                (uint)layout.GetTypeSizeInBits(allocaType).GetFixedValue()) : null;
            this.vectorType = vectorType;
            elementType = vectorType?.ElementType;
            elementSize = elementType is null ? 0 : layout.GetTypeSizeInBits(elementType).GetFixedValue() / 8;
            this.phiUsers = phiUsers; this.selectUsers = selectUsers;
            builder = new(layout, pass.Metrics, instruction =>
            {
                if (!instruction.Context.ShouldDiscardValueNames) instruction.SetName(namePrefix + instruction.Name);
            });
            Debug.Assert(integerType is null || vectorType is null);
            if (vectorType is null) return;
            Debug.Assert(layout.GetTypeSizeInBits(elementType!).GetFixedValue() % 8 == 0);
            Count("rewriter.vectorized");
        }

        internal bool Visit(Slice slice)
        {
            begin = slice.BeginOffset; end = slice.EndOffset;
            splittable = slice.IsSplittable;
            split = begin < allocaBegin || end > allocaEnd;
            Debug.Assert(begin < allocaEnd && end > allocaBegin);
            newBegin = Math.Max(begin, allocaBegin); newEnd = Math.Min(end, allocaEnd);
            size = newEnd - newBegin;
            oldUse = slice.Use!; oldPointer = (LlvmInstruction)oldUse.Value!;
            var user = (LlvmInstruction)oldUse.User;
            builder.SetInsertPoint(user); builder.CurrentSource = user.Source;
            if (!user.Context.ShouldDiscardValueNames)
                namePrefix = newAlloca.Name + "." + begin.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            Count("rewriter.slices"); Count(split ? "rewriter.slices.split" : "rewriter.slices.whole");
            bool result = pass.Trace is not { } trace ? VisitInstruction(user) : trace.MeasureDetailed(
                "optimizer.llvm-sroa.rewrite-slice", (Self: this, User: user),
                static state => state.Self.VisitInstruction(state.User), attributes: Attributes(user));
            Debug.Assert(integerType is null && vectorType is null || result);
            return result;
        }

        private bool VisitInstruction(LlvmInstruction instruction) => instruction switch
        {
            LlvmLoadInstruction load => RewriteLoad(load),
            LlvmStoreInstruction store => RewriteStore(store),
            LlvmPhiInstruction phi => RewritePhi(phi),
            LlvmSelectInstruction select => RewriteSelect(select),
            LlvmCallInstruction call => RewriteIntrinsic(call),
            _ => throw new InvalidOperationException($"SROA has no rewrite rule for {instruction.Opcode}.")
        };

        private LlvmValue PointerToNewAlloca(uint addressSpace, bool isVolatile) =>
            !isVolatile || addressSpace == newAlloca.AddressSpace ? newAlloca :
                builder.CreateCast(LlvmOpcode.AddrSpaceCast, newAlloca, LlvmPointerType.Get(newAlloca.Context, addressSpace));

        private LlvmValue SlicePointer(LlvmTransformBuilder insertion, LlvmType pointerType)
        {
            Debug.Assert(split || begin == newBegin);
            string name = OldPointerName();
            return GetAdjustedPtr(insertion, layout, newAlloca,
                new LlvmAPInt(layout.GetIndexTypeSizeInBits(newAlloca.Type), newBegin - allocaBegin), pointerType, name + ".");
        }

        private string OldPointerName()
        {
            string name = oldPointer.Name;
            int prefix = name.LastIndexOf(".sroa.", StringComparison.Ordinal);
            if (prefix >= 0)
            {
                name = name[(prefix + 6)..];
                int index = 0;
                while (index < name.Length && char.IsAsciiDigit(name[index])) ++index;
                if (index < name.Length && name[index] == '.')
                {
                    name = name[(index + 1)..]; index = 0;
                    while (index < name.Length && char.IsAsciiDigit(name[index])) ++index;
                    if (index < name.Length && name[index] == '.') name = name[(index + 1)..];
                }
            }
            int suffix = name.IndexOf(".sroa_", StringComparison.Ordinal);
            return suffix < 0 ? name : name[..suffix];
        }

        private LlvmAlign SliceAlignment() => LlvmAlign.CommonAlignment(newAlloca.Alignment, newBegin - allocaBegin);
        private uint VectorIndex(ulong offset)
        {
            Debug.Assert(vectorType is not null);
            ulong relative = offset - allocaBegin;
            Debug.Assert(relative / elementSize < uint.MaxValue && relative % elementSize == 0);
            return (uint)(relative / elementSize);
        }
        private void DeleteIfTriviallyDead(LlvmValue value)
        {
            var instruction = (LlvmInstruction)value;
            if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, pass.Metrics)) pass.AddDead(instruction);
        }
        private void Count(string key, ulong amount = 1) => pass.Count(key, amount);
        private Dictionary<string, string> Attributes(LlvmInstruction instruction)
        {
            var result = new Dictionary<string, string>
            {
                ["function.name"] = instruction.Parent!.Parent!.Name,
                ["alloca.name"] = slices.Alloca.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString(),
                ["slice.begin"] = begin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["slice.end"] = end.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["partition.begin"] = allocaBegin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["partition.end"] = allocaEnd.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (instruction.Source is { } source)
            {
                result["source.file"] = source.Source;
                result["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }
    }
}
