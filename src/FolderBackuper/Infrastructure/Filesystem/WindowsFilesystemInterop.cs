using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderBackuper.Infrastructure.Filesystem;

public sealed record FilesystemIdentity(ulong VolumeSerialNumber, string FileId, bool UsedFallback)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileId}";
}

public sealed record FilesystemMetadata(
    string FinalPath,
    FilesystemIdentity Identity,
    FileAttributes Attributes,
    uint ReparseTag);

public static class WindowsFilesystemInterop
{
    private const uint FileReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    public static SafeFileHandle OpenMetadataHandle(string absolutePath)
        => OpenMetadataHandle(absolutePath, 0);

    public static string GetFinalPath(string absolutePath)
    {
        using var handle = OpenMetadataHandle(absolutePath);
        return GetFinalPath(handle);
    }

    public static FilesystemIdentity GetIdentity(string absolutePath)
    {
        using var handle = OpenMetadataHandle(absolutePath);
        return GetIdentity(handle);
    }

    public static FilesystemMetadata GetMetadata(string absolutePath)
    {
        using var targetHandle = OpenMetadataHandle(absolutePath);
        using var entryHandle = OpenMetadataHandle(absolutePath, OpenReparsePoint);
        if (!GetFileInformationByHandleEx(entryHandle, 9, out FileAttributeTagInfo tagInfo, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain filesystem attribute and reparse data.");
        }
        return new(GetFinalPath(targetHandle), GetIdentity(targetHandle), (FileAttributes)tagInfo.FileAttributes, tagInfo.ReparseTag);
    }

    public static SafeFileHandle OpenReadDeleteHandle(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("An absolute path is required.", nameof(absolutePath));
        }

        var handle = CreateFileW(
            absolutePath,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open an exclusive ownership-marker handle.");
        }

        return handle;
    }

    public static void MarkForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the verified ownership marker.");
        }
    }

    private static SafeFileHandle OpenMetadataHandle(string absolutePath, uint additionalFlags)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("An absolute path is required.", nameof(absolutePath));
        }

        var handle = CreateFileW(
            absolutePath,
            FileReadAttributes,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics | additionalFlags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open a filesystem metadata handle.");
        }

        return handle;
    }

    public static string GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the final path.");
        var buffer = new char[required + 1];
        var written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the final path.");
        var path = new string(buffer, 0, (int)written);
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
            path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    public static FilesystemIdentity GetIdentity(SafeFileHandle handle)
    {
        var info = new FileIdInfo { FileId = new byte[16] };
        if (GetFileInformationByHandleEx(handle, 18, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return new(info.VolumeSerialNumber, Convert.ToHexString(info.FileId), false);
        }

        var error = Marshal.GetLastWin32Error();
        if (error is not 1 and not 50 and not 87)
        {
            throw new Win32Exception(error, "Could not obtain filesystem identity.");
        }

        if (!GetFileInformationByHandle(handle, out var basic))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain fallback filesystem identity.");
        }

        return new(basic.VolumeSerialNumber, (((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow).ToString("X16"), true);
    }

    public static DateTimeOffset GetCreationTimeUtc(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var basic))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain filesystem creation metadata.");
        }

        var fileTime = ((long)basic.CreationTime.High << 32) | basic.CreationTime.Low;
        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] FileId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes; public NativeFileTime CreationTime; public NativeFileTime LastAccessTime; public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks;
        public uint FileIndexHigh; public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public int High; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, ref FileIdInfo information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDispositionInfo information, uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[]? path, uint length, uint flags);
}
