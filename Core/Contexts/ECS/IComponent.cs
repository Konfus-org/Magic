namespace Magic.Contexts.Components;

/// <summary>
/// Marks a component that a chunk file may hold. The contract is plain data that <see cref="Assets.AssetJson"/>
/// can read and write as is: public fields or properties of numbers, bools, enums, System.Numerics vectors,
/// <see cref="Handle{T}"/> and nested structs of the same; no unions, no <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/>,
/// no references. A chunk names a component by its type name (<c>Transform</c>, <c>DirectionalLight</c>,
/// or <c>MyGem.Health</c> when two gems share a short name), so nothing registers it: implement this and it loads.
/// </summary>
public interface IComponent;
