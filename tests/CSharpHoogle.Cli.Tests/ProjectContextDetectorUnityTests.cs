using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class ProjectContextDetectorUnityTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectContextDetectorUnityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoogle-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Detect_AcceptsNetstandardTfm_InUnityProjectWithPackagesConfig()
    {
        // Unity project layout: csproj at project root, Assets/packages.config sibling.
        var unityRoot = Path.Combine(_tempDir, "MyUnityGame");
        Directory.CreateDirectory(Path.Combine(unityRoot, "Assets"));
        File.WriteAllText(Path.Combine(unityRoot, "Assets", "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages />
            """);

        var csproj = Path.Combine(unityRoot, "Assembly-CSharp.csproj");
        File.WriteAllText(csproj, """
            <Project>
              <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
            </Project>
            """);

        var ctx = ProjectContextDetector.Detect(unityRoot, projectOverride: csproj, tfmOverride: null);

        Assert.NotNull(ctx);
        Assert.Equal("netstandard2.1", ctx!.Tfm);
        Assert.Equal(SdkKind.Default, ctx.Sdk);
    }

    [Fact]
    public void Detect_RejectsNetstandardTfm_WhenNoUnityPackagesConfigPresent()
    {
        var projDir = Path.Combine(_tempDir, "Plain");
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "Lib.csproj");
        File.WriteAllText(csproj, """
            <Project>
              <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
            </Project>
            """);

        var ctx = ProjectContextDetector.Detect(projDir, projectOverride: csproj, tfmOverride: null);

        // No Unity tree → strict gate rejects netstandard.
        Assert.Null(ctx);
    }

    [Fact]
    public void EnumerateCsprojs_ReturnsSelf_ForCsprojOrigin()
    {
        var projDir = Path.Combine(_tempDir, "Single");
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "Single.csproj");
        File.WriteAllText(csproj, "<Project/>");

        var list = ProjectContextDetector.EnumerateCsprojs(csproj);

        Assert.Single(list);
        Assert.Equal(Path.GetFullPath(csproj), list[0]);
    }

    [Fact]
    public void ResolveProjectFromDirectory_PrefersSlnxOverSlnAndCsproj()
    {
        var dir = Path.Combine(_tempDir, "PriorityAll");
        Directory.CreateDirectory(dir);
        var slnx = Path.Combine(dir, "App.slnx");
        var sln = Path.Combine(dir, "App.sln");
        var csproj = Path.Combine(dir, "App.csproj");
        File.WriteAllText(slnx, "<Solution/>");
        File.WriteAllText(sln, "");
        File.WriteAllText(csproj, "<Project/>");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(error);
        Assert.Equal(slnx, resolved);
    }

    [Fact]
    public void ResolveProjectFromDirectory_PrefersSlnOverCsproj_WhenNoSlnx()
    {
        var dir = Path.Combine(_tempDir, "SlnOverCsproj");
        Directory.CreateDirectory(dir);
        var sln = Path.Combine(dir, "App.sln");
        var csproj = Path.Combine(dir, "App.csproj");
        File.WriteAllText(sln, "");
        File.WriteAllText(csproj, "<Project/>");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(error);
        Assert.Equal(sln, resolved);
    }

    [Fact]
    public void ResolveProjectFromDirectory_FallsBackToCsproj_WhenSolePresent()
    {
        var dir = Path.Combine(_tempDir, "OnlyCsproj");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "App.csproj");
        File.WriteAllText(csproj, "<Project/>");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(error);
        Assert.Equal(csproj, resolved);
    }

    [Fact]
    public void ResolveProjectFromDirectory_ErrorsOnMultipleSlnx()
    {
        var dir = Path.Combine(_tempDir, "MultiSlnx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "A.slnx"), "<Solution/>");
        File.WriteAllText(Path.Combine(dir, "B.slnx"), "<Solution/>");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(resolved);
        Assert.NotNull(error);
        Assert.Contains(".slnx", error);
        Assert.Contains("A.slnx", error);
        Assert.Contains("B.slnx", error);
    }

    [Fact]
    public void ResolveProjectFromDirectory_ErrorsOnMultipleSln_WhenNoSlnx()
    {
        var dir = Path.Combine(_tempDir, "MultiSln");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "A.sln"), "");
        File.WriteAllText(Path.Combine(dir, "B.sln"), "");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(resolved);
        Assert.NotNull(error);
        Assert.Contains(".sln", error);
        Assert.Contains("A.sln", error);
        Assert.Contains("B.sln", error);
    }

    [Fact]
    public void ResolveProjectFromDirectory_ErrorsOnMultipleCsproj_WhenNoSlnOrSlnx()
    {
        var dir = Path.Combine(_tempDir, "MultiCsproj");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "A.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(dir, "B.csproj"), "<Project/>");

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(resolved);
        Assert.NotNull(error);
        Assert.Contains(".csproj", error);
        Assert.Contains("A.csproj", error);
        Assert.Contains("B.csproj", error);
    }

    [Fact]
    public void ResolveProjectFromDirectory_ErrorsWhenNothingFound()
    {
        var dir = Path.Combine(_tempDir, "Empty");
        Directory.CreateDirectory(dir);

        var resolved = ProjectContextDetector.ResolveProjectFromDirectory(dir, out var error);

        Assert.Null(resolved);
        Assert.NotNull(error);
        Assert.Contains("No project files", error);
    }

    [Fact]
    public void EnumerateCsprojs_ReturnsAllMembers_ForSlnxOrigin()
    {
        var slnDir = Path.Combine(_tempDir, "Solution");
        var aDir = Path.Combine(slnDir, "A");
        var bDir = Path.Combine(slnDir, "B");
        Directory.CreateDirectory(aDir);
        Directory.CreateDirectory(bDir);

        var aProj = Path.Combine(aDir, "A.csproj");
        var bProj = Path.Combine(bDir, "B.csproj");
        File.WriteAllText(aProj, "<Project/>");
        File.WriteAllText(bProj, "<Project/>");

        var slnx = Path.Combine(slnDir, "Solution.slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Project Path="A/A.csproj" />
              <Project Path="B/B.csproj" />
            </Solution>
            """);

        var list = ProjectContextDetector.EnumerateCsprojs(slnx)
            .Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(2, list.Count);
        Assert.Contains(Path.GetFullPath(aProj), list);
        Assert.Contains(Path.GetFullPath(bProj), list);
    }
}
