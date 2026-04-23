using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Storage;

namespace CSharpHoogle.Core.Tests.Storage;

public class InMemoryDocEntryRepositoryTests
{
    private static DocEntry Make(string key, string summary) => new(
        MemberKey: key,
        Summary: summary,
        Returns: null,
        Params: new Dictionary<string, string>(),
        Remarks: null,
        Example: null);

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        var repo = new InMemoryDocEntryRepository();
        Assert.Null(repo.Get("T:Missing"));
    }

    [Fact]
    public void Store_ThenGet_ReturnsEntry()
    {
        var repo = new InMemoryDocEntryRepository();
        var entry = Make("T:Foo", "foo summary");

        repo.Store(new[] { entry });

        Assert.Equal(entry, repo.Get("T:Foo"));
    }

    [Fact]
    public void Store_DuplicateKey_Upserts()
    {
        var repo = new InMemoryDocEntryRepository();
        repo.Store(new[] { Make("T:Foo", "first") });
        repo.Store(new[] { Make("T:Foo", "second") });

        Assert.Equal("second", repo.Get("T:Foo")!.Summary);
    }
}
