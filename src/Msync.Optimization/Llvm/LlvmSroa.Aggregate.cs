// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp AggLoadStoreRewriter.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private sealed partial class AggLoadStoreRewriter(LlvmDataLayout layout, LlvmTransformBuilder builder,
        State? diagnostics = null)
    {
        private readonly List<LlvmUse> queue = [];
        private readonly HashSet<LlvmUser> visited = [];
        private LlvmUse currentUse = null!;

        internal bool Rewrite(LlvmInstruction pointer)
        {
            if (diagnostics?.Trace is not { } trace) return RewriteCore(pointer);
            return trace.MeasureDetailed("optimizer.llvm-sroa.aggregate-accesses", (Self: this, Pointer: pointer),
                static state => state.Self.RewriteCore(state.Pointer),
                attributes: AggregateAttributes(pointer));
        }

        private bool RewriteCore(LlvmInstruction pointer)
        {
            EnqueueUsers(pointer);
            bool changed = false;
            while (queue.Count != 0)
            {
                currentUse = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                Count("aggregate.users.visited");
                changed |= Visit((LlvmInstruction)currentUse.User);
            }
            return changed;
        }

        private bool Visit(LlvmInstruction instruction) => instruction switch
        {
            LlvmLoadInstruction load => RewriteLoad(load),
            LlvmStoreInstruction store => RewriteStore(store),
            LlvmGetElementPtrInstruction gep => RewriteGep(gep),
            LlvmPhiInstruction or LlvmSelectInstruction => EnqueuePointerUsers(instruction),
            LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast } => EnqueuePointerUsers(instruction),
            _ => false
        };

        private bool EnqueuePointerUsers(LlvmInstruction instruction)
        { EnqueueUsers(instruction); return false; }

        private void EnqueueUsers(LlvmInstruction instruction)
        {
            foreach (LlvmUse use in instruction.Uses)
            {
                if (!visited.Add(use.User)) { Count("aggregate.users.duplicate"); continue; }
                queue.Add(use);
                Count("aggregate.users.queued");
            }
        }

        private bool RewriteLoad(LlvmLoadInstruction load)
        {
            Debug.Assert(load.PointerOperand == currentUse.Value);
            if (!load.IsSimple || load.Type.IsSingleValueType()) return false;
            List<LlvmCallInstruction> fakeUses = [];
            foreach (LlvmUser user in load.Users)
                if (user is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } call &&
                    kind == LlvmIntrinsicKind.FakeUse) fakeUses.Add(call);
            List<LlvmValue> components = [];
            builder.SetInsertPoint(load);
            LlvmValue aggregate = LlvmPoisonValue.Get(load.Type);
            SplitAggregate(load.Type, ref aggregate, load.PointerOperand, load.Type, load.Alignment,
                load.Name + ".fca", store: false, components);
            foreach (LlvmCallInstruction fakeUse in fakeUses)
            {
                builder.SetInsertPoint(fakeUse);
                foreach (LlvmValue component in components) builder.CreateIntrinsic(LlvmIntrinsicKind.FakeUse, [component]);
                fakeUse.EraseFromParent();
                Count("aggregate.fake-uses.split");
            }
            visited.Remove(load);
            load.ReplaceAllUsesWith(aggregate);
            load.EraseFromParent();
            Count("aggregate.loads.split");
            return true;
        }

        private bool RewriteStore(LlvmStoreInstruction store)
        {
            if (!store.IsSimple || store.PointerOperand != currentUse.Value) return false;
            LlvmValue aggregate = store.ValueOperand;
            if (aggregate.Type.IsSingleValueType()) return false;
            builder.SetInsertPoint(store);
            SplitAggregate(aggregate.Type, ref aggregate, store.PointerOperand, aggregate.Type, store.Alignment,
                aggregate.Name + ".fca", store: true);
            visited.Remove(store);
            store.EraseFromParent();
            Count("aggregate.stores.split");
            return true;
        }

        private void SplitAggregate(LlvmType type, ref LlvmValue aggregate, LlvmValue pointer, LlvmType baseType,
            LlvmAlign alignment, string name, bool store, List<LlvmValue>? components = null)
        {
            List<uint> indices = [];
            List<LlvmValue> gepIndices = [LlvmConstantInt.Get(LlvmType.GetInt32Ty(type.Context), 0)];
            EmitSplitOps(type, ref aggregate, pointer, baseType, alignment, name, store, indices, gepIndices, components);
        }

        private void EmitSplitOps(LlvmType type, ref LlvmValue aggregate, LlvmValue pointer, LlvmType baseType,
            LlvmAlign alignment, string name, bool store, List<uint> indices, List<LlvmValue> gepIndices, List<LlvmValue>? components)
        {
            if (type.IsSingleValueType())
            {
                uint offset = unchecked((uint)layout.GetIndexedOffsetInType(baseType, gepIndices));
                EmitAggregateLeaf(type, ref aggregate, pointer, baseType, LlvmAlign.CommonAlignment(alignment, offset),
                    name, store, indices, gepIndices, components);
                return;
            }
            uint count = type switch
            {
                LlvmArrayType array => unchecked((uint)array.NumElements),
                LlvmStructType structure => (uint)structure.Elements.Count,
                _ => throw new InvalidOperationException("Only arrays and structs are aggregate loadable types.")
            };
            for (uint index = 0; index < count; ++index)
            {
                indices.Add(index);
                gepIndices.Add(LlvmConstantInt.Get(LlvmType.GetInt32Ty(type.Context), index));
                LlvmType element = type is LlvmArrayType array ? array.ElementType : ((LlvmStructType)type).Elements[(int)index];
                EmitSplitOps(element, ref aggregate, pointer, baseType, alignment, name + "." + index,
                    store, indices, gepIndices, components);
                gepIndices.RemoveAt(gepIndices.Count - 1);
                indices.RemoveAt(indices.Count - 1);
            }
        }

        private void EmitAggregateLeaf(LlvmType type, ref LlvmValue aggregate, LlvmValue pointer, LlvmType baseType,
            LlvmAlign alignment, string name, bool store, IReadOnlyList<uint> indices, IReadOnlyList<LlvmValue> gepIndices, List<LlvmValue>? components)
        {
            if (store)
            {
                LlvmValue value = builder.CreateExtractValue(aggregate, indices, name + ".extract");
                LlvmValue address = builder.CreateInBoundsGep(baseType, pointer, gepIndices, name + ".gep");
                builder.CreateAlignedStore(value, address, alignment);
                Count("aggregate.scalar-stores.created");
                return;
            }
            LlvmValue gep = builder.CreateInBoundsGep(baseType, pointer, gepIndices, name + ".gep");
            LlvmLoadInstruction load = builder.CreateAlignedLoad(type, gep, alignment, name + ".load");
            components?.Add(load);
            aggregate = builder.CreateInsertValue(aggregate, load, indices, name + ".insert");
            Count("aggregate.scalar-loads.created");
        }

        private void Count(string name, ulong amount = 1) => diagnostics?.Count(name, amount);

        private static Dictionary<string, string> AggregateAttributes(LlvmInstruction pointer)
        {
            Dictionary<string, string> attributes = new()
            {
                ["function.name"] = pointer.Parent!.Parent!.Name,
                ["instruction.name"] = pointer.Name,
                ["instruction.opcode"] = pointer.Opcode.ToString()
            };
            if (pointer.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
