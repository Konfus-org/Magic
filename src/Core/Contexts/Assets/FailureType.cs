namespace Magic.Contexts.Assets;

/// <summary>
/// What broke, which picks the asset drawn in a broken one's place (<see cref="Services.Assets.Failure{T}"/>): magenta
/// is a shader, red an asset that did not load. Its name is the file's: <c>Materials/Failures/Texture.mat</c>.
/// </summary>
public enum FailureType : byte
{
    /// <summary>
    /// A shader that does not compile, or a material that does not load: solid magenta.
    /// </summary>
    Shader,

    /// <summary>
    /// A texture that does not load: a red and black checker.
    /// </summary>
    Texture,

    /// <summary>
    /// A model that does not load: solid red, on a cube.
    /// </summary>
    Mesh,

    /// <summary>
    /// A sky that cannot be drawn, its material no sky or its sky not compiling: magenta across the view, and a plain
    /// grey sky in the light it sheds, so the world it lights stays lit.
    /// </summary>
    Sky,
}
