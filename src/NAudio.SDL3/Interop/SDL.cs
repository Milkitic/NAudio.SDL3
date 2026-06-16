using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NAudio.SDL3.Interop;

/// <summary>
/// Minimal P/Invoke surface for SDL3 audio streams.
/// </summary>
internal static unsafe partial class SdlNative
{
    private const string NativeLibName = "SDL3";

    static SdlNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SdlNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeLibName)
        {
            return IntPtr.Zero;
        }

        if (OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libSDL3.so.0", assembly, searchPath, out var handle)) return handle;
            if (NativeLibrary.TryLoad("libSDL3.so", assembly, searchPath, out handle)) return handle;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (NativeLibrary.TryLoad("libSDL3.0.dylib", assembly, searchPath, out var handle)) return handle;
            if (NativeLibrary.TryLoad("libSDL3.dylib", assembly, searchPath, out handle)) return handle;
        }

        return IntPtr.Zero;
    }

    public const uint SDL_INIT_AUDIO = 0x00000010u;
    public const uint SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK = 0xFFFFFFFFu;
    public const string SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES = "SDL_AUDIO_DEVICE_SAMPLE_FRAMES";

    public enum SDL_AudioFormat : ushort
    {
        SDL_AUDIO_UNKNOWN = 0x0000,
        SDL_AUDIO_U8 = 0x0008,
        SDL_AUDIO_S8 = 0x8008,
        SDL_AUDIO_S16LE = 0x8010,
        SDL_AUDIO_S16BE = 0x9010,
        SDL_AUDIO_S32LE = 0x8020,
        SDL_AUDIO_S32BE = 0x9020,
        SDL_AUDIO_F32LE = 0x8120,
        SDL_AUDIO_F32BE = 0x9120,
    }

    public static SDL_AudioFormat SDL_AUDIO_S16 =>
        BitConverter.IsLittleEndian ? SDL_AudioFormat.SDL_AUDIO_S16LE : SDL_AudioFormat.SDL_AUDIO_S16BE;

    public static SDL_AudioFormat SDL_AUDIO_S32 =>
        BitConverter.IsLittleEndian ? SDL_AudioFormat.SDL_AUDIO_S32LE : SDL_AudioFormat.SDL_AUDIO_S32BE;

    public static SDL_AudioFormat SDL_AUDIO_F32 =>
        BitConverter.IsLittleEndian ? SDL_AudioFormat.SDL_AUDIO_F32LE : SDL_AudioFormat.SDL_AUDIO_F32BE;

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_AudioSpec
    {
        public SDL_AudioFormat format;
        public int channels;
        public int freq;
    }

    [LibraryImport(NativeLibName, EntryPoint = "SDL_InitSubSystem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_InitSubSystem(uint flags);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_QuitSubSystem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SDL_QuitSubSystem(uint flags);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_WasInit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint SDL_WasInit(uint flags);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_GetError();

    [LibraryImport(NativeLibName, EntryPoint = "SDL_ClearError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ClearError();

    [LibraryImport(NativeLibName, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SetHint(string name, string value);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetNumAudioDrivers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SDL_GetNumAudioDrivers();

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetAudioDriver")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_GetAudioDriver(int index);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetCurrentAudioDriver")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_GetCurrentAudioDriver();

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetAudioPlaybackDevices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_GetAudioPlaybackDevices(out int count);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetAudioDeviceName")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_GetAudioDeviceName(uint devid);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_OpenAudioDeviceStream")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_OpenAudioDeviceStream(
        uint devid,
        ref SDL_AudioSpec spec,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void> callback,
        IntPtr userdata);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_DestroyAudioStream")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SDL_DestroyAudioStream(IntPtr stream);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetAudioStreamDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint SDL_GetAudioStreamDevice(IntPtr stream);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_PutAudioStreamData")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_PutAudioStreamData(IntPtr stream, void* buf, int len);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_GetAudioStreamFormat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_GetAudioStreamFormat(
        IntPtr stream,
        SDL_AudioSpec* srcSpec,
        SDL_AudioSpec* dstSpec);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_ClearAudioStream")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ClearAudioStream(IntPtr stream);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_PauseAudioStreamDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_PauseAudioStreamDevice(IntPtr stream);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_ResumeAudioStreamDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ResumeAudioStreamDevice(IntPtr stream);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_SetAudioStreamGain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SetAudioStreamGain(IntPtr stream, float gain);

    [LibraryImport(NativeLibName, EntryPoint = "SDL_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SDL_free(IntPtr mem);

    public static string? PtrToStringUTF8(IntPtr ptr)
    {
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }
}
