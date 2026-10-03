// A surface that parses (MaterialParams and EvaluateSurface are there) but does not compile: DXC rejects
// the body, so its pipeline class draws the shader-failure glow. Fix the typo while the sample runs to
// watch the cube turn into a real surface.

struct MaterialParams
{
    Color color = Color(0.2, 0.8, 0.2, 1.0);
};

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);
    surface.baseColor = material.color.rgb * this_is_not_declared;
    return surface;
}
