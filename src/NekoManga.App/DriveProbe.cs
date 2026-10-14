using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NekoManga.App;

/// <summary>드라이브 SSD/HDD·NVMe 판별 — Win32 IOCTL_STORAGE_QUERY_PROPERTY 직접 호출.
/// WMI(System.Management.dll)는 외부 어셈블리라 로컬 검증 배포(DLL 3개 덮어쓰기)에서 빠지면
/// 구성요소 창을 열다 앱이 죽는다(실사고) — P/Invoke는 추가 파일이 전혀 필요 없다.</summary>
internal static class DriveProbe
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageAdapterProperty = 1;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;   // 0 = PropertyStandardQuery
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    // STORAGE_ADAPTER_DESCRIPTOR — BusType은 DWORD 5개 + BOOLEAN 4개 뒤(오프셋 24)
    [StructLayout(LayoutKind.Sequential)]
    private struct StorageAdapterDescriptor
    {
        public uint Version;
        public uint Size;
        public uint MaximumTransferLength;
        public uint MaximumPhysicalPages;
        public uint AlignmentMask;
        public byte AdapterUsesPio;
        public byte AdapterScansDown;
        public byte CommandQueueing;
        public byte AcceleratedTransfer;
        public byte BusType;         // 17 = NVMe
        public ushort BusMajorVersion;
        public ushort BusMinorVersion;
        public byte SrbType;
        public byte AddressType;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        ref StoragePropertyQuery inBuffer, int inSize,
        out DeviceSeekPenaltyDescriptor outBuffer, int outSize,
        out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        ref StoragePropertyQuery inBuffer, int inSize,
        out StorageAdapterDescriptor outBuffer, int outSize,
        out int bytesReturned, IntPtr overlapped);

    /// <summary>쿼리 전용 볼륨 핸들 (desiredAccess 0 — 관리자 권한 불필요).</summary>
    public static SafeFileHandle OpenVolume(char letter) =>
        CreateFileW($@"\\.\{letter}:", 0, /*FILE_SHARE_READ|WRITE*/ 3, IntPtr.Zero, /*OPEN_EXISTING*/ 3, 0, IntPtr.Zero);

    /// <summary>true=탐색 지연 없음(SSD), false=있음(HDD), null=조회 실패.</summary>
    public static bool? HasNoSeekPenalty(SafeFileHandle volume)
    {
        var q = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty };
        return DeviceIoControl(volume, IoctlStorageQueryProperty,
                   ref q, Marshal.SizeOf<StoragePropertyQuery>(),
                   out DeviceSeekPenaltyDescriptor d, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out _, IntPtr.Zero)
               ? d.IncursSeekPenalty == 0
               : null;
    }

    /// <summary>STORAGE_BUS_TYPE (17=NVMe). 조회 실패는 0.</summary>
    public static int BusType(SafeFileHandle volume)
    {
        var q = new StoragePropertyQuery { PropertyId = StorageAdapterProperty };
        return DeviceIoControl(volume, IoctlStorageQueryProperty,
                   ref q, Marshal.SizeOf<StoragePropertyQuery>(),
                   out StorageAdapterDescriptor d, Marshal.SizeOf<StorageAdapterDescriptor>(), out _, IntPtr.Zero)
               ? d.BusType
               : 0;
    }
}
