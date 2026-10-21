using System.Diagnostics.CodeAnalysis;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// An asset type whose file is itself as JSON, in Core's dialect (<see cref="Json"/>): Core's
/// <see cref="IAssetLoader{T}"/> reads it, so the class is all there is to write, and its <c>.meta</c> sidecar holds
/// only the id.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker Core's asset loader asks a type for, as it asks for ITextAsset: "
        + "how a type's file is read is said by what it implements.")]
public interface IJsonAsset<TSelf> : IAsset<TSelf>
    where TSelf : IAsset<TSelf>;
