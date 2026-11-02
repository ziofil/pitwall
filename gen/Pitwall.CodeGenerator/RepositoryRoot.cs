namespace Pitwall.CodeGenerator;

internal static class RepositoryRoot
{
    /// <summary>The repository root: the nearest folder above this tool that holds Pitwall.sln.</summary>
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Pitwall.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Couldn't find the repository root (Pitwall.sln)"
            );
    }
}
