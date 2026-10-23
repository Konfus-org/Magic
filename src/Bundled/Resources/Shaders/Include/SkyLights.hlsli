// The sky's sun and moon, which a renderer hands a sky in its inputs and its own passes in its constants, whether or not
// either lights the world now. One the world has none of has no light and points straight down. Every row is 16 bytes,
// which packs the same in a storage buffer and in a pass's constants.

#ifndef MAGIC_SKY_LIGHTS_HLSLI
#define MAGIC_SKY_LIGHTS_HLSLI

struct SkyLightConstants
{
    float3 sunDirection;     // unit length, the way the sun's light travels: the sun is at -sunDirection
    float sunAngularRadius;  // radians, the sun's disc
    float3 sunIlluminance;   // lux, times the sun's colour, above the air; zero with no sun
    float moonAngularRadius; // radians, the moon's disc
    float3 moonDirection;    // unit length, the way the moon's light travels: the moon is at -moonDirection
    float padding0;
    float3 moonIlluminance;  // lux, times the moon's colour, of a full moon above the air; zero with no moon
    float padding1;
};

#endif
