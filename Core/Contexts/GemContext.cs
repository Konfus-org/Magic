using Core.Interfaces;

namespace Core.Contexts;

internal sealed record GemContext(
    IGem Loaded,
    GemMetadata Metadata,
    GemLoadContext Context);
