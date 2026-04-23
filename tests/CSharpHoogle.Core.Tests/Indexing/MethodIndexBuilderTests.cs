using CSharpHoogle.Core.Indexing;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Reflection;
using CSharpHoogle.Core.Storage;

namespace CSharpHoogle.Core.Tests.Indexing;

public class MethodIndexBuilderTests
{
    private static string SystemLinqDll =>
        Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Linq.dll");

    [Fact]
    public void BuildFromAssembly_SystemLinq_FindsEnumerableSelect()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var selects = entries
            .Where(e => e.FullName == "System.Linq.Enumerable.Select")
            .ToList();

        Assert.NotEmpty(selects);
    }

    [Fact]
    public void BuildFromAssembly_EnumerableSelect_IsExtensionMethod()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.FirstOrDefault(e =>
            e.FullName == "System.Linq.Enumerable.Select" &&
            e.GenericParams.Length == 2 &&
            e.ParameterTypes.Length == 2);

        Assert.NotNull(select);
        Assert.True(select!.IsExtensionMethod);
    }

    [Fact]
    public void BuildFromAssembly_GenericMethod_ExposesGenericParams()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.First(e =>
            e.FullName == "System.Linq.Enumerable.Select" &&
            e.GenericParams.Length == 2);

        // Enumerable.Select<TSource, TResult>(...) — the names come from the metadata,
        // which preserves the original TSource / TResult identifiers.
        Assert.Contains("TSource", select.GenericParams);
        Assert.Contains("TResult", select.GenericParams);
    }

    [Fact]
    public void BuildFromAssembly_WithDocRepo_AttachesDocEntry()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);

        // Seed the repo with a hand-crafted DocEntry whose key matches what
        // LoxSmoke.DocXml will generate for Enumerable.Select<TSource,TResult>.
        var docs = new InMemoryDocEntryRepository();
        const string selectKey =
            "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})";
        docs.Store(new[]
        {
            new DocEntry(
                MemberKey: selectKey,
                Summary: "SENTINEL SUMMARY",
                Returns: null,
                Params: new Dictionary<string, string>(),
                Remarks: null,
                Example: null),
        });

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var attached = entries.FirstOrDefault(e => e.Doc?.Summary == "SENTINEL SUMMARY");
        Assert.NotNull(attached);
        Assert.Equal("System.Linq.Enumerable.Select", attached!.FullName);
    }

    [Fact]
    public void BuildFromAssembly_DocUrl_PopulatedForBclMember()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.First(e => e.FullName == "System.Linq.Enumerable.Select");

        Assert.StartsWith("https://learn.microsoft.com/dotnet/api/system.linq.enumerable.select", select.DocUrl);
    }

    [Fact]
    public void BuildFromAssembly_Path_CreatesThrowawayLoaderWhenNoneProvided()
    {
        var docs = new InMemoryDocEntryRepository();
        var entries = MethodIndexBuilder.BuildFromAssembly(SystemLinqDll, docs);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.FullName == "System.Linq.Enumerable.Select");
    }
}
