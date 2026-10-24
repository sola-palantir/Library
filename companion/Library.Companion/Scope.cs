using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Library.Companion;

// Windows directory handles deny rename/delete and OPEN_REPARSE_POINT refuses junction traversal.
public sealed class Scope : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    public string Root { get; }
    public Scope(string root, bool opticalRoot = false)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root) || (!opticalRoot && Path.GetPathRoot(Root) == Root) || Root.StartsWith("\\\\")) throw new InvalidDataException("Scope must be an existing local non-root directory.");
        string current = Path.GetPathRoot(Root)!;
        try { foreach (var component in Root[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) { current = Path.Combine(current, component); Hold(current); } }
        catch { Dispose(); throw; }
    }
    public static void Relative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || IsDevice(p))) throw new InvalidDataException("Unsafe relative path.");
    }
    private static bool IsDevice(string p) { var stem = p.Split('.')[0].ToUpperInvariant(); return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]); }
    private void Hold(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points and symlinks are not allowed.");
        if (!OperatingSystem.IsWindows()) return;
        var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!GetFileInformationByHandle(h, out var info) || (info.Attributes & 0x400) != 0) { h.Dispose(); throw new IOException("Directory changed to a reparse point."); }
        directories.Add(h);
    }
    public string Resolve(string relative)
    {
        Relative(relative); var pieces = relative.Split('/'); var current = Root;
        foreach (var p in pieces[..^1]) { current = Path.Combine(current, p); Hold(current); }
        var path = Path.Combine(current, pieces[^1]);
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symlink file refused.");
        return path;
    }
    public FileStream Read(string relative)
    {
        var path = Resolve(relative);
        if (!OperatingSystem.IsWindows()) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var h = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!GetFileInformationByHandle(h, out var info) || (info.Attributes & (0x400 | 0x10)) != 0) { h.Dispose(); throw new IOException("Unsafe file handle."); }
        return new FileStream(h, FileAccess.Read);
    }
    public void Dispose() { foreach (var d in directories.AsEnumerable().Reverse()) d.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
}
