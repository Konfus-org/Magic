// The world's weather, as its Weather effects say, which a renderer hands every shader that needs it: surfaces and
// skies in their inputs, its own passes through the world's constants. Still and clear without any. A new weather value
// is a field here and in its C# twin, which the renderer fills from its effect.

module "Core/Weather";

public struct WeatherConstants
{
    float3 wind;             // metres per second: its direction and strength
    float coverage;          // how much of the sky clouds cover, 0 clear to 1 overcast
    float3 cloudColor;       // the clouds where the sun lights them, linear
    float density;           // how thick they are, 0 wisps to 1 thick
    float3 cloudShadow;      // the clouds in their own shade and underneath, linear
}
