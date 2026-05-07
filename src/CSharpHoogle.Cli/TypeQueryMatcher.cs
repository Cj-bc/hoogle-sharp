namespace CSharpHoogle.Cli;

/// <summary>
/// Matches a parsed <see cref="SignatureQuery"/> against the type signature
/// of a <see cref="CachedMethod"/>.
///
/// Rules:
/// <list type="bullet">
///   <item>Query arity must lie between the method's required count and its
///     total count: trailing optional parameters (<c>= default</c>) may be
///     omitted from the query, but every required parameter must be matched.</item>
///   <item>Instance methods get a synthetic optional <em>receiver</em> slot
///     (the formatted declaring type) prepended to the candidate pool, so
///     <c>Dictionary&lt;TKey,TValue&gt; -&gt; TKey -&gt; bool</c> matches
///     <c>bool Dictionary&lt;TKey,TValue&gt;.ContainsKey(TKey)</c>. The slot
///     is optional — the same query without the receiver still matches.
///     Static and extension methods do not have this slot (extension methods
///     already place their receiver in parameter[0]).</item>
///   <item>Parameter order is not significant at the top level: the matcher
///     searches for any one-to-one assignment of query parameters to method
///     parameters that satisfies the type rules. Generic arguments inside a
///     type (e.g. <c>Func&lt;int, bool&gt;</c>) remain positional.</item>
///   <item>C# keyword aliases match their BCL type names (<c>int</c> ↔ <c>Int32</c> etc.).</item>
///   <item>Generic-parameter-like names on either side act as unification variables:
///     repeated occurrences of the same variable must bind to the same type.
///     So <c>T -&gt; T</c> matches <c>int -&gt; int</c> but not <c>int -&gt; string</c>,
///     while <c>T -&gt; U</c> matches both. Type-level generics from the declaring
///     type (e.g. <c>TKey</c> from <c>Dictionary&lt;TKey,TValue&gt;</c>) act as
///     method-side wildcards alongside method-level generics.</item>
///   <item>Otherwise, simple names must match case-insensitively and generic args / array dims must match structurally.</item>
/// </list>
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
        // Instance methods get a synthetic, OPTIONAL receiver slot prepended
        // to the candidate pool so a query like `Dictionary<TKey,TValue> -> TKey -> bool`
        // matches `bool Dictionary<TKey,TValue>.ContainsKey(TKey)`. Static
        // and extension methods leave DeclaringType null so this slot is absent.
        var hasReceiver = !string.IsNullOrEmpty(method.DeclaringType);
        var totalSlots = method.ParameterTypes.Length + (hasReceiver ? 1 : 0);

        // The receiver is optional — it does not raise the lower bound, only
        // the upper bound. So the user may include or omit it freely.
        if (query.Parameters.Count < method.RequiredParameterCount
            || query.Parameters.Count > totalSlots)
        {
            return false;
        }

        // Type-level generics (e.g. `TKey`, `TValue` from `Dictionary<TKey,TValue>`)
        // act as method-side wildcards alongside the method's own generic params,
        // so a `TKey` in the receiver and a `TKey` in a parameter unify to the
        // same bound type.
        var typeGenerics = method.TypeGenericParams ?? Array.Empty<string>();
        var methodGenerics = typeGenerics.Length == 0
            ? method.GenericParams
            : method.GenericParams.Concat(typeGenerics).ToArray();

        var receiverOffset = hasReceiver ? 1 : 0;
        var slots = new TypeRef[totalSlots];
        if (hasReceiver)
        {
            var parsedReceiver = SafeParse(method.DeclaringType!);
            if (parsedReceiver is null)
            {
                return false;
            }
            slots[0] = parsedReceiver;
        }
        for (var i = 0; i < method.ParameterTypes.Length; i++)
        {
            var parsed = SafeParse(method.ParameterTypes[i]);
            if (parsed is null)
            {
                return false;
            }
            slots[receiverOffset + i] = parsed;
        }

        var ret = SafeParse(method.ReturnType);
        if (ret is null)
        {
            return false;
        }

        // Bindings are kept per-method so one method's signature is unified
        // as a whole — variables carry their meaning across parameters and
        // return type.
        var bindings = new UnificationState();
        var used = new bool[slots.Length];
        return TryAssignParameters(query, slots, ret, methodGenerics, receiverOffset, method.RequiredParameterCount, bindings, used, 0);
    }

    /// <summary>
    /// Backtracking search for a one-to-one assignment of query parameters
    /// to method parameters that the type rules accept. Order at the top
    /// level is not significant, so the user's <c>int -&gt; string -&gt; bool</c>
    /// matches a method that takes <c>(string, int)</c> as well as one that
    /// takes <c>(int, string)</c>. Bindings made during a failed branch are
    /// rolled back before trying the next slot.
    /// </summary>
    private static bool TryAssignParameters(
        SignatureQuery query,
        IReadOnlyList<TypeRef> methodParams,
        TypeRef methodReturn,
        IReadOnlyList<string> methodGenerics,
        int receiverOffset,
        int requiredCount,
        UnificationState bindings,
        bool[] used,
        int qIndex)
    {
        if (qIndex == query.Parameters.Count)
        {
            // Required slots live at [receiverOffset, receiverOffset + requiredCount).
            // The receiver slot (index 0 when present) is always optional, and
            // trailing optional parameters sit past the required range — both
            // may go unmatched without invalidating the assignment.
            for (var j = receiverOffset; j < receiverOffset + requiredCount; j++)
            {
                if (!used[j]) return false;
            }
            return MatchType(query.Return, methodReturn, methodGenerics, bindings);
        }

        var queryParam = query.Parameters[qIndex];
        for (var j = 0; j < methodParams.Count; j++)
        {
            if (used[j]) continue;

            var snapshot = bindings.Snapshot();
            if (MatchType(queryParam, methodParams[j], methodGenerics, bindings))
            {
                used[j] = true;
                if (TryAssignParameters(query, methodParams, methodReturn, methodGenerics, receiverOffset, requiredCount, bindings, used, qIndex + 1))
                {
                    return true;
                }
                used[j] = false;
            }
            bindings.Restore(snapshot);
        }
        return false;
    }

    private static bool MatchType(TypeRef query, TypeRef method, IReadOnlyList<string> methodGenerics, UnificationState bindings)
    {
        // Query-side wildcard (a user-written `T`, `TSource`, etc.) unifies
        // with the method position. The array suffix still has to line up
        // (so `T -> T[]` doesn't match `int -> int`); we strip those shared
        // dims and bind T to the element type, so repeated Ts compare apples
        // to apples.
        if (IsQueryWildcard(query.Name))
        {
            if (!DimsEqual(query.ArrayDims, method.ArrayDims))
            {
                return false;
            }
            var methodElement = method with { ArrayDims = Array.Empty<int>() };
            if (bindings.Query.TryGetValue(query.Name, out var bound))
            {
                return TypeRefEquals(bound, methodElement);
            }
            bindings.Query[query.Name] = methodElement;
            return true;
        }

        // Method-side wildcard (the method's own generic parameter) matches
        // only when the query hasn't asked for a specific shape. When the
        // user writes `Func<T, bool>` they want something function-shaped,
        // not just "anything that could be substituted for TSource". Repeated
        // occurrences of the same method variable must likewise unify.
        if (IsMethodWildcard(method.Name, methodGenerics))
        {
            if (query.Args.Count != 0 || !DimsEqual(query.ArrayDims, method.ArrayDims))
            {
                return false;
            }
            var queryElement = query with { ArrayDims = Array.Empty<int>() };
            if (bindings.Method.TryGetValue(method.Name, out var bound))
            {
                return TypeRefEquals(bound, queryElement);
            }
            bindings.Method[method.Name] = queryElement;
            return true;
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
            if (!MatchType(query.Args[i], method.Args[i], methodGenerics, bindings))
            {
                return false;
            }
        }

        return DimsEqual(query.ArrayDims, method.ArrayDims);
    }

    /// <summary>
    /// Structural equality between two stored bindings. Uses
    /// <see cref="SimpleNameEquals"/> so the BCL alias folding applies here
    /// too — once a variable is bound to <c>int</c>, a later <c>Int32</c>
    /// is still a match.
    /// </summary>
    private static bool TypeRefEquals(TypeRef a, TypeRef b)
    {
        if (!DimsEqual(a.ArrayDims, b.ArrayDims)) return false;
        if (!SimpleNameEquals(a.Name, b.Name)) return false;
        if (a.Args.Count != b.Args.Count) return false;
        for (var i = 0; i < a.Args.Count; i++)
        {
            if (!TypeRefEquals(a.Args[i], b.Args[i])) return false;
        }
        return true;
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

    /// <summary>
    /// Per-method bindings built up during unification. Query and method
    /// variables live in separate namespaces so a user's <c>T</c> and a
    /// method's <c>T</c> generic don't accidentally collide.
    /// </summary>
    private sealed class UnificationState
    {
        public Dictionary<string, TypeRef> Query { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypeRef> Method { get; } = new(StringComparer.Ordinal);

        public BindingsSnapshot Snapshot() => new(Query.Count, Method.Count, Query.Keys.ToArray(), Method.Keys.ToArray());

        public void Restore(BindingsSnapshot snap)
        {
            if (Query.Count != snap.QueryCount)
            {
                foreach (var key in Query.Keys.ToArray())
                {
                    if (Array.IndexOf(snap.QueryKeys, key) < 0) Query.Remove(key);
                }
            }
            if (Method.Count != snap.MethodCount)
            {
                foreach (var key in Method.Keys.ToArray())
                {
                    if (Array.IndexOf(snap.MethodKeys, key) < 0) Method.Remove(key);
                }
            }
        }
    }

    private readonly record struct BindingsSnapshot(int QueryCount, int MethodCount, string[] QueryKeys, string[] MethodKeys);
}
