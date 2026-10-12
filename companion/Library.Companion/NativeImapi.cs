using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
namespace Library.Companion;

public sealed class NativeImapi : IOpticalDevice
{
    public bool Simulated => false;
    private dynamic? heldRecorder;
    private string? heldId;
    public NativeImapi() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native IMAPI operations require Windows. No device operation was attempted."); }
    private static dynamic Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)!;
    private static void Release(object? obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj); }
    private dynamic Recorder(string id) { dynamic r = Create("IMAPI2.MsftDiscRecorder2"); r.InitializeDiscRecorder(id); return r; }
    public string[] ListRecorders()
    {
        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        try { var result = new List<string>(); for (int i = 0; i < (int)master.Count; i++) result.Add((string)master[i]); return result.ToArray(); }
        finally { Release(master); }
    }
    public Media Inspect(string recorderId)
    {
        dynamic r = heldId == recorderId ? heldRecorder! : Recorder(recorderId); dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        try
        {
            format.Recorder = r; format.ClientName = "Library preservation companion";
            if (!(bool)format.IsRecorderSupported(r) || !(bool)format.IsCurrentMediaSupported(r)) throw new InvalidOperationException("Recorder or media is unsupported.");
            int status = format.CurrentMediaStatus;
            return new(recorderId, (int)format.CurrentPhysicalMediaType, (bool)format.MediaPhysicallyBlank && (status & 0xfc01) == 0 && (status & 2) != 0, (bool)format.MediaHeuristicallyBlank, (long)format.FreeSectorsOnMedia, 2048, ((object[])r.VolumePathNames).Cast<string>().ToArray());
        }
        finally { Release(format); if (heldId != recorderId) Release(r); }
    }
    public IDisposable Acquire(string recorderId)
    {
        if (heldRecorder != null) throw new InvalidOperationException("A device is already held.");
        dynamic r = Recorder(recorderId); SafeFileHandle? drive = null; bool exclusive = false;
        try
        {
            r.AcquireExclusiveAccess(false, "Library preservation companion"); exclusive = true;
            var roots = ((object[])r.VolumePathNames).Cast<string>().ToArray();
            if (roots.Length != 1 || roots[0].Length != 3 || roots[0][1] != ':') throw new InvalidOperationException("One drive-letter optical volume is required.");
            drive = CreateFile("\\\\.\\" + roots[0][..2], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (drive.IsInvalid || !PreventRemoval(drive, true)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot lock optical tray; refusing a media-swap race.");
            heldRecorder = r; heldId = recorderId; var lockedDrive = drive;
            return new ActionLease(() => { try { PreventRemoval(lockedDrive, false); } finally { lockedDrive.Dispose(); r.ReleaseExclusiveAccess(); Release(r); heldRecorder = null; heldId = null; } });
        }
        catch { drive?.Dispose(); if (exclusive) r.ReleaseExclusiveAccess(); Release(r); throw; }
    }
    public string OpticalRoot(string recorderId, string expectedLabel)
    {
        if (heldId != recorderId) throw new InvalidOperationException("Device must be locked before verification.");
        var roots = ((object[])heldRecorder!.VolumePathNames).Cast<string>().ToArray();
        if (roots.Length != 1 || new DriveInfo(roots[0]).DriveType != DriveType.CDRom || !new DriveInfo(roots[0]).IsReady) throw new InvalidOperationException("Verification requires the recorder's mounted optical CD-ROM volume.");
        if (!new DriveInfo(roots[0]).VolumeLabel.Equals(expectedLabel, StringComparison.Ordinal)) throw new InvalidDataException("Wrong optical volume label.");
        dynamic format = Create("IMAPI2.MsftDiscFormat2Data");
        try { format.Recorder = heldRecorder; if ((int)format.CurrentPhysicalMediaType != 18 || (bool)format.MediaHeuristicallyBlank) throw new InvalidOperationException("Expected written BD-R media."); }
        finally { Release(format); }
        return roots[0];
    }
    public void BuildImage(FrozenManifest manifest, string stagingDirectory, byte[] manifestBytes, Stream output)
    {
        dynamic image = Create("IMAPI2FS.MsftFileSystemImage"); var files = new List<IDisposable>();
        try
        {
            image.ChooseImageDefaultsForMediaType(18); image.FileSystemsToCreate = 4; image.UDFRevision = 0x250; image.VolumeName = manifest.Label;
            // Image construction is intentionally not capped to nominal 50 GB. Actual inserted media decides fit later.
            image.FreeMediaBlocks = int.MaxValue;
            using var scope = new Scope(stagingDirectory); var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                var components = file.Path.Split('/'); var parent = "";
                foreach (var part in components[..^1]) { parent = parent.Length == 0 ? part : parent + "/" + part; if (directories.Add(parent)) image.Root.AddDirectory(parent); }
                var stream = scope.Read(file.Path); files.Add(stream); image.Root.AddFile(file.Path, new ComStream(stream));
            }
            var manifestStream = new MemoryStream(manifestBytes, writable: false); files.Add(manifestStream); image.Root.AddFile(Contract.ManifestName, new ComStream(manifestStream));
            dynamic result = image.CreateResultImage();
            try
            {
                long expected = checked((long)result.TotalBlocks * (int)result.BlockSize);
                IStream input = (IStream)result.ImageStream; var buffer = new byte[1024 * 1024]; var count = Marshal.AllocHGlobal(sizeof(int));
                try { long written = 0; while (written < expected) { var take = (int)Math.Min(buffer.Length, expected - written); input.Read(buffer, take, count); int read = Marshal.ReadInt32(count); if (read <= 0 || read > take) throw new EndOfStreamException(); output.Write(buffer, 0, read); written += read; } }
                finally { Marshal.FreeHGlobal(count); Release(input); }
            }
            finally { Release(result); }
        }
        finally { Release(image); foreach (var f in files) f.Dispose(); }
    }
    public void WriteImage(string recorderId, Stream image, Action<string> progress)
    {
        if (heldId != recorderId) throw new InvalidOperationException("Locked recorder is required.");
        dynamic writer = Create("IMAPI2.MsftDiscFormat2Data");
        var events = new Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
        Action<object, object> callback = (_, args) => { dynamic a = args; if ((int)a.CurrentAction == 5) progress("finalizing"); };
        bool subscribed = false;
        try
        {
            writer.Recorder = heldRecorder; writer.ClientName = "Library preservation companion"; writer.ForceMediaToBeClosed = true; writer.ForceOverwrite = false; writer.DisableConsumerDvdCompatibilityMode = false;
            // Recheck blank media immediately before the single write call; no erase/multisession path exists.
            Contract.Fit(Inspect(recorderId), image.Length, 0);
            ComEventsHelper.Combine(writer, events, 0x200, callback); subscribed = true;
            writer.Write(new ComStream(image));
        }
        finally { if (subscribed) ComEventsHelper.Remove(writer, events, 0x200, callback); Release(writer); }
    }
    public void Dispose() { }
    private sealed class ActionLease(Action release) : IDisposable { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
    private static bool PreventRemoval(SafeFileHandle drive, bool prevent) { byte input = prevent ? (byte)1 : (byte)0; return DeviceIoControl(drive, 0x002D4804, ref input, 1, IntPtr.Zero, 0, out _, IntPtr.Zero); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref byte input, uint size, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ComStream : IStream
{
    private readonly Stream stream;
    private long position;
    public ComStream(Stream stream) { this.stream = stream; position = stream.Position; }
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    { lock (stream) { stream.Position = position; int n = stream.Read(pv, 0, cb); position += n; if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n); } }
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException("Read-only stream.");
    public void Seek(long offset, int origin, IntPtr result)
    { lock (stream) { position = origin switch { 0 => offset, 1 => checked(position + offset), 2 => checked(stream.Length + offset), _ => throw new ArgumentException("Invalid seek origin") }; if (position < 0) throw new IOException("Negative seek"); if (result != IntPtr.Zero) Marshal.WriteInt64(result, position); } }
    public void SetSize(long size) => throw new NotSupportedException();
    public void CopyTo(IStream target, long cb, IntPtr read, IntPtr written)
    { var buffer = new byte[1024 * 1024]; long total = 0; var count = Marshal.AllocHGlobal(4); try { while (total < cb) { Read(buffer, (int)Math.Min(buffer.Length, cb - total), count); int n = Marshal.ReadInt32(count); if (n == 0) break; target.Write(buffer, n, IntPtr.Zero); total += n; } } finally { Marshal.FreeHGlobal(count); } if (read != IntPtr.Zero) Marshal.WriteInt64(read, total); if (written != IntPtr.Zero) Marshal.WriteInt64(written, total); }
    public void Commit(int flags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long cb, int type) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long cb, int type) => throw new NotSupportedException();
    public void Stat(out STATSTG stat, int flags) => stat = new STATSTG { type = 2, cbSize = stream.Length, grfMode = 0 };
    public void Clone(out IStream clone) { lock (stream) clone = new ComStream(stream) { position = position }; }
}
