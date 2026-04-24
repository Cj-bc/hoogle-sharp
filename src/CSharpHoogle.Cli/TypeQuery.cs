namespace CSharpHoogle.Cli;

/// <summary>
/// Structural representation of a C# type for signature matching.
/// Produced by both <see cref="TypeQuery.ParseType"/> (from query text)
/// and <see cref="TypeQuery.ParseType"/> (from the formatted strings stored
/// on <see cref="CachedMethod"/>).
/// </summary>
public sealed record TypeRef(
    string Name,
    IReadOnlyList<TypeRef> Args,
    IReadOnlyList<int> ArrayDims);

/// <summary>
/// Signature query: zero or more parameter types followed by a return type,
/// as in <c>A -&gt; B -&gt; C</c>.
/// </summary>
public sealed record SignatureQuery(
    IReadOnlyList<TypeRef> Parameters,
    TypeRef Return);

/// <summary>
/// Parser for signature queries and for the formatted type strings stored
/// on <see cref="CachedMethod"/>. The two sides must parse identically so
/// structural matching works without a separate normalization pass.
/// </summary>
public static class TypeQuery
{
    public static bool LooksLikeSignatureQuery(string text)
        => text.Contains("->", StringComparison.Ordinal);

    /// <summary>
    /// Parses <paramref name="text"/> as <c>T1 -&gt; T2 -&gt; ... -&gt; TN</c>.
    /// The last type is the return type; preceding types are parameters.
    /// Throws <see cref="FormatException"/> on malformed input.
    /// </summary>
    public static SignatureQuery ParseSignature(string text)
    {
        var lex = new Lexer(text);
        var types = new List<TypeRef> { ParseType(lex) };
        while (lex.AcceptArrow())
        {
            types.Add(ParseType(lex));
        }
        if (!lex.Eof)
        {
            throw new FormatException($"Unexpected trailing input at offset {lex.Position}.");
        }
        var ret = types[^1];
        var prms = types.Count == 1 ? Array.Empty<TypeRef>() : types.GetRange(0, types.Count - 1).ToArray();
        return new SignatureQuery(prms, ret);
    }

    /// <summary>
    /// Parses a single type reference, e.g. <c>IEnumerable&lt;T&gt;[]</c>.
    /// </summary>
    public static TypeRef ParseType(string text)
    {
        var lex = new Lexer(text);
        var t = ParseType(lex);
        if (!lex.Eof)
        {
            throw new FormatException($"Unexpected trailing input at offset {lex.Position}.");
        }
        return t;
    }

    private static TypeRef ParseType(Lexer lex)
    {
        // Optional "ref " prefix produced by TypeNameFormatter for by-ref parameters.
        // We simply drop it so queries don't need to mention it.
        lex.AcceptWord("ref");

        var name = lex.ReadIdent();
        if (string.IsNullOrEmpty(name))
        {
            throw new FormatException($"Expected type name at offset {lex.Position}.");
        }

        // Strip namespace — the method side is already simple-named, so the
        // query's "System.Int32" should collapse to "Int32" before matching.
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        IReadOnlyList<TypeRef> args = Array.Empty<TypeRef>();
        if (lex.Accept('<'))
        {
            var list = new List<TypeRef> { ParseType(lex) };
            while (lex.Accept(','))
            {
                list.Add(ParseType(lex));
            }
            lex.Expect('>');
            args = list;
        }

        // Pointer suffix — ignore for v1 signature matching.
        while (lex.Accept('*')) { }

        var dims = new List<int>();
        while (lex.Accept('['))
        {
            var rank = 1;
            while (lex.Accept(','))
            {
                rank++;
            }
            lex.Expect(']');
            dims.Add(rank);
        }

        return new TypeRef(name, args, dims);
    }

    private sealed class Lexer
    {
        private readonly string _s;
        private int _i;

        public Lexer(string s) { _s = s; _i = 0; }
        public int Position => _i;

        public bool Eof
        {
            get { SkipWs(); return _i >= _s.Length; }
        }

        public bool Accept(char c)
        {
            SkipWs();
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        public void Expect(char c)
        {
            if (!Accept(c))
            {
                throw new FormatException($"Expected '{c}' at offset {_i}.");
            }
        }

        public bool AcceptArrow()
        {
            SkipWs();
            if (_i + 1 < _s.Length && _s[_i] == '-' && _s[_i + 1] == '>')
            {
                _i += 2;
                return true;
            }
            return false;
        }

        public bool AcceptWord(string word)
        {
            SkipWs();
            if (_i + word.Length <= _s.Length
                && string.CompareOrdinal(_s, _i, word, 0, word.Length) == 0
                && (_i + word.Length == _s.Length || !IsIdentChar(_s[_i + word.Length])))
            {
                _i += word.Length;
                return true;
            }
            return false;
        }

        public string ReadIdent()
        {
            SkipWs();
            var start = _i;
            while (_i < _s.Length && IsIdentChar(_s[_i]))
            {
                _i++;
            }
            return _s[start.._i];
        }

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private static bool IsIdentChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '.';
    }
}
