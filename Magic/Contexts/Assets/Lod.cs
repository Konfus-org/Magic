namespace Magic.Contexts.Assets;

/// <summary>
/// One lesser version of an asset: the asset (by id) that stands in for it from <see cref="Threshold"/> on, and the
/// textures baked for that version alone. What a threshold measures belongs to the type: for a model the height on
/// screen, as a fraction of the view's, under which it is drawn; for a chunk the metres from a camera beyond which it is
/// spawned. <see cref="Atlases"/> are an impostor's: per mesh of it, where each texel's surface uv and coverage are,
/// then its normal; nothing names them but this.
/// </summary>
public readonly record struct Lod(float Threshold, ulong Asset, ulong[] Atlases);
