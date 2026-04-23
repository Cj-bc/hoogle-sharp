namespace CSharpHoogle.Core.Indexing;

/// <summary>
/// Generates a Microsoft Learn URL for BCL member keys.
/// Non-BCL members (Unity, user assemblies, third-party) get an empty string
/// — callers that need per-assembly URL schemes should resolve those elsewhere.
/// </summary>
public static class DocUrlResolver
{
    private const string LearnApiRoot = "https://learn.microsoft.com/dotnet/api/";

    /// <summary>
    /// Resolves a documentation URL for the given XML member key.
    /// Returns empty string if the member is not in a BCL namespace
    /// (namespaces starting with <c>System</c> or <c>Microsoft</c>).
    /// </summary>
    public static string Resolve(string memberKey)
    {
        // Member keys look like: M:System.Linq.Enumerable.Select``2(System.Collections...)
        //                         T:System.Collections.Generic.List`1
        //                         P:System.String.Length
        //                         F:System.Int32.MaxValue
        if (string.IsNullOrEmpty(memberKey) || memberKey.Length < 2 || memberKey[1] != ':')
        {
            return string.Empty;
        }

        var body = memberKey.AsSpan(2);

        // Cut method parameter list.
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body[..paren];
        }

        var path = body.ToString();

        if (!IsBclNamespace(path))
        {
            return string.Empty;
        }

        // Normalize: `` `` (ECMA-335 generic-method arity) and `` ` `` (generic-type arity)
        // both map to '-' on Learn (system.collections.generic.list-1, enumerable.select-2).
        path = path.Replace("``", "-").Replace("`", "-");

        // Lowercase.
        path = path.ToLowerInvariant();

        return LearnApiRoot + path;
    }

    private static bool IsBclNamespace(string path)
    {
        return path.StartsWith("System.", StringComparison.Ordinal)
            || path.StartsWith("Microsoft.", StringComparison.Ordinal)
            || path == "System"
            || path == "Microsoft";
    }
}
