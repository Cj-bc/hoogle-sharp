using CSharpHoogle.Core.Models;

namespace CSharpHoogle.Core.Storage;

/// <summary>
/// Stores and retrieves <see cref="DocEntry"/> records keyed by XML member name.
/// </summary>
public interface IDocEntryRepository
{
    DocEntry? Get(string memberKey);
    void Store(IEnumerable<DocEntry> entries);
}
