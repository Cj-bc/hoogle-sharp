using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpHoogle.Core.Models;

namespace CSharpHoogle.Core.Parsing;

/// <summary>
/// Parses .NET XML doc comment files into <see cref="DocEntry"/> dictionaries,
/// keyed by XML member name (e.g. "M:System.Linq.Enumerable.Select``2(...)").
/// </summary>
public static class XmlDocParser
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parses the specified XML doc file.
    /// Throws <see cref="FileNotFoundException"/> if the file is missing,
    /// or <see cref="InvalidDataException"/> if the root element is not &lt;doc&gt;.
    /// </summary>
    public static IReadOnlyDictionary<string, DocEntry> Parse(string xmlFilePath)
    {
        if (!File.Exists(xmlFilePath))
        {
            throw new FileNotFoundException($"XML doc file not found: {xmlFilePath}", xmlFilePath);
        }

        var doc = XDocument.Load(xmlFilePath);
        if (doc.Root?.Name.LocalName != "doc")
        {
            throw new InvalidDataException(
                $"Root element is not <doc>: '{doc.Root?.Name.LocalName}' in {xmlFilePath}");
        }

        var result = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
        var members = doc.Root.Element("members");
        if (members is null)
        {
            return result;
        }

        foreach (var member in members.Elements("member"))
        {
            var key = member.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var paramDict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var param in member.Elements("param"))
            {
                var paramName = param.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(paramName))
                {
                    continue;
                }

                paramDict[paramName] = Normalize(param.Value);
            }

            result[key] = new DocEntry(
                MemberKey: key,
                Summary: ReadElement(member, "summary"),
                Returns: ReadElement(member, "returns"),
                Params: paramDict,
                Remarks: ReadElement(member, "remarks"),
                Example: ReadElement(member, "example"));
        }

        return result;
    }

    /// <summary>
    /// Parses the XML doc file that sits next to the specified assembly
    /// (same path, extension swapped from .dll to .xml).
    /// Returns an empty dictionary if the XML file does not exist — many
    /// assemblies ship without documentation, and callers should not have
    /// to handle that as an error.
    /// </summary>
    public static IReadOnlyDictionary<string, DocEntry> ParseForAssembly(string dllPath)
    {
        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
        {
            return new Dictionary<string, DocEntry>(StringComparer.Ordinal);
        }

        return Parse(xmlPath);
    }

    private static string? ReadElement(XElement member, string name)
    {
        var element = member.Element(name);
        if (element is null)
        {
            return null;
        }

        return Normalize(element.Value);
    }

    private static string Normalize(string raw)
    {
        return WhitespaceRun.Replace(raw.Trim(), " ");
    }
}
