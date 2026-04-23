using CSharpHoogle.Core.Models;

namespace CSharpHoogle.Core.Storage;

/// <summary>
/// In-process dictionary-backed repository.
/// Not thread-safe — synchronize externally if shared between threads.
/// </summary>
public sealed class InMemoryDocEntryRepository : IDocEntryRepository
{
    private readonly Dictionary<string, DocEntry> _entries = new(StringComparer.Ordinal);

    public DocEntry? Get(string memberKey)
    {
        return _entries.TryGetValue(memberKey, out var entry) ? entry : null;
    }

    public void Store(IEnumerable<DocEntry> entries)
    {
        foreach (var entry in entries)
        {
            _entries[entry.MemberKey] = entry;
        }
    }
}
