namespace Library.Api;

public static class ExecutionMode
{
    public const string Linux = "linux-local";
    public const string Windows = "windows-worker";
    public static bool IsLocal(LibraryOptions options) => options.ExecutionBackend == Linux;
    public static void Validate(LibraryOptions options)
    {
        if (options.ExecutionBackend is not (Linux or Windows)) throw new ArgumentException("Unknown execution backend; choose linux-local or windows-worker explicitly.");
        if (!IsLocal(options) && (options.EnableSampleRemux || options.EnableSampleEncode || options.EnableSampleHandoff || options.SampleHandoffGrant != null))
            throw new ArgumentException("Windows worker mode cannot enable Linux processing or media export. Worker communication is not yet configured.");
    }
    public static void RequireLocal(LibraryOptions options)
    {
        if (!IsLocal(options)) throw new InvalidOperationException("Media execution belongs to the Windows worker. Its connection is not configured; no Linux fallback or media transfer is allowed.");
    }
}
