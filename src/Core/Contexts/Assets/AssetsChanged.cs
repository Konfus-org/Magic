using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// Asset files were added, moved, removed or written on disk, so what <see cref="Services.Assets.Find{T}"/>,
/// <see cref="Services.Assets.Paths"/> and <see cref="Services.Assets.Version{T}"/> answer may have changed: told once
/// the assets that changed have loaded again. A message on <see cref="Events"/>.
/// </summary>
public readonly record struct AssetsChanged;
