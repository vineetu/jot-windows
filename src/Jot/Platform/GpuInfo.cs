using System.Runtime.InteropServices;

namespace Jot.Platform;

/// <summary>
/// Identifies DXGI adapter 0 so the ggml Vulkan probe verdict can be cached against the exact
/// hardware+driver it was measured on. LUID is deliberately
/// NOT part of the identity: Windows assigns adapter LUIDs per boot, so a LUID key would force a
/// re-probe every restart. VendorId+DeviceId+Description+UMD driver version only changes when the GPU or
/// its driver actually changes — exactly when a cached verdict must be re-earned.
/// </summary>
public static class GpuInfo
{
    public sealed record Identity(
        string Description, uint VendorId, uint DeviceId,
        ulong DedicatedVideoMemoryBytes, long UmdDriverVersion, bool IsSoftwareAdapter)
    {
        /// <summary>Stable across reboots; changes on GPU swap or driver update.</summary>
        public string CacheKey => $"{VendorId:X4}:{DeviceId:X4}:{Description}:{UmdDriverVersion}";

        /// <summary>Cheap pre-filter for "worth probing / worth fetching the 1.3 GB fp16 model":
        /// a real (non-WARP) adapter with ≥2 GB dedicated VRAM. The probe stays authoritative.</summary>
        public bool LooksCapable => !IsSoftwareAdapter && DedicatedVideoMemoryBytes >= (2UL << 30);
    }

    /// <summary>Adapter 0's identity, or null when DXGI enumeration fails for any reason — never throws.</summary>
    public static Identity? TryGetPrimaryAdapter()
    {
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out IDXGIFactory1? factory) != 0 || factory is null) return null;
            try
            {
                if (factory.EnumAdapters1(0, out IDXGIAdapter1? adapter) != 0 || adapter is null) return null;
                try
                {
                    if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 d) != 0) return null;

                    // UMD driver version via CheckInterfaceSupport(IDXGIDevice) — the documented way to
                    // read the user-mode driver version without creating a D3D device. Failure → 0 (the
                    // identity still works; it just won't distinguish driver updates).
                    Guid dev = IID_IDXGIDevice;
                    if (adapter.CheckInterfaceSupport(ref dev, out long umd) != 0) umd = 0;

                    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
                    return new Identity(
                        d.Description.TrimEnd('\0'),
                        d.VendorId, d.DeviceId,
                        (ulong)d.DedicatedVideoMemory,
                        umd,
                        (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0);
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch
        {
            return null; // no DXGI (unlikely on Win11) / marshalling surprise — treat as "no GPU info"
        }
    }

    /// <summary>Formats an LARGE_INTEGER UMD version as the familiar dotted driver version.</summary>
    public static string FormatDriverVersion(long umd) =>
        $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";

    // DXGI COM interop — declaration order IS the vtable order; stubs exist only to hold slots.

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIFactory
        [PreserveSig] int _EnumAdapters();
        [PreserveSig] int _MakeWindowAssociation();
        [PreserveSig] int _GetWindowAssociation();
        [PreserveSig] int _CreateSwapChain();
        [PreserveSig] int _CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
        [PreserveSig] int _IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int _SetPrivateData();
        [PreserveSig] int _SetPrivateDataInterface();
        [PreserveSig] int _GetPrivateData();
        [PreserveSig] int _GetParent();
        // IDXGIAdapter
        [PreserveSig] int _EnumOutputs();
        [PreserveSig] int _GetDesc();
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
