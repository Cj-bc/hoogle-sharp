namespace CSharpHoogle.Cli;

internal static class PathDisplay
{
    internal static string GetFriendlyPath(string fullPath)
    {
        var cwd = Environment.CurrentDirectory;
        if (fullPath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
        {
            var rel = fullPath.Substring(cwd.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Length > 0 ? rel : fullPath;
        }
        return fullPath;
    }
}
