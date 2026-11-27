using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmFunction, ProjectionArm[]> projections = [];
        internal readonly record struct ProjectionArm(int Layout, int Offset);

        internal bool IsUniformProjection(LlvmFunction? target) => target is not null
            && graph.ValueOperations.TryGetValue(target, out var binding)
            && binding.Operation == LlvmRuntimeValueOperation.ProjectAddress;

        internal ProjectionArm[] Projection(LlvmFunction target, SourceSpan? source)
        {
            if (projections.TryGetValue(target, out var known)) return known;
            var cases = graph.ValueOperations[target].Projection!.Cases;
            if (cases.Count == 0) throw new MsyncException("HPIR projection has no physical target", source);
            var arms = new ProjectionArm[cases.Count];
            for (int index = 0; index < cases.Count; index++)
            {
                var candidate = cases[index];
                if (!structs.TryGetValue(candidate.Layout, out var layout))
                    throw new MsyncException("HPIR projection requires a concrete producer layout", source);
                arms[index] = new(candidate.Layout, FieldOffset(layout, (uint)candidate.Field));
            }
            projections.Add(target, arms);
            return arms;
        }

        internal string? MemberFailure(LlvmFunction target) => graph.ValueOperations.TryGetValue(target, out var binding)
            && binding.Operation == LlvmRuntimeValueOperation.FailMember ? binding.Symbol : null;

        internal int ProjectionLayoutImport(int logical, SourceSpan? source) => LayoutImport(logical, source);
    }

    private sealed partial class Body
    {
        private int uniformProjectionCount, finiteProjectionCount;

        private bool TryUniformProjection(LlvmCallInstruction call)
        {
            LlvmFunction? target = call.CalledFunction;
            if (target is not null && geometry.MemberFailure(target) is { } message)
            {
                EmitFailure(call, message);
                return true;
            }
            if (!geometry.IsUniformProjection(target)) return false;
            var arms = geometry.Projection(target!, call.Source);
            int parent = Location(call.GetArgument(0));
            int offset = arms[0].Offset;
            if (arms.All(arm => arm.Offset == offset))
            {
                if (offset == 0) Alias(call, parent);
                else Add(new(HpirOp.OffsetPointer, Location(call), parent, Immediate: offset), call.Source);
                ++uniformProjectionCount;
            }
            else ProjectVariant(call, parent, arms);
            return true;
        }

        private void ProjectVariant(LlvmCallInstruction call, int parent, Geometry.ProjectionArm[] arms)
        {
            int condition = Reserve(1, 1), destination = Location(call);
            int[] joins = new int[arms.Length];
            for (int index = 0; index < arms.Length; ++index)
            {
                var arm = arms[index];
                EmitCall(call, ~geometry.ProjectionLayoutImport(arm.Layout, call.Source), condition, [parent], safepoint: false);
                int branch = code.Count;
                Add(new(HpirOp.Branch, A: condition, B: branch + 1), call.Source);
                if (arm.Offset == 0) Copy(HpirWidth.U64, destination, parent, call.Source);
                else Add(new(HpirOp.OffsetPointer, destination, parent, Immediate: arm.Offset), call.Source);
                joins[index] = code.Count;
                Add(new(HpirOp.Jump), call.Source);
                code[branch] = code[branch] with { D = code.Count };
            }
            EmitFailure(call, "Object allocation is outside its structural view.");
            foreach (int join in joins) code[join] = code[join] with { A = code.Count };
            ++finiteProjectionCount;
        }
    }
}
