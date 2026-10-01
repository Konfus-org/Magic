using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// A script that lives on one entity: a chunk lists it under the entity's <c>scripts</c>, and one instance is made
/// when the entity streams in and disposed when it goes. Its constructor parameters are its dependencies, as a
/// gem's are, plus the <see cref="Handle"/> of its entity when it asks for one. The hooks are a gem's, called once
/// per phase after the gems'; implement only the ones you need.
/// </summary>
public interface IBehavior : IScript
{
    void Update(in Frame frame)
    {
    }

    /// <summary>Zero or more times a frame, with <see cref="Frame.Delta"/> the fixed step.</summary>
    void FixedUpdate(in Frame frame)
    {
    }

    void LateUpdate(in Frame frame)
    {
    }

    void Render(in Frame frame)
    {
    }
}
