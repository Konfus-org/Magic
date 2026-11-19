using System.Runtime.InteropServices;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The RenderDoc in-application API of a RenderDoc that started Magic: it writes the frames between
/// <see cref="Start"/> and <see cref="End"/> on the Vulkan instance to a capture file.
/// </summary>
/// <remarks>
/// It finds a RenderDoc that is loaded already and never loads one. The binding follows <c>renderdoc_app.h</c>
/// (https://github.com/baldurk/renderdoc/blob/v1.x/renderdoc/api/app/renderdoc_app.h), written by hand because no
/// generator makes it: each call is a slot of the struct <c>RENDERDOC_API_1_1_2</c>, an array of function pointers.
/// </remarks>
internal sealed unsafe partial class RenderDoc
{
    /// <summary>
    /// <c>eRENDERDOC_API_Version_1_1_2</c>, the oldest version that has every call used here.
    /// </summary>
    private const int ApiVersion = 10102;

    private const int SetCaptureFilePathTemplateSlot = 11;

    private const int GetNumCapturesSlot = 13;

    private const int GetCaptureSlot = 14;

    private const int StartFrameCaptureSlot = 19;

    private const int EndFrameCaptureSlot = 21;

    private readonly void** _api;

    /// <summary>
    /// What RenderDoc calls the device of a Vulkan instance: the dispatch table pointer, the first pointer in the
    /// memory of the <c>VkInstance</c> (<c>RENDERDOC_DEVICEPOINTER_FROM_VKINSTANCE</c>).
    /// </summary>
    private readonly nint _device;

    private RenderDoc(void** api, VkInstance instance)
    {
        _api = api;
        _device = *(nint*)instance.Handle;
    }

    /// <summary>
    /// The API of the RenderDoc that started Magic, or null when none did. Call it before the frames to capture.
    /// </summary>
    /// <remarks>
    /// Only Windows is searched: <c>GetModuleHandle</c> finds <c>renderdoc.dll</c> only when RenderDoc loaded it.
    /// </remarks>
    public static RenderDoc? Find(VkInstance instance)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        nint library = GetModuleHandleW("renderdoc");
        if (library == 0 || !NativeLibrary.TryGetExport(library, "RENDERDOC_GetAPI", out nint getApi))
            return null;

        void** api = null;
        int found = ((delegate* unmanaged[Cdecl]<int, void***, int>)getApi)(ApiVersion, &api);

        return found == 1 ? new RenderDoc(api, instance) : null;
    }

    /// <summary>
    /// Starts to capture what the Vulkan instance does, into a file whose name starts with
    /// <paramref name="pathTemplate"/>. RenderDoc adds the frame number and <c>.rdc</c>.
    /// </summary>
    public void Start(string pathTemplate)
    {
        // RenderDoc copies the template, so the bytes need to last only for the call.
        fixed (byte* template = CString.ToBytes(pathTemplate))
            ((delegate* unmanaged[Cdecl]<byte*, void>)_api[SetCaptureFilePathTemplateSlot])(template);
        ((delegate* unmanaged[Cdecl]<nint, nint, void>)_api[StartFrameCaptureSlot])(_device, 0);
    }

    /// <summary>
    /// Ends the capture and writes its file. Gives the path of the file as RenderDoc reports it.
    /// </summary>
    public Result<string> End()
    {
        if (((delegate* unmanaged[Cdecl]<nint, nint, uint>)_api[EndFrameCaptureSlot])(_device, 0) == 0)
            return Result.Failure("RenderDoc could not capture the frames.");

        // The newest capture is the last; a first call gets the length of its path, a second the path itself.
        var getCapture = (delegate* unmanaged[Cdecl]<uint, byte*, uint*, ulong*, uint>)_api[GetCaptureSlot];
        uint count = ((delegate* unmanaged[Cdecl]<uint>)_api[GetNumCapturesSlot])();
        uint length = 0;
        if (count == 0 || getCapture(count - 1, null, &length, null) == 0)
            return Result.Failure("RenderDoc reports no capture file.");

        byte[] path = new byte[length + 1];
        fixed (byte* bytes = path)
        {
            getCapture(count - 1, bytes, null, null);

            return Result.Success(CString.Read(bytes));
        }
    }

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(string moduleName);
}
