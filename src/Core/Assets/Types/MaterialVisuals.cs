namespace Magic.Assets.Types;

/// <summary>
/// How a <see cref="MaterialVisuals"/> is blended, which picks the pipeline it is drawn with.
/// </summary>
public enum MaterialType : byte
{
    Opaque,

    /// <summary>
    /// Opaque, but pixels the surface makes less opaque than its alpha cutoff are not drawn.
    /// </summary>
    Masked,

    /// <summary>
    /// Blended over what is behind it, drawn after everything opaque.
    /// </summary>
    Transparent,
}

/// <summary>
/// What a <see cref="Material"/> looks like: a material shader, a module implementing the shader library's material contract, values for the parameters
/// its struct declares, and how it is blended:
/// <c>{ "shader": { "id": 10001 }, "params": { "roughness": { "x": 0.8 } } }</c>. Its params, how it is blended and
/// its sides may be set while it is drawn, on the main thread, and are drawn so from the next frame; what is set holds
/// until the material is made again, as its file changes, and is not saved with the world.
/// </summary>
public sealed class MaterialVisuals
{
    public required Handle<Shader> Shader { get; init; }

    /// <summary>
    /// How it is blended.
    /// </summary>
    public MaterialType Type { get; set; }

    /// <summary>
    /// Its back faces are drawn too.
    /// </summary>
    public bool IsDoubleSided { get; set; }

    /// <summary>
    /// Values by the names of the fields of the shader's struct, case and all; one left out takes the field's
    /// initializer. Its values change, never the dictionary, so whatever draws it sees each change.
    /// </summary>
    public MaterialParamDictionary Params { get; init; } = [];
}
