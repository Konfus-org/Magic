namespace Magic.Contexts.Assets;

/// <summary>
/// An asset whose file is taken as it is: the asset manager reads its bytes into <see cref="Data"/>, for a type whose
/// own code reads them when and how it needs to.
/// </summary>
public abstract class BinaryAsset : Asset
{
    public byte[] Data { get; internal set; } = [];
}
