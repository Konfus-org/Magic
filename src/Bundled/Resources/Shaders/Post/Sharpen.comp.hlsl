// Contrast-adaptive sharpening (Lottes 2019, AMD FidelityFX CAS), a thread a pixel of the view as it is shown
// (Sharpened): each pixel is pushed away from the mean of the four beside it, a negative lobe on them, by as much as
// the view was upscaled, so a view drawn at its target's size or larger, or shown as it is drawn, is left as it is. The
// lobe is weighed down where the five already span the range, an edge at full contrast, which is sharp enough, and
// the result is kept within what the five hold, so an edge steepens without a halo. It works on the colour through a
// reversible tonemap (Karis 2013) and undoes it after, so a bright highlight weighs no more than white and does not
// ring. It reads the upscale (Taa), never what the upscale keeps for the next frame, so nothing sharpened feeds back.

#include "Include/Math.hlsli"

struct PassParams
{
    // How hard a view upscaled by half again or more is sharpened, 0 to 1: the lobe on the neighbours, a fifth of
    // the centre at 1.
    float strength = 0.5;
};

// The view as it is shown at a pixel: the upscale's, else the scene colour as it was drawn, scaled to it.
float3 ShownAt(int2 at)
{
    int2 clamped = clamp(at, int2(0, 0), (int2)OutputSize - 1);
#if defined(HAS_Taa)
    return max(Taa.Load(int3(clamped, 0)).rgb, 0.0);
#else
    return max(Hdr.SampleLevel(HdrSampler, ((float2)clamped + 0.5) / OutputSize, 0.0).rgb, 0.0);
#endif
}

// Any colour into [0, 1) and back, exactly: over one more than its largest channel.
float3 Compress(float3 color)
{
    return color / (1.0 + Max3(color));
}

float3 Expand(float3 color)
{
    return color / max(1.0 - Max3(color), 1e-4);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)OutputSize))
    {
        return;
    }

    PassParams params = LoadPassParams();
    int2 at = (int2)id.xy;
    float3 centre = ShownAt(at);
    float upscaled = OutputSize.y / max(ViewSize.y, 1.0);
    float amount = saturate(params.strength) * saturate((upscaled - 1.0) * 2.0);
    [branch] if (amount <= 0.0)
    {
        Sharpened[id.xy] = float4(centre, 1.0);
        return;
    }

    float3 middle = Compress(centre);
    float3 up = Compress(ShownAt(at + int2(0, -1)));
    float3 left = Compress(ShownAt(at + int2(-1, 0)));
    float3 right = Compress(ShownAt(at + int2(1, 0)));
    float3 down = Compress(ShownAt(at + int2(0, 1)));
    float3 least = min(middle, min(min(up, left), min(right, down)));
    float3 most = max(middle, max(max(up, left), max(right, down)));

    // How far the five could go either way before leaving [0, 1], over the brightest: none left where they already
    // span it.
    float3 room = sqrt(saturate(min(least, 1.0 - most) / max(most, 1e-5)));
    float3 lobe = -0.2 * amount * room;
    float3 sharpened = (middle + lobe * (up + left + right + down)) / (1.0 + 4.0 * lobe);
    Sharpened[id.xy] = float4(Expand(clamp(sharpened, least, most)), 1.0);
}
