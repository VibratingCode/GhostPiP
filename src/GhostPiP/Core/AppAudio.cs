using System.IO;
using System.Runtime.InteropServices;

namespace GhostPiP.Core;

/// <summary>Per-app volume through Core Audio, so the mirrored app can be turned down without touching anything else.</summary>
public static class AppAudio
{
    static Guid s_context = Guid.Empty;

    public static (float Volume, bool Muted)? Get(string exeName)
    {
        foreach (var volume in Sessions(exeName))
        {
            try
            {
                volume.GetMasterVolume(out float level);
                volume.GetMute(out bool muted);
                return (level, muted);
            }
            catch (COMException)
            {
            }
        }
        return null;
    }

    public static bool SetVolume(string exeName, float level)
    {
        bool any = false;
        foreach (var volume in Sessions(exeName))
        {
            try
            {
                volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref s_context);
                any = true;
            }
            catch (COMException)
            {
            }
        }
        return any;
    }

    public static bool SetMute(string exeName, bool muted)
    {
        bool any = false;
        foreach (var volume in Sessions(exeName))
        {
            try
            {
                volume.SetMute(muted, ref s_context);
                any = true;
            }
            catch (COMException)
            {
            }
        }
        return any;
    }

    /// <summary>Browsers play audio from helper processes, so sessions match on the exe name rather than the window's PID.</summary>
    static List<ISimpleAudioVolume> Sessions(string exeName)
    {
        var found = new List<ISimpleAudioVolume>();
        if (string.IsNullOrEmpty(exeName))
            return found;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices) != 0)
                return found;
            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                devices.Item(d, out var device);
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out object managerObject) != 0)
                    continue;
                var manager = (IAudioSessionManager2)managerObject;
                if (manager.GetSessionEnumerator(out var sessions) != 0)
                    continue;
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out var control);
                    if (control is not IAudioSessionControl2 control2)
                        continue;
                    control2.GetProcessId(out uint pid);
                    if (pid != 0 && Path.GetFileName(Win.ProcessPath(pid)).Equals(exeName, StringComparison.OrdinalIgnoreCase) && control is ISimpleAudioVolume volume)
                        found.Add(volume);
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // No audio stack (or it went away): the volume controls just won't do anything.
        }
        return found;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, int flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, int flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, nint context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, nint context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(nint param, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
