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
