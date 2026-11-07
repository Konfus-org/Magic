// The air of the world: a haze that scatters the lights, thickest at its height and thinning above it, so a light's
// beam shows and the far fades. A fog material's params are this struct's fields by name, "density", "halfHeight",
// "height", "color" and "anisotropy", which whatever draws the air reads.
module "Weather/Fog";

public struct Fog
{
    // How much of the light a metre of air at its height scatters, 0 to 1.
    public float density;
    // Metres above its height at which the air is half as thick.
    public float halfHeight;
    // Where the air is thickest, in metres.
    public float height;
    // What the air scatters light into: white for clean haze.
    public float4 color;
    // How much the air scatters light onward rather than everywhere, -1 to 1: 0 evenly, towards 1 mostly ahead, as
    // mist does.
    public float anisotropy;
}
