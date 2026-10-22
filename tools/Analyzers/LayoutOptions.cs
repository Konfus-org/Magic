using System.Globalization;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Magic.Tools.Analyzers;

/// <summary>
/// The .editorconfig settings a layout rule measures lines by: the longest a line may be, and how far one indent goes.
/// </summary>
public static class LayoutOptions
{
    /// <summary>
    /// Reads both for a file. A file with no line length set has nothing to measure against, and no rule that needs
    /// one checks it.
    /// </summary>
    public static bool TryRead(AnalyzerConfigOptions options, out int maxLineLength, out int indentSize)
    {
        maxLineLength = 0;
        indentSize = 4;
        if (!options.TryGetValue("max_line_length", out string? maxText) || !TryParse(maxText, out maxLineLength))
            return false;

        if (options.TryGetValue("indent_size", out string? indentText) && TryParse(indentText, out int indent))
            indentSize = indent;

        return true;
    }

    private static bool TryParse(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
