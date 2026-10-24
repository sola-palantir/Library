using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Library.Companion;

public record NativeProcessLimits(long MemoryBytes, uint CpuRate, uint Processes, TimeSpan Timeout)
{
    public static readonly NativeProcessLimits Sample = new(1024L * 1024 * 1024, 2500, 2, TimeSpan.FromMinutes(2));
    public void Validate()
    {
        if (MemoryBytes is < 16 * 1024 * 1024 or > 1024L * 1024 * 1024 || CpuRate is < 100 or > 5000 || Processes is < 1 or > 4 || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Native resource limits exceed the bounded sample envelope.");
    }
}
public record NativeProcessEvidence(uint ProcessId, uint ExitCode, long OutputBytes, long PeakCommittedBytes, long UserTimeTicks, string Stderr);

// No shell, path reopening, network transport, permission change or service. The
// child joins the configured kill-on-close Job Object AT CREATION, before any
// user code runs. Only three explicitly duplicated standard handles are inherited.
internal static class NativeProcess
{
    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL in process argument.");
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static SafeFileHandle Duplicate(SafeFileHandle original, uint access)
    {
        Check(DuplicateHandle(GetCurrentProcess(), original, GetCurrentProcess(), out var copy, access, true, 0)); return copy;
    }
    static void SetJob<T>(SafeFileHandle job, int kind, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>(); var memory = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(value, memory, false); Check(SetInformationJobObject(job, kind, memory, (uint)size)); }
        finally { Marshal.FreeHGlobal(memory); }
    }
    static T ReadJob<T>(SafeFileHandle job, int kind) where T : struct
    {
        var size = Marshal.SizeOf<T>(); var memory = Marshal.AllocHGlobal(size);
        try { Check(QueryInformationJobObject(job, kind, memory, (uint)size, IntPtr.Zero)); return Marshal.PtrToStructure<T>(memory); }
        finally { Marshal.FreeHGlobal(memory); }
    }
    // Internal validation path for an already bounded in-memory sample. This is
    // an anonymous local pipe, never a network/control-plane media endpoint.
    // NativeMediaProcessor still requires a pinned seekable file for source I/O.
    public static async Task<NativeProcessEvidence> RunBufferedInput(string executable, string[] arguments, ReadOnlyMemory<byte> input,
        Stream output, NativeProcessLimits limits, long outputLimit, CancellationToken ct)
    {
        if (input.Length is <= 0 or > 268435456) throw new ArgumentException("Buffered validation input exceeds the sample envelope.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows process controls required.");
        ct.ThrowIfCancellationRequested();
        Check(CreatePipe(out var readHandle, out var writeHandle, IntPtr.Zero, 65536));
        using var reader = new FileStream(readHandle, FileAccess.Read);
        using var writer = new FileStream(writeHandle, FileAccess.Write);
        var feed = Task.Run(async () => {
            try { for (var offset = 0; offset < input.Length; offset += 65536) await writer.WriteAsync(input.Slice(offset, Math.Min(65536, input.Length - offset)), ct); }
            finally { writer.Dispose(); }
        });
        var succeeded = false;
        try
        {
            var result = await Run(executable, arguments, reader, output, limits, outputLimit, ct);
            // Closing the parent's reader unblocks a feeder if a child exited
            // early, including after timeout or log overflow.
            reader.Dispose();
            await feed; succeeded = true; return result;
        }
        finally
        {
            reader.Dispose();
            if (!succeeded) { try { await feed; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }
        }
    }
    public static async Task<NativeProcessEvidence> Run(string executable, string[] arguments, FileStream? input, Stream output,
        NativeProcessLimits limits, long outputLimit, CancellationToken caller, Action<uint>? started = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows process controls required.");
        limits.Validate(); caller.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executable) || outputLimit is <= 0 or > 268435456 || arguments.Length > 512) throw new ArgumentException("Explicit executable and bounded output required.");
        var commandLine = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
        if (commandLine.Length > 30000) throw new ArgumentException("Process arguments too long.");
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // JobMemoryLimit is aggregate committed memory, not a sampled working-set
        // promise. No breakaway flags. The job handle itself is never inherited.
        SetJob(job, 9, new ExtendedLimits { Basic = new() { Flags = 0x2000 | 0x200 | 0x100 | 0x8, ActiveProcesses = limits.Processes }, ProcessMemory = (nuint)limits.MemoryBytes, JobMemory = (nuint)limits.MemoryBytes });
        SetJob(job, 15, new CpuLimit { Flags = 1 | 4, Rate = limits.CpuRate });
        var configured = ReadJob<ExtendedLimits>(job, 9); var cpu = ReadJob<CpuLimit>(job, 15);
        if (configured.JobMemory != (nuint)limits.MemoryBytes || configured.Basic.ActiveProcesses != limits.Processes || cpu.Rate != limits.CpuRate || cpu.Flags != 5)
            throw new IOException("Windows did not retain the requested job limits.");
        using var stdin = input == null ? CreateFile("NUL", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero) : Duplicate(input.SafeFileHandle, 0x120089);
        if (stdin.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var seekableInput = input?.CanSeek == true;
        if (input != null && !seekableInput && GetFileType(input.SafeFileHandle) != 3) throw new IOException("Only pinned disk handles or internal anonymous validation pipes are supported.");
        if (seekableInput) { input!.Position = 0; Check(SetFilePointerEx(stdin, 0, out _, 0)); }
        // NUL's original handle is also duplicated so its inheritance is explicit.
        using var inheritedInput = Duplicate(stdin, 0x120089);
        Check(CreatePipe(out var stdoutRead, out var stdoutWrite, IntPtr.Zero, 65536));
        using var readOutput = new FileStream(stdoutRead, FileAccess.Read);
        using var writeOutput = stdoutWrite;
        using var inheritedOutput = Duplicate(writeOutput, 0x120116);
        Check(CreatePipe(out var stderrRead, out var stderrWrite, IntPtr.Zero, 65536));
        using var readError = new FileStream(stderrRead, FileAccess.Read);
        using var writeError = stderrWrite;
        using var inheritedError = Duplicate(writeError, 0x120116);
        nuint attributeBytes = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeBytes);
        var attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
        var inherited = Marshal.AllocHGlobal(3 * IntPtr.Size); var jobList = Marshal.AllocHGlobal(IntPtr.Size);
        var environment = Marshal.StringToHGlobalUni($"SystemRoot={Environment.GetEnvironmentVariable("SystemRoot")}\0WINDIR={Environment.GetEnvironmentVariable("WINDIR")}\0\0");
        ProcessInfo info = default; bool initialized = false, created = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeBytes)); initialized = true;
            Marshal.WriteIntPtr(inherited, inheritedInput.DangerousGetHandle()); Marshal.WriteIntPtr(inherited, IntPtr.Size, inheritedOutput.DangerousGetHandle()); Marshal.WriteIntPtr(inherited, IntPtr.Size * 2, inheritedError.DangerousGetHandle());
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, inherited, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx { Info = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, StdInput = inheritedInput.DangerousGetHandle(), StdOutput = inheritedOutput.DangerousGetHandle(), StdError = inheritedError.DangerousGetHandle() }, Attributes = attributes };
            Check(CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x00080000 | 0x00000400 | 0x08000000 | 0x4, environment, Path.GetDirectoryName(executable)!, ref startup, out info)); created = true;
            using var process = new SafeFileHandle(info.Process, true); using var thread = new SafeFileHandle(info.Thread, true);
            // Closing every parent-side writer is essential for EOF after job kill.
            inheritedOutput.Dispose(); inheritedError.Dispose(); writeOutput.Dispose(); writeError.Dispose(); inheritedInput.Dispose();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(limits.Timeout);
            void Kill() { if (!job.IsClosed) TerminateJobObject(job, 0xE001); }
            using var cancellation = deadline.Token.Register(Kill);
            async Task<long> Pump(Stream from, Stream to, long bound)
            {
                try
                {
                    long total = 0; var buffer = new byte[65536]; int read;
                    while ((read = await from.ReadAsync(buffer, CancellationToken.None)) > 0)
                    {
                        if (read > bound - total) throw new IOException("Native subprocess output/log limit exceeded.");
                        deadline.Token.ThrowIfCancellationRequested();
                        await to.WriteAsync(buffer.AsMemory(0, read), deadline.Token); total += read;
                    }
                    return total;
                }
                catch { Kill(); throw; }
            }
            using var error = new MemoryStream();
            var stdoutTask = Task.Run(() => Pump(readOutput, output, outputLimit));
            var stderrTask = Task.Run(() => Pump(readError, error, 65536));
            try
            {
                started?.Invoke(info.ProcessId);
                deadline.Token.ThrowIfCancellationRequested();
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                await Task.Run(() => { if (WaitForSingleObject(process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error()); });
                Check(GetExitCodeProcess(process, out var exit));
                Kill(); // Root exit must not leave children holding pipes or file handles.
                await Task.WhenAll(stdoutTask, stderrTask);
                caller.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new TimeoutException("Native subprocess exceeded its wall-clock deadline; job tree terminated.");
                var usage = ReadJob<ExtendedLimits>(job, 9); var accounting = ReadJob<Accounting>(job, 1);
                return new(info.ProcessId, exit, stdoutTask.Result, checked((long)usage.PeakJobMemory), accounting.TotalUser, Encoding.UTF8.GetString(error.ToArray()));
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new TimeoutException("Native subprocess exceeded its wall-clock deadline; job tree terminated."); }
            finally
            {
                Kill();
                if (WaitForSingleObject(process, 10000) != 0) throw new IOException("Owned native process did not terminate after job kill.");
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* preserve the primary failure */ }
                if (seekableInput) { Check(SetFilePointerEx(stdin, 0, out _, 0)); input!.Position = 0; }
            }
        }
        finally
        {
            // If setup fails after creation, the suspended child is already in job.
            if (created) TerminateJobObject(job, 0xE001);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(inherited); Marshal.FreeHGlobal(jobList); Marshal.FreeHGlobal(environment);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] struct CpuLimit { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long TotalUser, TotalKernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo { public uint Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedBytes; public IntPtr ReservedData, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr source, SafeFileHandle handle, IntPtr target, out SafeFileHandle copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, IntPtr info, uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr info, uint bytes, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
