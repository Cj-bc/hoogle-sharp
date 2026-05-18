namespace CSharpHoogle.Cli;

static class CSharpKeywordAliases
{
    private static readonly Dictionary<string, string> _sensitive = new(StringComparer.Ordinal)
    {
        ["bool"]    = "Boolean",
        ["byte"]    = "Byte",
        ["sbyte"]   = "SByte",
        ["short"]   = "Int16",
        ["ushort"]  = "UInt16",
        ["int"]     = "Int32",
        ["uint"]    = "UInt32",
        ["long"]    = "Int64",
        ["ulong"]   = "UInt64",
        ["float"]   = "Single",
        ["double"]  = "Double",
        ["decimal"] = "Decimal",
        ["char"]    = "Char",
        ["string"]  = "String",
        ["object"]  = "Object",
        ["void"]    = "Void",
        ["nint"]    = "IntPtr",
        ["nuint"]   = "UIntPtr",
    };

    private static readonly Dictionary<string, string> _insensitive =
        new(_sensitive, StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCanonical(string token, out string canonical) =>
        _sensitive.TryGetValue(token, out canonical!);

    public static bool TryGetCanonicalIgnoreCase(string token, out string canonical) =>
        _insensitive.TryGetValue(token, out canonical!);
}
