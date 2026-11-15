// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineVectorOps.cpp visitInsertValueInst and aggregate reconstruction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private enum AggregateSourceKind : byte { NotFound, Found, Mismatch }
        private readonly record struct AggregateSource(AggregateSourceKind Kind, LlvmValue? Value = null);

        private LlvmInstruction? VisitInsertValueInst(LlvmInsertValueInstruction instruction)
        {
            if (LlvmInstructionSimplify.SimplifyInstructionWithOperands(instruction,
                [instruction.AggregateOperand, instruction.InsertedValueOperand], Query(instruction)) is { } simplified)
                return ReplaceInstUsesWith(instruction, simplified);
            LlvmValue value = instruction;
            for (int depth = 0; value.HasOneUse && depth < 10; ++depth)
            {
                if (value.Users.First() is not LlvmInsertValueInstruction user || user.AggregateOperand != value) break;
                if (user.Indices.SequenceEqual(instruction.Indices))
                {
                    Count("aggregate.insert.overwritten");
                    return ReplaceInstUsesWith(instruction, instruction.AggregateOperand);
                }
                value = user;
            }
            return FoldAggregateConstructionIntoAggregateReuse(instruction);
        }

        private LlvmInstruction? FoldAggregateConstructionIntoAggregateReuse(LlvmInsertValueInstruction original)
        {
            LlvmType type = original.Type;
            uint count = type switch
            {
                LlvmStructType structure => (uint)structure.Elements.Count,
                LlvmArrayType array => unchecked((uint)array.NumElements),
                _ => throw new InvalidOperationException("Insertvalue requires an aggregate type.")
            };
            Debug.Assert(count > 0);
            if (count > 2) return null;
            LlvmInstruction?[] elements = new LlvmInstruction?[count];
            LlvmInsertValueInstruction? current = original;
            for (uint depth = 0; depth < 2 * count && current is not null && elements.Any(static element => element is null); ++depth)
            {
                Count("aggregate.reconstruction.inserts-examined");
                if (current.InsertedValueOperand is not LlvmInstruction element || current.Indices.Count != 1) return null;
                elements[current.Indices[0]] ??= element;
                current = current.AggregateOperand as LlvmInsertValueInstruction;
            }
            if (elements.Any(static element => element is null)) return null;
            bool elementDefinedInUseBlock = false;
            AggregateSource source = FindCommonSourceAggregate(elements, type, null, null, ref elementDefinedInUseBlock);
            if (source.Kind != AggregateSourceKind.NotFound)
            {
                if (source.Kind == AggregateSourceKind.Mismatch) return null;
                Count("aggregate.reconstructions.simplified");
                return ReplaceInstUsesWith(original, source.Value!);
            }
            LlvmBasicBlock? useBlock = null;
            foreach (LlvmInstruction? element in elements)
            {
                if (useBlock is null) useBlock = element!.Parent;
                else if (useBlock != element!.Parent) return null;
            }
            if (useBlock is null) return null;
            List<LlvmBasicBlock> predecessors = [];
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(useBlock))
            {
                if (predecessors.Count >= 64) { Count("aggregate.reconstruction.predecessor-limit"); return null; }
                predecessors.Add(predecessor);
            }
            if (predecessors.Count == 0) return null;
            List<(LlvmBasicBlock Block, LlvmValue? Value)> aggregates = [];
            Dictionary<LlvmBasicBlock, int> indices = [];
            bool foundSource = false;
            foreach (LlvmBasicBlock predecessor in predecessors)
            {
                if (indices.ContainsKey(predecessor)) continue;
                indices.Add(predecessor, aggregates.Count);
                source = FindCommonSourceAggregate(elements, type, useBlock, predecessor, ref elementDefinedInUseBlock);
                if (source.Kind == AggregateSourceKind.Found)
                {
                    foundSource = true;
                    aggregates.Add((predecessor, source.Value));
                }
                else
                {
                    if (predecessor.GetTerminator() is not LlvmUncondBrInstruction) return null;
                    aggregates.Add((predecessor, null));
                }
            }
            if (!foundSource) return null;
            foreach (var aggregate in aggregates)
            {
                if (aggregate.Value is not null) continue;
                if (elementDefinedInUseBlock || useBlock != original.Parent) return null;
                if (elements.All(element => element!.DoPHITranslation(useBlock, aggregate.Block) is LlvmConstant)) return null;
            }
            ConstructMissingPredecessorAggregates(original, elements, useBlock, aggregates);
            LlvmTransformBuilder.InsertionPoint insertion = Builder.SaveInsertionPoint();
            try
            {
                Builder.SetInsertPoint(FirstNonPhi(useBlock));
                LlvmPhiInstruction phi = Builder.CreatePhi(type, predecessors.Count, original.Name + ".merged");
                phi.LogicalType = original.LogicalType;
                foreach (LlvmBasicBlock predecessor in predecessors)
                {
                    phi.AddIncoming(aggregates[indices[predecessor]].Value!, predecessor);
                    phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = original.LogicalType;
                }
                Count("aggregate.reconstructions.simplified");
                return ReplaceInstUsesWith(original, phi);
            }
            finally { Builder.RestoreInsertionPoint(insertion); }
        }

        private void ConstructMissingPredecessorAggregates(LlvmInsertValueInstruction original,
            IReadOnlyList<LlvmInstruction?> elements, LlvmBasicBlock useBlock,
            List<(LlvmBasicBlock Block, LlvmValue? Value)> aggregates)
        {
            for (int index = 0; index < aggregates.Count; ++index)
            {
                var aggregate = aggregates[index];
                if (aggregate.Value is not null) continue;
                Builder.SetInsertPoint(aggregate.Block.GetTerminator());
                LlvmValue value = LlvmPoisonValue.Get(original.Type);
                for (uint field = 0; field < elements.Count; ++field)
                {
                    LlvmValue element = elements[(int)field]!.DoPHITranslation(useBlock, aggregate.Block);
                    value = Builder.CreateInsertValue(value, element, field);
                    if (value is LlvmInstruction instruction) instruction.LogicalType = original.LogicalType;
                }
                aggregates[index] = (aggregate.Block, value);
                Count("aggregate.reconstruction.predecessor-aggregates-created");
            }
        }

        private AggregateSource FindCommonSourceAggregate(IReadOnlyList<LlvmInstruction?> elements,
            LlvmType aggregateType, LlvmBasicBlock? useBlock, LlvmBasicBlock? predecessor, ref bool elementDefinedInUseBlock)
        {
            AggregateSource common = default;
            for (uint index = 0; index < elements.Count; ++index)
            {
                AggregateSource source = FindSourceAggregate(elements[(int)index]!, index, aggregateType,
                    useBlock, predecessor, ref elementDefinedInUseBlock);
                if (source.Kind != AggregateSourceKind.Found) return source;
                if (common.Kind == AggregateSourceKind.NotFound) common = source;
                else if (common.Value != source.Value) return new(AggregateSourceKind.Mismatch);
            }
            Debug.Assert(common.Kind == AggregateSourceKind.Found);
            return common;
        }

        private AggregateSource FindSourceAggregate(LlvmInstruction element, uint index, LlvmType aggregateType,
            LlvmBasicBlock? useBlock, LlvmBasicBlock? predecessor, ref bool elementDefinedInUseBlock)
        {
            Count("aggregate.reconstruction.sources-examined");
            LlvmInstruction? translated = element;
            if (useBlock is not null && predecessor is not null)
            {
                translated = element.DoPHITranslation(useBlock, predecessor) as LlvmInstruction;
                if (translated?.Parent == useBlock) elementDefinedInUseBlock = true;
            }
            if (translated is not LlvmExtractValueInstruction extract) return default;
            if (extract.AggregateOperand.Type != aggregateType || extract.Indices.Count != 1 || extract.Indices[0] != index)
                return new(AggregateSourceKind.Mismatch);
            return new(AggregateSourceKind.Found, extract.AggregateOperand);
        }
    }
}
