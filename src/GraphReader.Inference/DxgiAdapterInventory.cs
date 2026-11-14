// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Runtime.InteropServices;

namespace GraphReader.Inference;

/// <summary>DXGI enumeration index is the DirectML device ID, not a speed ranking.</summary>
public sealed record DxgiAdapter(int Index, string Name, uint VendorId, uint DeviceId, long Luid, bool IsSoftware,
    ulong DedicatedVideoMemoryBytes = 0, ulong SharedSystemMemoryBytes = 0);
public sealed record DirectMlAdapterSelection(int Index, long Luid);
public sealed record DxgiLocalMemoryBudget(ulong BudgetBytes, ulong CurrentUsageBytes,
    ulong AvailableForReservationBytes, ulong CurrentReservationBytes);

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
    private static readonly Guid Adapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

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
                            desc.DeviceId, luid, (desc.Flags & 2) != 0,
                            desc.DedicatedVideoMemory.ToUInt64(), desc.SharedSystemMemory.ToUInt64()));
                    }
                    finally { Marshal.Release(adapter1); }
                }
                finally { Marshal.Release(adapter); }
            }
            return result.AsReadOnly();
        }
        finally { Marshal.Release(factory); }
    }

    public static DxgiLocalMemoryBudget? TryReadLocalMemoryBudget(DirectMlAdapterSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!OperatingSystem.IsWindows() || selection.Index < 0) return null;

        IntPtr factory = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        IntPtr adapter1 = IntPtr.Zero;
        IntPtr adapter3 = IntPtr.Zero;
        try
        {
            Guid factoryIid = Factory1;
            if (CreateDXGIFactory1(ref factoryIid, out factory) < 0) return null;

            EnumAdapters enumerate = GetMethod<EnumAdapters>(factory, 7);
            if (enumerate(factory, checked((uint)selection.Index), out adapter) < 0) return null;

            Guid adapter1Iid = Adapter1;
            if (Marshal.QueryInterface(adapter, in adapter1Iid, out adapter1) < 0) return null;
            if (GetMethod<GetDesc1>(adapter1, 10)(adapter1, out AdapterDescription desc) < 0) return null;
            long luid = unchecked((long)(((ulong)(uint)desc.LuidHigh << 32) | desc.LuidLow));
            if (luid != selection.Luid || (desc.Flags & 2) != 0) return null;

            Guid adapter3Iid = Adapter3;
            if (Marshal.QueryInterface(adapter, in adapter3Iid, out adapter3) < 0) return null;
            var query = GetMethod<QueryVideoMemoryInfo>(adapter3, 14);
            if (query(adapter3, 0, 0, out QueryVideoMemoryInfoResult info) < 0) return null;
            return new DxgiLocalMemoryBudget(info.Budget, info.CurrentUsage,
                info.AvailableForReservation, info.CurrentReservation);
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or
                                           EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return null;
        }
        finally
        {
            if (adapter3 != IntPtr.Zero) Marshal.Release(adapter3);
            if (adapter1 != IntPtr.Zero) Marshal.Release(adapter1);
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfo(IntPtr adapter, uint nodeIndex, int memorySegmentGroup,
        out QueryVideoMemoryInfoResult info);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryVideoMemoryInfoResult
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

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
