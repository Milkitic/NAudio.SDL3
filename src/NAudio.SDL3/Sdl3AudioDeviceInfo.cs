using SdlNative = NAudio.SDL3.Interop.SdlNative;

namespace NAudio.SDL3;

public sealed record Sdl3AudioDeviceInfo(uint Id, string Name)
{
    public static Sdl3AudioDeviceInfo Default { get; } = new(0, "Default");

    public bool IsDefault => Id == 0;
}

public static unsafe class Sdl3AudioDevices
{
    public static IReadOnlyList<Sdl3AudioDeviceInfo> GetPlaybackDevices()
    {
        Sdl3Audio.Acquire();
        try
        {
            var devicesPtr = SdlNative.SDL_GetAudioPlaybackDevices(out var count);
            var list = new List<Sdl3AudioDeviceInfo>(Math.Max(1, count) + 1)
            {
                Sdl3AudioDeviceInfo.Default
            };

            if (devicesPtr == IntPtr.Zero)
            {
                return list;
            }

            try
            {
                if (count <= 0)
                {
                    return list;
                }

                var devices = new ReadOnlySpan<uint>((void*)devicesPtr, count);
                for (var i = 0; i < devices.Length; i++)
                {
                    var id = devices[i];
                    var name = SdlNative.PtrToStringUTF8(SdlNative.SDL_GetAudioDeviceName(id));
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(new Sdl3AudioDeviceInfo(id, name));
                    }
                }

                return list;
            }
            finally
            {
                SdlNative.SDL_free(devicesPtr);
            }
        }
        finally
        {
            Sdl3Audio.Release();
        }
    }
}
