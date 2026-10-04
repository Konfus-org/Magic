namespace Magic.Contexts.Settings;

/// <summary>
/// Shadow maps: the sun's cascades along the camera's view, and the pages of the spot and point lights nearest the
/// camera, all drawn into one depth atlas. The structural ones (the cascade count and resolutions, how many local lights
/// hold pages) build the render state again when they change; the rest is read every frame.
/// </summary>
public sealed class ShadowSettings
{
    private int _cascades = 4;
    private int _cascadeResolution = 2048;
    private float _distance = 300f;
    private float _splitLambda = 0.7f;
    private float _blendFraction = 0.15f;
    private float _filterRadius = 0.03f;
    private float _normalBias = 1.5f;
    private float _depthBias = 1f;
    private float _slopeBias = 2f;
    private float _casterRange = 500f;
    private float _lodBias = 0.5f;
    private float _minTexels = 1.5f;
    private int _maxLocalLights = 8;
    private int _localResolution = 256;
    private int _localFacesPerFrame = 4;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many cascades the sun's shadow is cut into along the view, 1 to 4: more spend the texels more evenly over the distance.
    /// </summary>
    public int Cascades { get => _cascades; set => _cascades = Math.Clamp(value, 1, 4); }

    /// <summary>
    /// Texels a side per cascade, 256 to 4096.
    /// </summary>
    public int CascadeResolution { get => _cascadeResolution; set => _cascadeResolution = Math.Clamp(value, 256, 4096); }

    /// <summary>
    /// Metres from the camera the sun casts shadows over; past it the sun lights everything, faded in over the last cascade's blend band.
    /// </summary>
    public float Distance { get => _distance; set => _distance = MathF.Max(10f, value); }

    /// <summary>
    /// How the cascades split the distance: 0 in equal lengths, 1 logarithmically (most of them near), between a blend of both.
    /// </summary>
    public float SplitLambda { get => _splitLambda; set => _splitLambda = Math.Clamp(value, 0f, 1f); }

    /// <summary>
    /// The share of each cascade's range over which it blends into the next, so no seam shows between them.
    /// </summary>
    public float BlendFraction { get => _blendFraction; set => _blendFraction = Math.Clamp(value, 0.01f, 0.5f); }

    /// <summary>
    /// The penumbra's radius in metres, the same in the world whichever cascade a pixel falls in; wider is softer.
    /// </summary>
    public float FilterRadius { get => _filterRadius; set => _filterRadius = Math.Clamp(value, 0f, 1f); }

    /// <summary>
    /// Shadow texels a receiver is moved along its normal before it is tested, against acne on surfaces at a grazing angle.
    /// </summary>
    public float NormalBias { get => _normalBias; set => _normalBias = Math.Clamp(value, 0f, 8f); }

    /// <summary>
    /// Shadow texels a receiver is moved towards the light before it is tested, whatever way it faces: the acne on a
    /// surface facing the light, which the normal bias leaves alone, goes with it.
    /// </summary>
    public float DepthBias { get => _depthBias; set => _depthBias = Math.Clamp(value, 0f, 16f); }

    /// <summary>
    /// The rasteriser's slope-scaled depth bias when the maps are drawn.
    /// </summary>
    public float SlopeBias { get => _slopeBias; set => _slopeBias = Math.Clamp(value, 0f, 16f); }

    /// <summary>
    /// Metres a cascade's volume reaches towards the sun beyond what the camera sees, so tall casters outside the view still shadow it.
    /// </summary>
    public float CasterRange { get => _casterRange; set => _casterRange = Math.Clamp(value, 0f, 10000f); }

    /// <summary>
    /// Multiplies the renderer's LOD bias for the shadow views: under 1 casts lesser versions of a mesh sooner than the camera draws them.
    /// </summary>
    public float LodBias { get => _lodBias; set => _lodBias = Math.Clamp(value, 0.01f, 4f); }

    /// <summary>
    /// A caster smaller than this many shadow texels casts nothing: specks cost vertices and shadow nothing anyone sees.
    /// </summary>
    public float MinTexels { get => _minTexels; set => _minTexels = Math.Clamp(value, 0f, 64f); }

    /// <summary>
    /// Draw the third and fourth cascade on alternate frames: half their cost, a frame behind.
    /// </summary>
    public bool StaggerFar { get; set; } = true;

    /// <summary>
    /// How many shadow-casting spot and point lights hold pages at once, the nearest and biggest on screen first; 0 to 64.
    /// </summary>
    public int MaxLocalLights { get => _maxLocalLights; set => _maxLocalLights = Math.Clamp(value, 0, 64); }

    /// <summary>
    /// Texels a side of a local light's page (a point light has six), 64 to 1024.
    /// </summary>
    public int LocalResolution { get => _localResolution; set => _localResolution = Math.Clamp(value, 64, 1024); }

    /// <summary>
    /// How many local pages are drawn again per frame, the ones that waited longest first; a page that is new is drawn
    /// whatever this says. A point light is six pages.
    /// </summary>
    public int LocalFacesPerFrame { get => _localFacesPerFrame; set => _localFacesPerFrame = Math.Clamp(value, 1, 64); }
}
