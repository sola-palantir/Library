using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Library.Api;

// Linux-only service. Every component is opened relative to an already-open directory,
// with O_NOFOLLOW. Pinning parent handles closes traversal/symlink replacement races.
public sealed class Storage(LibraryOptions options)
{
    public IReadOnlyList<StorageRoot> Roots => options.Roots;
    public void RequireLocalExecution() => ExecutionMode.RequireLocal(options);
    public StorageRoot Root(string id)
    {
        RequireLocalExecution();
        return Roots.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("Unknown storage root.");
    }
    public void ValidateConfiguration()
    {
        ExecutionMode.Validate(options);
        if (!ExecutionMode.IsLocal(options)) return; // Root paths belong to the worker, never this container.
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run the service in a Linux container.");
        if (Roots.Select(x => x.Id).Distinct().Count() != Roots.Count) throw new ArgumentException("Root IDs must be unique.");
        var rootIdentities = new HashSet<string>();
        foreach (var r in Roots)
        {
            if (r.Role is not ("source" or "library" or "staging" or "disc")) throw new ArgumentException("Invalid root role.");
            if (!Path.IsPathFullyQualified(r.ContainerPath) || r.ContainerPath == "/") throw new ArgumentException("Roots must be absolute and cannot be /.");
            var path = Path.GetFullPath(r.ContainerPath).TrimEnd('/');
            if (path.Length == 0) throw new ArgumentException("A storage root cannot be the filesystem root.");
            if (Roots.Any(other => other != r && (path.Equals(Path.GetFullPath(other.ContainerPath).TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.GetFullPath(other.ContainerPath).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("Storage roots must not overlap.");
            var data = Path.GetFullPath(options.DataPath).TrimEnd('/');
            if (path == data || path.StartsWith(data + "/") || data.StartsWith(path + "/")) throw new ArgumentException("Catalog data must be separate from media roots.");
            // Missing mounts remain visible as offline. Existing roots must be safe.
            if (Directory.Exists(path))
            {
                using var handle = OpenDirectory(path);
                var identity = string.Join(":", Native.Observe(handle.DangerousGetHandle().ToInt32()).Identity.Split(':').Take(3));
                if (!rootIdentities.Add(identity)) throw new ArgumentException("Storage roots must not alias the same physical directory.");
            }
            var windowsPath = r.WindowsPath.Replace('\\', '/').TrimEnd('/');
            if (windowsPath.Length > 0 && Roots.Any(other => other != r && other.WindowsPath.Length > 0 &&
                (windowsPath.Equals(other.WindowsPath.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || windowsPath.StartsWith(other.WindowsPath.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("Declared Windows storage roots must not overlap.");
        }
    }
    public static string[] Components(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\')) throw new ArgumentException("Use a nonempty relative Linux path.");
        var parts = path.Split('/');
        foreach (var p in parts)
            if (p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0].ToUpperInvariant()))
                throw new ArgumentException("Unsafe or Windows-incompatible path component.");
        return parts;
    }
    public SafeFileHandle OpenRoot(string id) => OpenDirectory(Root(id).ContainerPath);
    public FileStream Read(string id, string relative)
    {
        using var parent = Parent(id, relative, false);
        var fd = Native.openat(parent.DangerousGetHandle().ToInt32(), Components(relative)[^1], Native.NoFollow | Native.CloseOnExec | Native.NonBlock, 0);
        var handle = Checked(fd);
        // Reject directories, devices, pipes, sockets; statx on the open descriptor.
        if (!Native.IsRegular(fd)) { handle.Dispose(); throw new IOException("Only regular files are supported."); }
        return new FileStream(handle, FileAccess.Read, 1024 * 1024, false);
    }
    public FileStream Create(string id, string relative)
    {
        Writable(id);
        using var parent = Parent(id, relative, true);
        var name = Components(relative)[^1];
        RejectCaseCollision(parent, name);
        return new FileStream(Checked(Native.openat(parent.DangerousGetHandle().ToInt32(), name, Native.NoFollow | Native.CloseOnExec | 1 | 64 | 128, 0x180)), FileAccess.Write, 1024 * 1024, false);
    }
    public void Publish(string id, string temporary, string destination)
    {
        Writable(id);
        using var from = Parent(id, temporary, false);
        using var to = Parent(id, destination, true);
        RejectCaseCollision(to, Components(destination)[^1]);
        if (Native.renameat2(from.DangerousGetHandle().ToInt32(), Components(temporary)[^1], to.DangerousGetHandle().ToInt32(), Components(destination)[^1], 1) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Atomic no-replace publication failed (errno {error}: {new Win32Exception(error).Message}). Source and staged data are retained; this destination must support renameat2(RENAME_NOREPLACE). No overwrite fallback is permitted.");
        }
        if (Native.fsync(to.DangerousGetHandle().ToInt32()) != 0) throw Error();
    }
    public void RemoveTemporary(string id, string relative, string jobId)
    {
        Writable(id);
        if (!relative.EndsWith($".library-{jobId}.partial", StringComparison.Ordinal)) throw new ArgumentException("Only job-owned temporary files may be removed.");
        using var parent = Parent(id, relative, false);
        var rc = Native.unlinkat(parent.DangerousGetHandle().ToInt32(), Components(relative)[^1], 0);
        if (rc != 0 && Marshal.GetLastPInvokeError() != 2) throw Error();
    }
    public FileObservation Observe(string id, string path)
    {
        using var file = Read(id, path);
        return Observe(file);
    }
    public static FileObservation Observe(FileStream file) => Native.Observe(file.SafeFileHandle.DangerousGetHandle().ToInt32());
    public (long Bytes, long Ticks) Fingerprint(string id, string path)
    {
        using var file = Read(id, path);
        var observation = Observe(file); return (observation.Bytes, observation.Ticks);
    }
    public string Identity(string id, string path)
    {
        using var file = Read(id, path);
        return Observe(file).Identity;
    }
    public bool Exists(string id, string path)
    {
        try
        {
            using var parent = Parent(id, path, false);
            return Directory.EnumerateFileSystemEntries(FdPath(parent)).Any(x => Path.GetFileName(x).Equals(Components(path)[^1], StringComparison.OrdinalIgnoreCase));
        }
        catch (FileNotFoundException) { return false; }
    }
    public long FreeBytes(string id)
    {
        using var root = OpenRoot(id);
        if (Native.statvfs(FdPath(root), out var stat) != 0) throw Error();
        return checked((long)(stat.Available * stat.FragmentSize));
    }
    public IEnumerable<string> EnumerateMedia(string id)
    {
        using var root = OpenRoot(id);
        foreach (var path in Walk(root, "")) yield return path;
    }
    private static IEnumerable<string> Walk(SafeFileHandle directory, string prefix)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(FdPath(directory)))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null) continue;
            var name = Path.GetFileName(path);
            var relative = prefix + name;
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                using var child = Checked(Native.openat(directory.DangerousGetHandle().ToInt32(), name, Native.Directory | Native.NoFollow | Native.CloseOnExec, 0));
                foreach (var nested in Walk(child, relative + "/")) yield return nested;
            }
            else if (new[] { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts" }.Contains(Path.GetExtension(name).ToLowerInvariant())) yield return relative;
        }
    }
    private void Writable(string id)
    {
        if (Root(id).Role is not ("library" or "staging")) throw new ArgumentException("This root is read-only.");
    }
    private SafeFileHandle Parent(string id, string path, bool create)
    {
        var parts = Components(path);
        var current = OpenRoot(id);
        try
        {
            foreach (var name in parts[..^1])
            {
                if (create)
                {
                    Writable(id);
                    // Windows path semantics: refuse differently cased sibling names.
                    RejectCaseCollision(current, name, allowExact: true);
                    var created = Native.mkdirat(current.DangerousGetHandle().ToInt32(), name, 0x1C0);
                    if (created != 0 && Marshal.GetLastPInvokeError() != 17) throw Error();
                    if (created == 0 && Native.fsync(current.DangerousGetHandle().ToInt32()) != 0) throw Error();
                }
                var next = Checked(Native.openat(current.DangerousGetHandle().ToInt32(), name, Native.Directory | Native.NoFollow | Native.CloseOnExec, 0));
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    private static void RejectCaseCollision(SafeFileHandle parent, string name, bool allowExact = false)
    {
        if (Directory.EnumerateFileSystemEntries(FdPath(parent)).Select(Path.GetFileName).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && (!allowExact || n != name))) throw new IOException("Destination collision; existing files are never overwritten.");
    }
    private static SafeFileHandle OpenDirectory(string absolute)
    {
        if (!Path.IsPathFullyQualified(absolute)) throw new ArgumentException("Absolute root required.");
        var current = Checked(Native.open("/", Native.Directory | Native.CloseOnExec, 0));
        try
        {
            foreach (var component in Path.GetFullPath(absolute).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Checked(Native.openat(current.DangerousGetHandle().ToInt32(), component, Native.Directory | Native.NoFollow | Native.CloseOnExec, 0));
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    private static string FdPath(SafeFileHandle h) => $"/proc/self/fd/{h.DangerousGetHandle()}";
    private static SafeFileHandle Checked(int fd) => fd < 0 ? throw Error() : new SafeFileHandle((IntPtr)fd, true);
    private static IOException Error() => Marshal.GetLastPInvokeError() == 2 ? new FileNotFoundException("Storage path is unavailable.") : new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
}
internal static class Native
{
    internal const int NoFollow = 0x20000, CloseOnExec = 0x80000, Directory = 0x10000, NonBlock = 0x800;
    [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] internal static extern int openat(int dir, string name, int flags, int mode);
    [DllImport("libc", SetLastError = true)] internal static extern int mkdirat(int dir, string name, int mode);
    [DllImport("libc", SetLastError = true)] internal static extern int renameat2(int fromDir, string from, int toDir, string to, uint flags);
    [DllImport("libc", SetLastError = true)] internal static extern int unlinkat(int dir, string name, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] internal static extern int statvfs(string path, out StatVfs stat);
    [StructLayout(LayoutKind.Sequential)] internal struct StatVfs { public ulong BlockSize, FragmentSize, Blocks, Free, Available, Files, FilesFree, FilesAvailable, FileSystemId, Flags, NameMax; private ulong spare1, spare2, spare3; }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, string path, int flags, uint mask, IntPtr buffer);
    internal static FileObservation Observe(int fd)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (statx(fd, "", 0x1000, 0x7ff, buffer) != 0 || (Marshal.ReadInt32(buffer) & 0x3c0) != 0x3c0) throw new IOException("Filesystem does not expose the required file identity and timestamps.");
            return new(Marshal.ReadInt64(buffer, 40), DateTime.UnixEpoch.Ticks + Marshal.ReadInt64(buffer, 112) * TimeSpan.TicksPerSecond + (uint)Marshal.ReadInt32(buffer, 120) / 100,
                $"{Marshal.ReadInt32(buffer, 136)}:{Marshal.ReadInt32(buffer, 140)}:{Marshal.ReadInt64(buffer, 32)}:{Marshal.ReadInt64(buffer, 96)}:{Marshal.ReadInt32(buffer, 104)}");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static bool IsRegular(int fd)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try { return statx(fd, "", 0x1000, 2, buffer) == 0 && (Marshal.ReadInt16(buffer, 28) & 0xF000) == 0x8000; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
