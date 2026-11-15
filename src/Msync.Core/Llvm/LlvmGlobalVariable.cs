// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: GlobalVariable.h, Globals.cpp.
namespace Msync.Core.Llvm;

public sealed partial class LlvmGlobalVariable : LlvmGlobalObject
{
    public bool IsConstant { get; set; }
    public bool IsExternallyInitialized { get; set; }
    public bool HasInitializer => OperandCount != 0;
    public bool HasDefinitiveInitializer => HasInitializer && !IsInterposable && !IsExternallyInitialized;
    public override bool IsDeclaration => !HasInitializer;
    public LlvmConstant? Initializer
    {
        get => HasInitializer ? (LlvmConstant?)GetOperand(0) : null;
        set
        {
            if (value is null)
            {
                if (!HasInitializer) return;
                SetOperand(0, null);
                SetOperandCount(0);
                return;
            }
            System.Diagnostics.Debug.Assert(value.Type == ValueType);
            if (!HasInitializer)
            {
                if (OperandCapacity == 0) GrowOperands(1);
                SetOperandCount(1);
            }
            SetOperand(0, value);
        }
    }
    public LlvmGlobalVariable(LlvmType type, bool isConstant, LlvmLinkage linkage,
        LlvmConstant? initializer = null, string name = "",
        LlvmThreadLocalMode threadLocalMode = LlvmThreadLocalMode.NotThreadLocal,
        uint addressSpace = 0, bool isExternallyInitialized = false)
        : base(type, LlvmValueKind.GlobalVariableVal, initializer is null ? 0 : 1, linkage, name, addressSpace)
    {
        System.Diagnostics.Debug.Assert(!type.IsFunctionTy());
        IsConstant = isConstant;
        IsExternallyInitialized = isExternallyInitialized;
        ThreadLocalMode = threadLocalMode;
        if (initializer is not null)
        {
            System.Diagnostics.Debug.Assert(initializer.Type == type);
            SetOperand(0, initializer);
        }
    }
    public LlvmGlobalVariable(LlvmModule module, LlvmType type, bool isConstant,
        LlvmLinkage linkage, LlvmConstant? initializer = null, string name = "",
        LlvmGlobalVariable? before = null,
        LlvmThreadLocalMode threadLocalMode = LlvmThreadLocalMode.NotThreadLocal,
        uint? addressSpace = null, bool isExternallyInitialized = false)
        : this(type, isConstant, linkage, initializer, name, threadLocalMode,
            addressSpace ?? module.DataLayout.GetDefaultGlobalsAddressSpace(), isExternallyInitialized)
    {
        if (before is not null) before.Parent!.InsertGlobalVariable(this, before);
        else module.InsertGlobalVariable(this);
    }
    public void ReplaceInitializer(LlvmConstant initializer)
    {
        ValueType = initializer.Type;
        Initializer = initializer;
    }
    public void RemoveFromParent() => Parent!.RemoveGlobalVariable(this);
}
