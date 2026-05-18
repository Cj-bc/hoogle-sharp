namespace CSharpHoogle.Cli;

internal static class UnityFallbacks
{
    internal static readonly IReadOnlyList<string> Tfms = new[]
    {
        "netstandard2.1",
        "netstandard2.0",
        "net48",
        "net471",
        "net46",
    };
}
