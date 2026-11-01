using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

/// <summary>
/// A live, compositor-drawn copy of another window. It costs almost nothing and is always real-time,
/// but DWM paints it on top of everything else in the destination window.
/// </summary>
public sealed class DwmThumbnail : IDisposable
{
    nint _handle;

    DwmThumbnail(nint handle) => _handle = handle;

    public static DwmThumbnail? Create(nint destination, nint source)
    {
        if (destination == 0 || source == 0)
            return null;
        return DwmRegisterThumbnail(destination, source, out nint handle) == 0 ? new DwmThumbnail(handle) : null;
    }

    /// <param name="source">Region of the source's client area; null shows all of it.</param>
    public void Update(RECT destination, RECT? source, byte opacity = 255, bool visible = true)
    {
        if (_handle == 0)
            return;
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destination,
            opacity = opacity,
            fVisible = visible,
            fSourceClientAreaOnly = true,
        };
        if (source is { } s)
        {
            p.dwFlags |= DWM_TNP_RECTSOURCE;
            p.rcSource = s;
        }
        DwmUpdateThumbnailProperties(_handle, ref p);
    }

    public void Hide()
    {
        if (_handle == 0)
            return;
        var p = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
        DwmUpdateThumbnailProperties(_handle, ref p);
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            DwmUnregisterThumbnail(_handle);
            _handle = 0;
        }
    }
}
