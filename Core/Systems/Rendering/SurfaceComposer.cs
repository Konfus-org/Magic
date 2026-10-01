using Magic.Contexts.Rendering;
using Magic.Utils;
using System.Text;

namespace Magic.Systems.Rendering;

/// <summary>
/// Builds the HLSL of one material pipeline stage: the variant defines, the surface contract, the surface
/// shader (stripped of what DXC must not see), the generated loader, then the template. <c>#line</c>
/// directives keep DXC's messages pointing at the real files. Pure text, so the tests run it without a device.
/// </summary>
internal static class SurfaceComposer
{
    public static Result<SurfaceSource> Read(ulong id, string path, string text)
    {
        Result<ParamLayout> layout = ParamLayout.Parse(text, "MaterialParams", allowTextures: true);
        if (layout.Failed)
            return Result<SurfaceSource>.Failure($"{path}: {layout.Message}");
        if (!text.Contains("EvaluateSurface", StringComparison.Ordinal))
            return Result<SurfaceSource>.Failure($"{path}: no EvaluateSurface(SurfaceInputs, MaterialParams) function.");

        string stripped = ParamLayout.BlankDefaults(text, layout.Payload.StructSpan);
        return Result<SurfaceSource>.Success(new SurfaceSource(id, path, stripped, layout.Payload));
    }

    public static string Compose(SurfaceSource surface, SurfaceVariant variant, string templatePath, string templateText)
    {
        StringBuilder sb = new(surface.Stripped.Length + templateText.Length + 512);
        sb.Append("#define SURFACE_MASKED ").Append(variant.HasFlag(SurfaceVariant.Masked) ? '1' : '0').Append('\n');
        sb.Append("#define SURFACE_DOUBLE_SIDED ").Append(variant.HasFlag(SurfaceVariant.DoubleSided) ? '1' : '0').Append('\n');
        if (variant.HasFlag(SurfaceVariant.FailureForced))
            sb.Append("#define FAILURE_FORCE 1\n");
        sb.Append("#include \"Include/Surface.hlsli\"\n");
        sb.Append("#line 1 \"").Append(surface.Path).Append("\"\n");
        sb.Append(surface.Stripped);
        if (!surface.Stripped.EndsWith('\n'))
            sb.Append('\n');
        sb.Append("#line 1 \"").Append(surface.Path).Append(".loader\"\n");
        sb.Append(surface.Layout.EmitLoader("LoadMaterialParams", "Materials"));
        sb.Append("#line 1 \"").Append(templatePath).Append("\"\n");
        sb.Append(templateText);
        return sb.ToString();
    }
}
