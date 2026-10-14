// A cube texture's faces, as every API lays them out: +X, -X, +Y, -Y, +Z, -Z, a face's uv from its top left.

#ifndef MAGIC_CUBE_HLSLI
#define MAGIC_CUBE_HLSLI

// The unit direction a point of a face looks along.
float3 CubeDirection(uint face, float2 uv)
{
    float2 st = uv * 2.0 - 1.0;
    float3 direction = float3(st.x, -st.y, 1.0);
    [branch] if (face == 0u)
    {
        direction = float3(1.0, -st.y, -st.x);
    }
    else if (face == 1u)
    {
        direction = float3(-1.0, -st.y, st.x);
    }
    else if (face == 2u)
    {
        direction = float3(st.x, 1.0, st.y);
    }
    else if (face == 3u)
    {
        direction = float3(st.x, -1.0, -st.y);
    }
    else if (face == 5u)
    {
        direction = float3(-st.x, -st.y, -1.0);
    }

    return normalize(direction);
}

#endif
