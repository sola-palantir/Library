using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Library.Execution;

namespace Library.Companion;

// Local filesystem primitive only. This opens no listener and transfers no data.
// Scope must pin parent directories for the lifetime of the returned handle.
public static class WindowsPublication
{
    public static FileObservation Observe(FileStream file, bool requireSingleLink = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows handle observation required.");
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (0x400 | 0x10)) != 0 || requireSingleLink && info.Links != 1) throw new IOException("Unsafe file identity.");
        var ticks = ((long)(uint)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime;
        return new(checked(((long)info.SizeHigh << 32) | info.SizeLow), DateTime.FromFileTimeUtc(ticks).Ticks, $"{info.Volume}:{info.IndexHigh}:{info.IndexLow}");
    }
    public static FileStream Open(Scope scope, string relative, bool existing = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native publication requires Windows.");
        var path = scope.Resolve(relative);
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveFormat != "NTFS") throw new IOException("Only local NTFS publication is qualified.");
        var handle = CreateFile(path, 0xC0010000, 0, IntPtr.Zero, existing ? 3u : 1u, 0x80200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try { Identity(handle); return new FileStream(handle, FileAccess.ReadWrite); }
        catch { handle.Dispose(); throw; }
    }
    public static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (0x400 | 0x10)) != 0 || info.Links != 1) throw new IOException("Publication requires a regular file with one link and no reparse point.");
        return $"{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
    }
    public static void Publish(Scope scope, FileStream file, string relative)
    {
        var destination = scope.Resolve(relative);
        var identity = Identity(file.SafeFileHandle); file.Flush(true);
        var name = Encoding.Unicode.GetBytes(destination);
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var size = checked(nameOffset + name.Length + 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            // FILE_RENAME_INFO: ReplaceIfExists=false, RootDirectory=NULL;
            // the caller holds Scope's directory chain throughout the operation.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(file.SafeFileHandle, 3, buffer, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Native no-overwrite publication failed; partial retained.");
            file.Flush(true);
            if (Identity(file.SafeFileHandle) != identity) throw new IOException("File identity changed during publication.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, uint bytes);
}
