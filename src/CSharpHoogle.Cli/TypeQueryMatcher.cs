namespace CSharpHoogle.Cli;

/// <summary>
/// Matches a parsed <see cref="SignatureQuery"/> against the type signature
/// of a <see cref="CachedMethod"/>.
///
/// v1 rules:
/// <list type="bullet">
///   <item>Arity must match: the query's parameter count equals the method's.</item>
///   <item>C# keyword aliases match their BCL type names (<c>int</c> ↔ <c>Int32</c> etc.).</item>
///   <item>Generic-parameter-like names on either side act as wildcards that match any type.</item>
///   <item>Otherwise, simple names must match case-insensitively and generic args / array dims must match structurally.</item>
/// </list>
/// Unification is intentionally not performed — <c>T -&gt; T</c> and <c>T -&gt; U</c>
/// both match <c>TSource -&gt; TResult</c>. That keeps the matcher cheap and is
/// fine for a "find methods with roughly this shape" search.
/// </summary>
public static class TypeQueryMatcher
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = "Boolean",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["float"] = "Single",
        ["double"] = "Double",
        ["decimal"] = "Decimal",
        ["char"] = "Char",
        ["string"] = "String",
        ["object"] = "Object",
        ["void"] = "Void",
        ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr",
    };

    public static bool Matches(SignatureQuery query, CachedMethod method)
    {
        if (query.Parameters.Count != method.ParameterTypes.Length)
        {
            return false;
        }

        var methodGenerics = method.GenericParams;

        for (var i = 0; i < query.Parameters.Count; i++)
        {
            var methodType = SafeParse(method.ParameterTypes[i]);
            if (methodType is null || !MatchType(query.Parameters[i], methodType, methodGenerics))
            {
                return false;
            }
        }

        var ret = SafeParse(method.ReturnType);
        return ret is not null && MatchType(query.Return, ret, methodGenerics);
    }

    private static bool MatchType(TypeRef query, TypeRef method, IReadOnlyList<string> methodGenerics)
    {
        // Query-side wildcard (a user-written `T`, `TSource`, etc.) matches
        // any type name — but the array suffix still has to line up, so that
        // `T -> T[]` doesn't match `int -> int`.
        if (IsQueryWildcard(query.Name))
        {
            return DimsEqual(query.ArrayDims, method.ArrayDims);
        }

        // Method-side wildcard (the method's own generic parameter) matches
        // only when the query hasn't asked for a specific shape. When the
        // user writes `Func<T, bool>` they want something function-shaped,
        // not just "anything that could be substituted for TSource".
        if (IsMethodWildcard(method.Name, methodGenerics))
        {
            return query.Args.Count == 0
                && DimsEqual(query.ArrayDims, method.ArrayDims);
        }

        if (!SimpleNameEquals(query.Name, method.Name))
        {
            return false;
        }

        if (query.Args.Count != method.Args.Count)
        {
            return false;
        }

        for (var i = 0; i < query.Args.Count; i++)
        {
            if (!MatchType(query.Args[i], method.Args[i], methodGenerics))
            {
                return false;
            }
        }

        return DimsEqual(query.ArrayDims, method.ArrayDims);
    }

    private static bool DimsEqual(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool SimpleNameEquals(string a, string b)
    {
        var na = Aliases.TryGetValue(a, out var an) ? an : a;
        var nb = Aliases.TryGetValue(b, out var bn) ? bn : b;
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Query-side wildcard heuristic: single letter (<c>T</c>, <c>a</c>) or
    /// <c>T</c>-prefixed PascalCase (<c>TSource</c>). C# keyword aliases
    /// (<c>bool</c>, <c>void</c>) must never be wildcards, which is why the
    /// rule doesn't include "all lowercase".
    /// </summary>
    private static bool IsQueryWildcard(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (Aliases.ContainsKey(name)) return false;
        if (name.Length == 1) return char.IsLetter(name[0]);
        if (name[0] == 'T' && char.IsUpper(name[1])) return true;
        return false;
    }

    private static bool IsMethodWildcard(string name, IReadOnlyList<string> methodGenerics)
    {
        foreach (var g in methodGenerics)
        {
            if (string.Equals(g, name, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static TypeRef? SafeParse(string text)
    {
        try { return TypeQuery.ParseType(text); }
        catch (FormatException) { return null; }
    }
}
