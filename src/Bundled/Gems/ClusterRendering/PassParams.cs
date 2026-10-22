using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// A pass's params as a listing runs them: packed by its shader's <see cref="ParamLayout"/> from the dictionary the
/// pipeline that runs it holds, and packed again in place when a game sets one, from the count the dictionary keeps
/// (<see cref="MaterialParamDictionary.Version"/>), so a value changes without the listing being built again, and what
/// the passes keep from frame to frame, exposure, temporal history, global illumination's field, stays. A curve param
/// naming a curve the build did not load cannot be packed so; it waits for a build, which loads it. Made on a worker as
/// the build loads the pass, then the render thread's.
/// </summary>
internal sealed class PassParams
{
    private readonly string _name;
    private readonly MaterialParamDictionary _values;
    private readonly ParamLayout? _layout;

    /// <summary>
    /// The curves the build loaded for the pass's curve members, and the default curve, which one left out takes.
    /// </summary>
    private readonly IReadOnlyDictionary<Handle<Curve>, Curve> _curves;
    private readonly Handle<Curve> _defaultCurve;

    /// <summary>
    /// <see cref="FindCurve"/>, made once, so packing again makes no closure.
    /// </summary>
    private readonly Func<MaterialParam, Curve?> _findCurve;

    /// <summary>
    /// The params given that the shader does not declare, each said once.
    /// </summary>
    private readonly HashSet<string> _unknown = new(StringComparer.Ordinal);

    /// <summary>
    /// The count of the values last packed.
    /// </summary>
    private long _version;

    /// <summary>
    /// Packs <paramref name="values"/> for the pass <paramref name="name"/>, <paramref name="version"/> being their
    /// count before the build read them.
    /// </summary>
    internal PassParams(
        string name,
        MaterialParamDictionary values,
        long version,
        ParamLayout? layout,
        IReadOnlyDictionary<Handle<Curve>, Curve> curves,
        Handle<Curve> defaultCurve)
    {
        _name = name;
        _values = values;
        _version = version;
        _layout = layout;
        _curves = curves;
        _defaultCurve = defaultCurve;
        _findCurve = FindCurve;
        Words = Pack();
    }

    /// <summary>
    /// The params packed: the pass's rows of the frame's constants.
    /// </summary>
    internal PassWords Words { get; private set; }

    /// <summary>
    /// Packs the params again when a game set one since they were last packed: false, and left as they were, when one
    /// names a curve the build did not load, which only another build can.
    /// </summary>
    internal bool TryFollow()
    {
        long version = _values.Version;

        if (version == _version) return true;

        if (!AreCurvesLoaded()) return false;

        _version = version;
        Words = Pack();

        return true;
    }

    /// <summary>
    /// The params packed by the layout, over its defaults; said first, once each, is any it does not declare.
    /// </summary>
    private PassWords Pack()
    {
        PassWords words = default;

        if (_layout is null) return words;

        foreach (string unknown in _layout.FindUnknown(_values.Keys))
        {
            if (_unknown.Add(unknown))
                Debugging.Warn($"Pass {_name} is given {unknown}, which its shader's PassParams does not declare.");
        }

        _layout.Write(
            [_values],
            static _ => GpuContract.NoTexture,
            _findCurve,
            MemoryMarshal.AsBytes(new Span<PassWords>(ref words)));

        return words;
    }

    /// <summary>
    /// Whether every curve the params name, the default curve for a curve member they leave out, was loaded.
    /// </summary>
    private bool AreCurvesLoaded()
    {
        if (_layout is null) return true;

        foreach (ParamField field in _layout.Fields)
        {
            if (field.Type != ParamType.Curve) continue;

            if (!_curves.ContainsKey(GetCurve(_values.GetValueOrDefault(field.Name) ?? field.Default))) return false;
        }

        return true;
    }

    private Curve? FindCurve(MaterialParam param) => _curves.GetValueOrDefault(GetCurve(param));

    /// <summary>
    /// The curve a param names, the default curve for none.
    /// </summary>
    private Handle<Curve> GetCurve(MaterialParam param)
        => param.Curve == Handle<Curve>.None ? _defaultCurve : param.Curve;
}
