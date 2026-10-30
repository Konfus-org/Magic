// The sky a broken one is drawn as, which its failure material, Materials/Failures/Sky.mat, names and a renderer draws
// in a broken sky's place (Assets.Failure<Material>): magenta breathing in the view, given off as it is so it shows
// whatever the exposure, as the failure surface does, and never taken for a real sky. Into the light from everywhere
// it is a plain grey sky as bright as the sun makes it, so the world it lights stays lit as if the sky were there,
// rather than taking on the failure's colour.

module "Skies/Failure";

import Core.Failure;
import Core.Sky;

public struct Failure : ISky
{
    float captured = 0.03;   // the grey sky's nits per lux of the sun, as it is captured

    public SkyRadiance Evaluate(SkyInputs input)
    {
        SkyRadiance light;
        light.nits = float3(1.0, 1.0, 1.0) * (GetLuminance(input.celestial.sunIlluminance) * captured);
        light.discs = float3(0.0, 0.0, 0.0);
        light.emissive = float3(0.0, 0.0, 0.0);
        if (!input.isCapture)
        {
            light.nits = float3(0.0, 0.0, 0.0);
            light.emissive = GetFailureGlow(input.frame.realSec);
        }

        return light;
    }
}
