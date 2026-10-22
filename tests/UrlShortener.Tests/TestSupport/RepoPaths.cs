namespace UrlShortener.Tests.TestSupport;

public static class RepoPaths
{
    /// <summary>Directory that contains src/UrlShortener.Api (found by walking up from the test binaries).</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "UrlShortener.Api")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/UrlShortener.Api above " + AppContext.BaseDirectory);
    }

    public static string ApiSource() => Path.Combine(Root(), "src", "UrlShortener.Api");

    public static IEnumerable<string> ApiSourceFiles() =>
        Directory.EnumerateFiles(ApiSource(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
}
