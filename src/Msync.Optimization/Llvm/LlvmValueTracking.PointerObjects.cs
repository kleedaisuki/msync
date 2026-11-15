// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// getUnderlyingObject and getUnderlyingObjectAggressive.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static LlvmValue GetUnderlyingObject(LlvmValue value, uint maxLookup = 10,
        bool mustPreserveProvenance = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.underlying_object.queries");
        for (uint count = 0; maxLookup == 0 || count < maxLookup; count++)
        {
            Count(metrics, "value_tracking.underlying_object.lookups");
            if (LlvmGepOperator.TryGet(value, out var gep))
            {
                if (!gep.PointerOperand.Type.IsPointerTy()) return value;
                value = gep.PointerOperand;
            }
            else if (value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast } cast)
            {
                if (!cast.Operand.Type.IsPointerTy()) return value;
                value = cast.Operand;
            }
            else if (value is LlvmConstantExpr { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast } expression)
            {
                if (!expression.GetConstantOperand(0).Type.IsPointerTy()) return value;
                value = expression.GetConstantOperand(0);
            }
            else if (value is LlvmPhiInstruction { IncomingCount: 1 } phi)
                value = phi.GetIncomingValue(0);
            else
            {
                // GlobalAlias and call returned-argument/capture/intrinsic attributes are absent.
                Count(metrics, "value_tracking.underlying_object.base");
                return value;
            }
        }
        Count(metrics, "value_tracking.underlying_object.lookup_limit");
        return value;
    }

    public static LlvmValue GetUnderlyingObjectAggressive(LlvmValue value,
        bool mustPreserveProvenance = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.underlying_object.aggressive_queries");
        var visited = new HashSet<LlvmValue>(ReferenceEqualityComparer.Instance);
        var work = new Stack<LlvmValue>(); work.Push(value);
        LlvmValue? common = null;
        bool first = true;
        var firstObject = GetUnderlyingObject(value, 10, mustPreserveProvenance, metrics);
        do
        {
            var current = work.Pop();
            current = first ? firstObject : GetUnderlyingObject(current, 10, mustPreserveProvenance, metrics);
            first = false;
            if (!visited.Add(current)) continue;
            if (visited.Count == 8)
            { Count(metrics, "value_tracking.underlying_object.aggressive_visit_limit"); return firstObject; }
            if (current is LlvmSelectInstruction select)
            { work.Push(select.TrueValue); work.Push(select.FalseValue); continue; }
            if (current is LlvmPhiInstruction phi)
            {
                for (int n = 0; n < phi.IncomingCount; n++) work.Push(phi.GetIncomingValue(n));
                continue;
            }
            if (common is null) common = current;
            else if (!ReferenceEquals(common, current))
            { Count(metrics, "value_tracking.underlying_object.aggressive_distinct"); return firstObject; }
        } while (work.Count != 0);
        Count(metrics, "value_tracking.underlying_object.aggressive_common");
        return common ?? firstObject;
    }
}
