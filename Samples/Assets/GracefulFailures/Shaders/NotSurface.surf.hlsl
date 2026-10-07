// Named like a surface but not one: there is no EvaluateSurface, so the renderer turns the shader away before it
// is ever compiled and its materials draw as "Not a surface shader". Add the function to watch the cube heal.

#include "Include/Surface.hlsli"

struct MaterialParams
{
    Color color = Color(0.2, 0.4, 0.9, 1.0);
};
