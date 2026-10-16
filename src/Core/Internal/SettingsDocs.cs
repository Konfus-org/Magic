using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Internal;

/// <summary>
/// What each setting is, as its doc comment says it: the build writes every assembly's comments into a documentation
/// file beside it, so a settings UI shows a setting's summary, and nothing repeats the comment. A file is read the
/// first time one of its assembly's settings is asked about; an assembly without one has no summaries.
/// </summary>
internal sealed class SettingsDocs(IFileSystem files)
{
    private readonly Dictionary<Assembly, Dictionary<string, string>> _summaries = [];

    /// <summary>
    /// <paramref name="property"/>'s summary as plain text; empty when it has none.
    /// </summary>
    public string SummaryOf(PropertyInfo property)
    {
        Type declaring = property.DeclaringType ?? throw new ArgumentException($"{property.Name} belongs to no type.", nameof(property));
        if (!_summaries.TryGetValue(declaring.Assembly, out Dictionary<string, string>? summaries))
        {
            summaries = Read(declaring.Assembly);
            _summaries[declaring.Assembly] = summaries;
        }

        return summaries.GetValueOrDefault($"P:{declaring.FullName?.Replace('+', '.')}.{property.Name}") ?? string.Empty;
    }

    /// <summary>
    /// The text of <paramref name="node"/>: a reference is the last part of what it names, and every run of white space
    /// one space.
    /// </summary>
    private static void AppendText(StringBuilder text, XNode node)
    {
        if (node is XText run)
        {
            text.Append(run.Value);
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

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

    /// <summary>
    /// Every member's summary in <paramref name="assembly"/>'s documentation file, by the id the file gives it.
    /// </summary>
    private Dictionary<string, string> Read(Assembly assembly)
    {
        Dictionary<string, string> summaries = new(StringComparer.Ordinal);
        string path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!files.FileExists(path))
        {
            return summaries;
        }

        Result<byte[]> bytes = files.Read(path);
        if (!bytes.Ok)
        {
            return summaries;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(bytes.Value).TrimStart('﻿'));
        }
        catch (XmlException)
        {
            return summaries;
        }

        StringBuilder text = new();
        foreach (XElement member in document.Descendants("member"))
        {
            if (member.Attribute("name") is not { } id || member.Element("summary") is not { } summary)
            {
                continue;
            }

            text.Clear();
            AppendText(text, summary);
            summaries[id.Value] = string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return summaries;
    }
}
