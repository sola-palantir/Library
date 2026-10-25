namespace Library.Execution;

public static class ArchivePaths
{
    // Call after validating individual relative paths and exact duplicates.
    // A file cannot also be the parent directory of another selected file.
    public static bool HasFileDirectoryCollision(IEnumerable<string> paths)
    {
        var files = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (files.Contains(path[..slash])) return true;
        return false;
    }
}
