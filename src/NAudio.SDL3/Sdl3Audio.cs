using NAudio.SDL3.Interop;

namespace NAudio.SDL3;

/// <summary>
/// Process-wide refcounted lifecycle for the SDL3 audio subsystem.
/// </summary>
public static class Sdl3Audio
{
    private static readonly Lock s_syncRoot = new();
    private static int _refCount;

    public static void Acquire()
    {
        lock (s_syncRoot)
        {
            if (_refCount == 0 && !SdlNative.SDL_InitSubSystem(SdlNative.SDL_INIT_AUDIO))
            {
                throw new Sdl3AudioException("SDL_InitSubSystem(SDL_INIT_AUDIO) failed", GetError());
            }

            _refCount++;
        }
    }

    public static void Release()
    {
        lock (s_syncRoot)
        {
            if (_refCount == 0)
            {
                return;
            }

            _refCount--;
            if (_refCount == 0)
            {
                SdlNative.SDL_QuitSubSystem(SdlNative.SDL_INIT_AUDIO);
            }
        }
    }

    public static string? GetCurrentDriver()
    {
        EnsureInitialized();
        return SdlNative.PtrToStringUTF8(SdlNative.SDL_GetCurrentAudioDriver());
    }

    public static IReadOnlyList<string> GetAvailableDrivers()
    {
        EnsureInitialized();
        var count = SdlNative.SDL_GetNumAudioDrivers();
        if (count <= 0)
        {
            return [];
        }

        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var name = SdlNative.PtrToStringUTF8(SdlNative.SDL_GetAudioDriver(i));
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    public static string? GetError()
    {
        var msg = SdlNative.PtrToStringUTF8(SdlNative.SDL_GetError());
        _ = SdlNative.SDL_ClearError();
        return string.IsNullOrEmpty(msg) ? null : msg;
    }

    private static void EnsureInitialized()
    {
        if ((SdlNative.SDL_WasInit(SdlNative.SDL_INIT_AUDIO) & SdlNative.SDL_INIT_AUDIO) == 0)
        {
            throw new InvalidOperationException(
                "SDL3 audio subsystem is not initialised. Call Sdl3Audio.Acquire() first.");
        }
    }
}
