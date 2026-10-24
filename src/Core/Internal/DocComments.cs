using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Internal;

/// <summary>
/// The doc comments the build writes into a documentation file beside every assembly, read so a settings UI shows a
/// setting's summary and nothing repeats the comment (<see cref="Services.Settings"/>).
/// </summary>
internal static class DocComments
{
    /// <summary>
    /// Every member's summary in <paramref name="assembly"/>'s documentation file, as plain text, by the id the file
    /// gives it; none when it has no such file, or one that cannot be read.
    /// </summary>
    internal static Dictionary<string, string> Read(IFileSystem files, Assembly assembly)
    {
        Dictionary<string, string> summaries = new(StringComparer.Ordinal);
        string path = Path.ChangeExtension(assembly.Location, ".xml");

        if (!files.FileExists(path)) return summaries;

        Result<byte[]> bytes = files.Read(path);

        if (!bytes.Ok) return summaries;

        // What is trimmed is the byte order mark a file may start with, which does not show here.
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(bytes.Value).TrimStart('﻿'));
        }
        catch (XmlException)
        {
            return summaries;
        }

        // Each member's summary as plain text, every run of white space one space, as the file wraps and indents it.
        StringBuilder text = new();
        foreach (XElement member in document.Descendants("member"))
        {
            if (member.Attribute("name") is not { } id || member.Element("summary") is not { } summary) continue;

            text.Clear();
            AppendText(text, summary);
            summaries[id.Value] = string.Join(
                ' ',
                text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return summaries;
    }

    /// <summary>
    /// <paramref name="property"/>'s summary among <paramref name="summaries"/>, its assembly's
    /// (<see cref="Read"/>); empty when it has none.
    /// </summary>
    internal static string GetSummary(Dictionary<string, string> summaries, PropertyInfo property)
    {
        Type declaring = property.DeclaringType
            ?? throw new ArgumentException($"{property.Name} belongs to no type.", nameof(property));

        // The id the documentation file gives a property: P:, its type's full name with a dot for the + before a
        // nested type, then its name.
        return summaries.GetValueOrDefault($"P:{declaring.FullName?.Replace('+', '.')}.{property.Name}")
            ?? string.Empty;
    }

    /// <summary>
    /// The text of <paramref name="node"/>, onto <paramref name="text"/>: a reference is the last part of what it
    /// names, and a reference to a parameter is its name.
    /// </summary>
    private static void AppendText(StringBuilder text, XNode node)
    {
        if (node is XText run)
        {
            text.Append(run.Value);

            return;
        }

        if (node is not XElement element) return;

        if (element.Attribute("cref") is { } cref)
        {
            text.Append(cref.Value[(cref.Value.LastIndexOf('.') + 1)..]);

            return;
        }

        if (element.Attribute("name") is { } name && element.IsEmpty)
        {
            text.Append(name.Value);

            return;
        }

        foreach (XNode child in element.Nodes())
        {
            AppendText(text, child);
        }
    }
}
