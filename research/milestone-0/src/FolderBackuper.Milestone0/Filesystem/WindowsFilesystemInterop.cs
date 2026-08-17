using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderBackuper.Milestone0.Filesystem;

public sealed record FilesystemIdentity(ulong VolumeSerialNumber, string FileId, string Api)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileId} ({Api})";
}

public static class WindowsFilesystemInterop
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;

    public static SafeFileHandle OpenMetadataHandle(string path)
    {
        var handle = CreateFile(
            Path.GetFullPath(path),
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open metadata handle for '{Path.GetFileName(path)}'.");
        }

        return handle;
    }

    public static FilesystemIdentity GetIdentity(string path)
    {
        using var handle = OpenMetadataHandle(path);
        var info = new FileIdInfo { FileId = new byte[16] };
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return new FilesystemIdentity(info.VolumeSerialNumber, Convert.ToHexString(info.FileId), "FileIdInfo");
        }

        var extendedError = Marshal.GetLastWin32Error();
        if (extendedError is not 1 and not 50 and not 87)
        {
            throw new Win32Exception(extendedError, "Could not obtain filesystem identity.");
        }

        if (!GetFileInformationByHandle(handle, out var basicInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain fallback filesystem identity.");
        }

        var fileId = ((ulong)basicInfo.FileIndexHigh << 32) | basicInfo.FileIndexLow;
        return new FilesystemIdentity(basicInfo.VolumeSerialNumber, fileId.ToString("X16"), "ByHandleFileInformation");
    }

    public static string GetFinalPath(string path)
    {
        using var handle = OpenMetadataHandle(path);
        var required = GetFinalPathNameByHandle(handle, null, 0, 0);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine final path length.");
        }

        var buffer = new char[required + 1];
        var written = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine final path.");
        }

        return NormalizeFinalPath(new string(buffer, 0, (int)written));
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase) ? path[localPrefix.Length..] : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileIdInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? filePath,
        uint filePathLength,
        uint flags);
}
