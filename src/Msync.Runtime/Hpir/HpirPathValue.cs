using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirPathAnchors(nint LiteralDirectory, nint Home,
    HpirDirectoryAuthority? Authority = null)
{
    internal nint Directory => Authority is { Directory: not 0 } active ? active.Directory : LiteralDirectory;
    internal HpirPathValue Capture(nint text) => new() { Raw = text, Directory = Directory, Home = Home };
    internal HpirPathValue CaptureLiteral(nint text) => new() { Raw = text, Directory = LiteralDirectory, Home = Home };
    internal BindingContext? Binding(BindingContext? fallback) => Authority?.Snapshot ?? fallback;

    internal HpirPathAnchors Fork() => Authority is { } authority
        ? new(Directory, Home, authority.Fork(LiteralDirectory))
        : this;
}

[StructLayout(LayoutKind.Explicit, Size = HpirPathImports.PathBytes)]
internal struct HpirPathValue
{
    [FieldOffset(HpirPathImports.Endpoint)] internal ConnectionKind Endpoint;
    [FieldOffset(HpirPathImports.IsQualified)] internal byte Qualified;
    [FieldOffset(HpirPathImports.Raw)] internal nint Raw;
    [FieldOffset(HpirPathImports.Directory)] internal nint Directory;
    [FieldOffset(HpirPathImports.Home)] internal nint Home;
    [FieldOffset(HpirPathImports.Spelling)] internal nint Spelling;
    [FieldOffset(HpirPathImports.EndpointAddress)] internal nint EndpointAddress;

    internal readonly bool Local => Qualified == 0 || Endpoint == ConnectionKind.Local;
    internal readonly nint NativeText => Local && Spelling != 0 ? Spelling : Raw;

    internal readonly NativePathValue ToHost(bool contextual = false) => new(HpirTextStorage.Read(Raw).ToString(),
        contextual ? String(Directory) : null,
        Qualified == 0 ? null : new NativeConnection(Endpoint, String(EndpointAddress) ?? ""),
        contextual ? String(Home) : null, String(Spelling));

    private static string? String(nint address) => address == 0 ? null : HpirTextStorage.Read(address).ToString();
}
