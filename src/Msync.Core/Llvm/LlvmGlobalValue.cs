// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: GlobalValue.h, GlobalObject.h.
namespace Msync.Core.Llvm;

public enum LlvmLinkage
{
    ExternalLinkage, AvailableExternallyLinkage, LinkOnceAnyLinkage, LinkOnceODRLinkage,
    WeakAnyLinkage, WeakODRLinkage, AppendingLinkage, InternalLinkage, PrivateLinkage,
    ExternalWeakLinkage, CommonLinkage
}
public enum LlvmVisibility { DefaultVisibility, HiddenVisibility, ProtectedVisibility }
public enum LlvmDllStorageClass { DefaultStorageClass, DLLImportStorageClass, DLLExportStorageClass }
public enum LlvmThreadLocalMode { NotThreadLocal, GeneralDynamicTLSModel, LocalDynamicTLSModel, InitialExecTLSModel, LocalExecTLSModel }
public enum LlvmUnnamedAddr { None, Local, Global }

public abstract partial class LlvmGlobalValue : LlvmConstant
{
    private LlvmLinkage linkage;
    private LlvmVisibility visibility;
    public LlvmModule? Parent { get; internal set; }
    public LlvmType ValueType { get; protected set; }
    public uint AddressSpace { get; }
    public LlvmDllStorageClass DllStorageClass { get; set; }
    public LlvmThreadLocalMode ThreadLocalMode { get; set; }
    public LlvmUnnamedAddr UnnamedAddr { get; set; }
    public bool IsDSOLocal { get; set; }
    public bool IsInterposable => Linkage is LlvmLinkage.WeakAnyLinkage or LlvmLinkage.LinkOnceAnyLinkage or
        LlvmLinkage.CommonLinkage or LlvmLinkage.ExternalWeakLinkage ||
        (!IsDSOLocal && Parent is { SemanticInterposition: true });
    public bool HasLocalLinkage => linkage is LlvmLinkage.InternalLinkage or LlvmLinkage.PrivateLinkage;
    public bool IsImplicitDSOLocal => HasLocalLinkage ||
        (visibility != LlvmVisibility.DefaultVisibility && linkage != LlvmLinkage.ExternalWeakLinkage);
    public LlvmLinkage Linkage
    {
        get => linkage;
        set
        {
            linkage = value;
            if (HasLocalLinkage) { visibility = LlvmVisibility.DefaultVisibility; DllStorageClass = LlvmDllStorageClass.DefaultStorageClass; }
            if (IsImplicitDSOLocal) IsDSOLocal = true;
        }
    }
    public LlvmVisibility Visibility
    {
        get => visibility;
        set
        {
            System.Diagnostics.Debug.Assert(!HasLocalLinkage || value == LlvmVisibility.DefaultVisibility);
            visibility = value;
            if (IsImplicitDSOLocal) IsDSOLocal = true;
        }
    }
    protected LlvmGlobalValue(LlvmType type, LlvmValueKind kind, int operands,
        LlvmLinkage linkage, string name, uint addressSpace)
        : base(LlvmPointerType.Get(type.Context, addressSpace), (byte)kind, operands)
    {
        ValueType = type;
        AddressSpace = addressSpace;
        Linkage = linkage;
        SetName(name);
    }
    public abstract bool IsDeclaration { get; }
}

public abstract partial class LlvmGlobalObject : LlvmGlobalValue
{
    public LlvmAlign? Alignment { get; set; }
    protected LlvmGlobalObject(LlvmType type, LlvmValueKind kind, int operands,
        LlvmLinkage linkage, string name, uint addressSpace)
        : base(type, kind, operands, linkage, name, addressSpace) { }
}
