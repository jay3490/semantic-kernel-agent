namespace Agent.Tests;

/// <summary>Access to the files under <c>samples/</c>, which the test project copies next to the test assembly.</summary>
internal static class Samples
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "samples");

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));
}
