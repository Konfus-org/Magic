using Magic.Interfaces;
using Magic.Services;

namespace SampleShared;

/// <summary>
/// What the samples share: every sample references this gem, so its dll is built into the sample's own folder and
/// loads with it. The gem itself only adds the folder its script assets are in, beside the samples, to the assets,
/// so every sample's chunks name the same <c>CameraController.cs</c> and <c>OrbitSystem.cs</c> by id.
/// </summary>
internal sealed class SampleShared : IGem
{
    public SampleShared(Assets assets, IFileSystem files, Project project)
    {
        assets.AddFolder(files.Combine(project.Root, "..", nameof(SampleShared), "Assets"));
    }
}
