using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpHoogle.Cli;

/// <summary>
/// Walks .cs files via Roslyn syntax (no semantic model — fast, no Compilation
/// required) and emits <see cref="CachedMethod"/> records for every method
/// declaration whose containing type is non-private. The
/// <see cref="CachedMethod.Source"/> kind is <c>"source"</c> with the csproj's
/// assembly name; this lets the dedupe pass in <see cref="IndexBuilder"/>
/// recognize them as the live-edit counterpart to the assembly walk.
/// </summary>
internal static class SourceIndexBuilder
{
    /// <summary>
    /// Parses each .cs file the csproj declares and returns the methods found.
    /// <paramref name="assemblyName"/> tags the resulting <see cref="MethodSource"/>
    /// — falls back to the csproj filename when the csproj doesn't declare one.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildFromCsproj(
        string csprojPath,
        string assemblyName,
        Action<string>? progress = null)
    {
        var files = CompileItemEnumerator.Enumerate(csprojPath);
        if (files.Count == 0)
        {
            return Array.Empty<CachedMethod>();
        }

        var source = new MethodSource("source", assemblyName);
        var methods = new List<CachedMethod>();

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                // File swept out from under us mid-walk — skip.
                continue;
            }

            SyntaxTree tree;
            try
            {
                tree = CSharpSyntaxTree.ParseText(text, path: file);
            }
            catch (Exception)
            {
                // ParseText itself doesn't normally throw, but a corrupt
                // encoding or pathological input shouldn't kill the index.
                continue;
            }

            CollectMethods(tree, source, methods);
        }

        progress?.Invoke($"  {methods.Count:N0} methods from {files.Count} source files in {assemblyName}");
        return methods;
    }

    private static void CollectMethods(SyntaxTree tree, MethodSource source, List<CachedMethod> sink)
    {
        var root = tree.GetRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!IsAccessible(method))
            {
                continue;
            }

            var containingTypeNames = GetContainingTypeNames(method);
            if (containingTypeNames is null)
            {
                // Method declared in a context we don't index (top-level
                // statement helper inside Program, etc.).
                continue;
            }

            var ns = GetNamespace(method);
            var typePath = string.Join('.', containingTypeNames);
            var fullName = string.IsNullOrEmpty(ns)
                ? $"{typePath}.{method.Identifier.Text}"
                : $"{ns}.{typePath}.{method.Identifier.Text}";

            var paramTypes = method.ParameterList.Parameters
                .Select(p => p.Type is null ? "?" : FormatTypeSyntax(p.Type))
                .ToArray();

            var requiredCount = method.ParameterList.Parameters.Count;
            while (requiredCount > 0
                && method.ParameterList.Parameters[requiredCount - 1].Default is not null)
            {
                requiredCount--;
            }

            var generics = method.TypeParameterList?.Parameters
                .Select(p => p.Identifier.Text)
                .ToArray() ?? Array.Empty<string>();

            // Receiver only makes sense for instance methods. `static` and
            // extension-method declarations (the latter detected by the
            // `this` modifier on parameter 0) are skipped so the matcher's
            // synthetic-receiver slot doesn't fire for them.
            var isExtension = IsExtensionMethod(method);
            var isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            string? declaringType = null;
            var typeGenericParams = Array.Empty<string>();
            if (!isStatic && !isExtension)
            {
                var receiver = GetDeclaringTypeRef(method);
                if (receiver is not null)
                {
                    declaringType = receiver.Value.Formatted;
                    typeGenericParams = receiver.Value.GenericParams;
                }
            }

            sink.Add(new CachedMethod(
                FullName: fullName,
                ReturnType: FormatTypeSyntax(method.ReturnType),
                ParameterTypes: paramTypes,
                GenericParams: generics,
                IsExtensionMethod: isExtension,
                DocUrl: "",
                Summary: ExtractSummary(method),
                Source: source,
                RequiredParameterCount: requiredCount,
                DeclaringType: declaringType,
                TypeGenericParams: typeGenericParams));
        }
    }

    /// <summary>
    /// Returns the immediately-containing type rendered the same way
    /// <see cref="FormatTypeSyntax"/> renders other types — identifier plus
    /// optional <c>&lt;T1, T2&gt;</c> from its <c>TypeParameterList</c>.
    /// Returns null when no class/struct/record/interface ancestor exists
    /// (e.g. methods inside top-level statements).
    /// </summary>
    private static (string Formatted, string[] GenericParams)? GetDeclaringTypeRef(MethodDeclarationSyntax method)
    {
        SyntaxNode? parent = method.Parent;
        while (parent is not null)
        {
            switch (parent)
            {
                case TypeDeclarationSyntax t:
                    return (FormatTypeRef(t.Identifier.Text, t.TypeParameterList),
                            ExtractTypeParamNames(t.TypeParameterList));
                case BaseNamespaceDeclarationSyntax:
                case CompilationUnitSyntax:
                    return null;
            }
            parent = parent.Parent;
        }
        return null;
    }

    private static string FormatTypeRef(string name, TypeParameterListSyntax? typeParams)
    {
        if (typeParams is null || typeParams.Parameters.Count == 0)
        {
            return name;
        }
        var sb = new StringBuilder(name.Length + typeParams.Parameters.Count * 6);
        sb.Append(name);
        sb.Append('<');
        for (var i = 0; i < typeParams.Parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(typeParams.Parameters[i].Identifier.Text);
        }
        sb.Append('>');
        return sb.ToString();
    }

    private static string[] ExtractTypeParamNames(TypeParameterListSyntax? typeParams)
    {
        if (typeParams is null || typeParams.Parameters.Count == 0)
        {
            return Array.Empty<string>();
        }
        return typeParams.Parameters.Select(p => p.Identifier.Text).ToArray();
    }

    private static bool IsAccessible(MethodDeclarationSyntax method)
    {
        // Rule of thumb: skip private members. Internal/protected/public all
        // get indexed — same lenient policy the assembly walk uses (anything
        // visible outside its file is fair game). Default visibility on
        // class/struct members is private, so absent modifiers ≠ public.
        var hasPrivate = method.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
        var hasPublic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasInternal = method.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasProtected = method.Modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));

        if (hasPrivate)
        {
            return false;
        }

        // Inside a containing type — interfaces/records/structs default to public,
        // class members default to private. Walk up to figure out the default.
        if (hasPublic || hasInternal || hasProtected)
        {
            return true;
        }

        var containing = method.Parent;
        while (containing is not null)
        {
            switch (containing)
            {
                case InterfaceDeclarationSyntax:
                    return true;
                case ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax:
                    return false;
            }
            containing = containing.Parent;
        }

        return false;
    }

    private static IReadOnlyList<string>? GetContainingTypeNames(MethodDeclarationSyntax method)
    {
        var names = new List<string>();
        SyntaxNode? parent = method.Parent;
        while (parent is not null)
        {
            switch (parent)
            {
                case TypeDeclarationSyntax t:
                    names.Add(t.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax:
                case CompilationUnitSyntax:
                    parent = null;
                    continue;
            }
            parent = parent?.Parent;
        }

        if (names.Count == 0)
        {
            return null;
        }

        names.Reverse();
        return names;
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var parts = new List<string>();
        SyntaxNode? n = node.Parent;
        while (n is not null)
        {
            if (n is BaseNamespaceDeclarationSyntax nsDecl)
            {
                parts.Insert(0, nsDecl.Name.ToString());
            }
            n = n.Parent;
        }
        return string.Join('.', parts);
    }

    private static bool IsExtensionMethod(MethodDeclarationSyntax method)
    {
        if (method.ParameterList.Parameters.Count == 0)
        {
            return false;
        }

        var first = method.ParameterList.Parameters[0];
        return first.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword));
    }

    private static string? ExtractSummary(MethodDeclarationSyntax method)
    {
        var trivia = method.GetLeadingTrivia();
        foreach (var t in trivia)
        {
            if (!t.HasStructure) continue;

            var structure = t.GetStructure();
            if (structure is null) continue;

            // <summary>...</summary> sits inside DocumentationCommentTriviaSyntax.
            // We only need the inner text, joined and trimmed.
            var summaryNode = structure.DescendantNodes()
                .OfType<XmlElementSyntax>()
                .FirstOrDefault(e => e.StartTag.Name.LocalName.Text == "summary");

            if (summaryNode is null) continue;

            var sb = new StringBuilder();
            foreach (var child in summaryNode.Content)
            {
                if (child is XmlTextSyntax textNode)
                {
                    foreach (var token in textNode.TextTokens)
                    {
                        sb.Append(token.ValueText);
                    }
                }
            }

            var raw = sb.ToString();
            // XML doc lines are leading-whitespace-padded ("/// summary..."); collapse.
            var collapsed = string.Join(' ',
                raw.Split('\n')
                   .Select(l => l.Trim())
                   .Where(l => l.Length > 0));
            return collapsed.Length > 0 ? collapsed : null;
        }

        return null;
    }

    private static string FormatTypeSyntax(TypeSyntax type)
    {
        // ToString() on a TypeSyntax is identifier-perfect and preserves
        // generics/arrays/refs as written. Whitespace is trimmed; trailing
        // syntax trivia (newlines after `void` etc.) gets stripped.
        return type.ToString().Trim();
    }
}
