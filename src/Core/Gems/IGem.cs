using System.Diagnostics.CodeAnalysis;
using Magic.Pipes;

namespace Magic.Gems;

/// <summary>
/// Marks a gem's one class, its plugin: Magic makes it after what its constructor takes. Each
/// <see cref="IPipeline{TState}"/> it implements is a pipeline it joins, and every other interface is what it provides
/// to the gems made after it.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the registry and the loader hold a gem by, which an attribute could not be."
)]
public interface IGem;
