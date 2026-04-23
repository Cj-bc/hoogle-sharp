using CSharpHoogle.Core.Indexing;

namespace CSharpHoogle.Core.Tests.Indexing;

public class BclIndexBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public BclIndexBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoogle-sharp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteFixture(string fileName, string xml)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void BuildFromFiles_TwoFixtures_MergesBothSets()
    {
        var a = WriteFixture("a.xml", """
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:A.One"><summary>A one.</summary></member>
            </members></doc>
            """);
        var b = WriteFixture("b.xml", """
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:B.Two"><summary>B two.</summary></member>
            </members></doc>
            """);

        var merged = BclIndexBuilder.BuildFromFiles(new[] { a, b });

        Assert.Equal(2, merged.Count);
        Assert.Equal("A one.", merged["T:A.One"].Summary);
        Assert.Equal("B two.", merged["T:B.Two"].Summary);
    }

    [Fact]
    public void BuildFromFiles_OverlappingKeys_LastWins()
    {
        var a = WriteFixture("a.xml", """
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Same.Key"><summary>First.</summary></member>
            </members></doc>
            """);
        var b = WriteFixture("b.xml", """
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Same.Key"><summary>Second.</summary></member>
            </members></doc>
            """);

        var merged = BclIndexBuilder.BuildFromFiles(new[] { a, b });

        Assert.Single(merged);
        Assert.Equal("Second.", merged["T:Same.Key"].Summary);
    }

    [Fact]
    public void BuildFromRuntime_DefaultDocDir_FindsSystemLinqSelect()
    {
        // On a well-formed .NET SDK install, ResolveDefaultDocDir finds the reference pack
        // (docs ship there, not in shared/Microsoft.NETCore.App). If it returns a dir with
        // zero XML files we've got a trimmed install — skip rather than false-fail.
        var docDir = BclIndexBuilder.ResolveDefaultDocDir();
        if (Directory.GetFiles(docDir, "*.xml").Length == 0)
        {
            return;
        }

        var index = BclIndexBuilder.BuildFromRuntime();

        var selectKeys = index.Keys
            .Where(k => k.StartsWith("M:System.Linq.Enumerable.Select", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(selectKeys);

        // At least one Select overload should have source + selector params and a non-empty Summary.
        var matching = selectKeys
            .Select(k => index[k])
            .FirstOrDefault(e =>
                e.Params.ContainsKey("source") &&
                e.Params.ContainsKey("selector") &&
                !string.IsNullOrWhiteSpace(e.Summary));

        Assert.NotNull(matching);
        Assert.Contains("sequence", matching!.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFromRuntime_ExplicitDir_UsesProvidedDir()
    {
        WriteFixture("Widget.xml", """
            <?xml version="1.0"?>
            <doc><members>
              <member name="T:Widget"><summary>W.</summary></member>
            </members></doc>
            """);
        // Create a matching .dll stub so the enumeration picks it up.
        File.WriteAllBytes(Path.Combine(_tempDir, "Widget.dll"), Array.Empty<byte>());

        var index = BclIndexBuilder.BuildFromRuntime(_tempDir);

        Assert.Single(index);
        Assert.Equal("W.", index["T:Widget"].Summary);
    }
}
