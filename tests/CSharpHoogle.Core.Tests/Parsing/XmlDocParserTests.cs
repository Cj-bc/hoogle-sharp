using CSharpHoogle.Core.Parsing;

namespace CSharpHoogle.Core.Tests.Parsing;

public class XmlDocParserTests : IDisposable
{
    private readonly string _tempDir;

    public XmlDocParserTests()
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
    public void Parse_ValidFixture_ExtractsSummaryParamsReturns()
    {
        const string xml = """
            <?xml version="1.0"?>
            <doc>
              <assembly><name>System.Linq</name></assembly>
              <members>
                <member name="M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})">
                  <summary>Projects each element of a sequence into a new form.</summary>
                  <param name="source">A sequence of values to invoke a transform function on.</param>
                  <param name="selector">A transform function to apply to each element.</param>
                  <returns>An IEnumerable whose elements are the result of invoking the transform function on each element of source.</returns>
                </member>
              </members>
            </doc>
            """;

        var path = WriteFixture("linq.xml", xml);
        var entries = XmlDocParser.Parse(path);

        Assert.Single(entries);
        var key = "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})";
        Assert.True(entries.ContainsKey(key));
        var entry = entries[key];
        Assert.Equal("Projects each element of a sequence into a new form.", entry.Summary);
        Assert.Equal("A sequence of values to invoke a transform function on.", entry.Params["source"]);
        Assert.Equal("A transform function to apply to each element.", entry.Params["selector"]);
        Assert.NotNull(entry.Returns);
        Assert.Contains("IEnumerable", entry.Returns);
    }

    [Fact]
    public void Parse_SeeCrefTag_StrippedToPlainText()
    {
        const string xml = """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:System.String">
                  <summary>Represents text as a sequence of <see cref="T:System.Char"/> values.</summary>
                </member>
              </members>
            </doc>
            """;

        var path = WriteFixture("string.xml", xml);
        var entries = XmlDocParser.Parse(path);

        var entry = entries["T:System.String"];
        Assert.NotNull(entry.Summary);
        Assert.Contains("Represents text", entry.Summary);
        Assert.Contains("values", entry.Summary);
        Assert.DoesNotContain("<see", entry.Summary);
    }

    [Fact]
    public void Parse_IndentedWhitespace_NormalizedToSingleSpaces()
    {
        const string xml = """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="M:Foo.Bar">
                  <summary>
                    First line.
                    Second    line   with    extra   spaces.
                  </summary>
                </member>
              </members>
            </doc>
            """;

        var path = WriteFixture("indented.xml", xml);
        var entries = XmlDocParser.Parse(path);

        var summary = entries["M:Foo.Bar"].Summary;
        Assert.Equal("First line. Second line with extra spaces.", summary);
    }

    [Fact]
    public void Parse_MissingFile_Throws()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.xml");
        Assert.Throws<FileNotFoundException>(() => XmlDocParser.Parse(missing));
    }

    [Fact]
    public void Parse_NonDocRoot_ThrowsInvalidData()
    {
        var path = WriteFixture("bad.xml", "<?xml version=\"1.0\"?><notdoc/>");
        Assert.Throws<InvalidDataException>(() => XmlDocParser.Parse(path));
    }

    [Fact]
    public void ParseForAssembly_MissingSiblingXml_ReturnsEmpty()
    {
        // A dll path with no sibling .xml — use a path that doesn't need to exist;
        // ParseForAssembly only cares about the .xml sibling.
        var dllPath = Path.Combine(_tempDir, "NoDocs.dll");
        var entries = XmlDocParser.ParseForAssembly(dllPath);

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseForAssembly_SiblingXmlExists_ReturnsParsed()
    {
        WriteFixture("HasDocs.xml", """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:HasDocs.Thing"><summary>A thing.</summary></member>
              </members>
            </doc>
            """);
        var dllPath = Path.Combine(_tempDir, "HasDocs.dll");
        var entries = XmlDocParser.ParseForAssembly(dllPath);

        Assert.Single(entries);
        Assert.Equal("A thing.", entries["T:HasDocs.Thing"].Summary);
    }
}
