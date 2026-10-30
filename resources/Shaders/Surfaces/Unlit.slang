// A surface that ignores lighting: its colour is emitted as is, and it neither scatters nor reflects (a black base
// and a specular of 0). Useful for debug geometry and UI-like things in the world.

module "Surfaces/Unlit";

import Core.Surface;

public struct Unlit : ISurface
{
    [GiEmissive] Color color = { 1.0, 1.0, 1.0, 1.0 };
    [GiColorMap] TextureRef colorMap = TextureNone;
    float alphaCutoff = 0.5;

    public Surface Evaluate(SurfaceInputs input)
    {
        Surface surface = CreateDefaultSurface(input);

        float4 color = this.color.rgba;
        [branch] if (colorMap.isValid)
        {
            color *= input.Sample(colorMap);
        }

        surface.baseColor = float3(0.0, 0.0, 0.0);
        surface.specular = 0.0;
        surface.alpha = color.a;
        surface.emissive = color.rgb;
        surface.alphaCutoff = alphaCutoff;
        return surface;
    }
}
