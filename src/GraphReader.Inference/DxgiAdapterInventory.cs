// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Runtime.InteropServices;

namespace GraphReader.Inference;

/// <summary>DXGI enumeration index is the DirectML device ID, not a speed ranking.</summary>
public sealed record DxgiAdapter(int Index, string Name, uint VendorId, uint DeviceId, long Luid, bool IsSoftware);
public sealed record DirectMlAdapterSelection(int Index, long Luid);

public interface IDxgiAdapterInventory
{
    IReadOnlyList<DxgiAdapter> Enumerate();
}

public interface IInferenceAdapterSelection
{
    string? DirectMlSelectionIdentity { get; }
    void ValidateDirectMlSelection();
}

public sealed class WindowsDxgiAdapterInventory : IDxgiAdapterInventory
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Adapter1 = new("29038f61-3839-4626-91fd-086879011a05");

    public IReadOnlyList<DxgiAdapter> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Guid iid = Factory1;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IntPtr factory));
        try
        {
            // DirectML device IDs use IDXGIFactory::EnumAdapters, before filtering.
            var enumerate = GetMethod<EnumAdapters>(factory, 7);
            var result = new List<DxgiAdapter>();
            for (uint index = 0; ; index++)
            {
                int hr = enumerate(factory, index, out IntPtr adapter);
                if (hr == DxgiErrorNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Guid adapterIid = Adapter1;
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(adapter, in adapterIid, out IntPtr adapter1));
                    try
                    {
                        Marshal.ThrowExceptionForHR(GetMethod<GetDesc1>(adapter1, 10)(adapter1, out AdapterDescription desc));
                        long luid = unchecked((long)(((ulong)(uint)desc.LuidHigh << 32) | desc.LuidLow));
                        result.Add(new DxgiAdapter(checked((int)index), desc.Description, desc.VendorId,
                            desc.DeviceId, luid, (desc.Flags & 2) != 0));
                    }
                    finally { Marshal.Release(adapter1); }
                }
                finally { Marshal.Release(adapter); }
            }
            return result.AsReadOnly();
        }
        finally { Marshal.Release(factory); }
    }

    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
