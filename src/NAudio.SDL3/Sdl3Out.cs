using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.SDL3.WaveProviders;
using NAudio.Wave;
using SdlNative = NAudio.SDL3.Interop.SdlNative;

namespace NAudio.SDL3;

/// <summary>
/// Cross-platform <see cref="IWavePlayer"/> backed by SDL3's audio stream API.
/// </summary>
public sealed unsafe class Sdl3Out : IWavePlayer, IWavePosition
{
    private static readonly Lock StreamOpenLock = new();

    private readonly SynchronizationContext? _syncContext;
    private readonly string? _deviceName;
    private readonly int _desiredBufferFrames;
    private readonly bool _fallbackToDefaultDevice;
    private readonly Lock _stateLock = new();

    private IWaveProvider? _provider;
    private IWaveProvider? _streamProvider;
    private WaveFormat? _outputFormat;
    private byte[]? _callbackBuffer;
    private IntPtr _stream;
    private GCHandle _selfHandle;
    private IntPtr _userdata;
    private long _bytesPlayed;
    private float _volume = 1f;
    private byte _silenceByte;
    private bool _subsystemAcquired;
    private bool _disposed;
    private bool _sourceExhausted;
    private volatile PlaybackState _state = PlaybackState.Stopped;

    public Sdl3Out() : this(null, 64)
    {
    }

    /// <param name="desiredBufferFrames">
    /// Requested SDL3 device buffer size in sample frames. SDL3 treats this as a hint.
    /// Latency-to-frame conversion belongs to the caller.
    /// </param>
    public Sdl3Out(
        string? deviceName,
        int desiredBufferFrames = 64,
        bool fallbackToDefaultDevice = true)
    {
        if (desiredBufferFrames is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredBufferFrames), desiredBufferFrames,
                $"Desired buffer size must be between 1 and {ushort.MaxValue} sample frames.");
        }

        _syncContext = SynchronizationContext.Current;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        _desiredBufferFrames = desiredBufferFrames;
        _fallbackToDefaultDevice = fallbackToDefaultDevice;
    }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public PlaybackState PlaybackState => _state;

    public WaveFormat OutputWaveFormat =>
        _outputFormat ?? throw new InvalidOperationException("Must call Init first.");

    public float Volume
    {
        get => _volume;
        set
        {
            if (float.IsNaN(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Volume must be >= 0.");
            }

            _volume = value;
            var stream = _stream;
            if (stream != IntPtr.Zero)
            {
                _ = SdlNative.SDL_SetAudioStreamGain(stream, value);
            }
        }
    }

    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_state != PlaybackState.Stopped)
            {
                throw new InvalidOperationException("Cannot re-initialise while playback is active.");
            }

            if (_stream != IntPtr.Zero)
            {
                CloseStreamCore();
            }

            var format = waveProvider.WaveFormat;
            var sdlFormat = TranslateFormat(format);
            var (streamFormat, streamProvider) = WrapForStream(format, waveProvider);

            if (!_subsystemAcquired)
            {
                Sdl3Audio.Acquire();
                _subsystemAcquired = true;
            }

            EnsureSelfHandle();

            var desired = new SdlNative.SDL_AudioSpec
            {
                freq = streamFormat.SampleRate,
                channels = streamFormat.Channels,
                format = sdlFormat,
            };

            var stream = IntPtr.Zero;
            try
            {
                stream = OpenAudioDeviceStream(
                    _deviceName,
                    desired,
                    _desiredBufferFrames,
                    _userdata,
                    out var obtained,
                    out var openError);

                if (stream == IntPtr.Zero && _deviceName != null && _fallbackToDefaultDevice)
                {
                    var requestedDeviceName = _deviceName;
                    stream = OpenAudioDeviceStream(
                        null,
                        desired,
                        _desiredBufferFrames,
                        _userdata,
                        out _,
                        out var fallbackError);

                    if (stream == IntPtr.Zero)
                    {
                        throw new Sdl3AudioException(
                            $"SDL_OpenAudioDeviceStream failed for device '{requestedDeviceName}', and fallback to default also failed",
                            CombineOpenErrors(openError, fallbackError));
                    }
                }
                else if (stream == IntPtr.Zero)
                {
                    throw new Sdl3AudioException(
                        $"SDL_OpenAudioDeviceStream failed for device '{_deviceName ?? "(default)"}'",
                        openError);
                }

                _stream = stream;
                stream = IntPtr.Zero;
                _provider = waveProvider;
                _streamProvider = streamProvider;
                _outputFormat = BuildOutputFormat(obtained);
                _callbackBuffer =
                    GC.AllocateUninitializedArray<byte>(GetCallbackBufferSize(streamFormat, _desiredBufferFrames));
                _silenceByte = GetSilenceByte(sdlFormat);
                _bytesPlayed = 0;
                _sourceExhausted = false;
                _ = SdlNative.SDL_SetAudioStreamGain(_stream, _volume);
            }
            catch
            {
                if (stream != IntPtr.Zero)
                {
                    SdlNative.SDL_DestroyAudioStream(stream);
                }

                ReleaseSelfHandle();

                if (_subsystemAcquired)
                {
                    _subsystemAcquired = false;
                    Sdl3Audio.Release();
                }

                throw;
            }
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            EnsureInitialized();

            if (_state == PlaybackState.Playing)
            {
                return;
            }

            if (!SdlNative.SDL_ResumeAudioStreamDevice(_stream))
            {
                throw new Sdl3AudioException("SDL_ResumeAudioStreamDevice failed", Sdl3Audio.GetError());
            }

            _sourceExhausted = false;
            _state = PlaybackState.Playing;
        }
    }

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            if (_stream != IntPtr.Zero)
            {
                _ = SdlNative.SDL_PauseAudioStreamDevice(_stream);
            }

            _state = PlaybackState.Paused;
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        bool wasActive;
        lock (_stateLock)
        {
            wasActive = _state != PlaybackState.Stopped;
            if (wasActive && _stream != IntPtr.Zero)
            {
                _ = SdlNative.SDL_PauseAudioStreamDevice(_stream);
                _ = SdlNative.SDL_ClearAudioStream(_stream);
            }

            _state = PlaybackState.Stopped;
        }

        if (wasActive)
        {
            RaisePlaybackStopped(null);
        }
    }

    public long GetPosition() => Interlocked.Read(ref _bytesPlayed);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseStreamCore();
            _state = PlaybackState.Stopped;

            if (_subsystemAcquired)
            {
                _subsystemAcquired = false;
                Sdl3Audio.Release();
            }
        }

        GC.SuppressFinalize(this);
    }

    private void CloseStreamCore()
    {
        var stream = _stream;
        _stream = IntPtr.Zero;

        if (stream != IntPtr.Zero)
        {
            try
            {
                _ = SdlNative.SDL_PauseAudioStreamDevice(stream);
                _ = SdlNative.SDL_ClearAudioStream(stream);
            }
            catch
            {
                // best effort
            }

            SdlNative.SDL_DestroyAudioStream(stream);
        }

        ReleaseSelfHandle();
        _provider = null;
        _streamProvider = null;
        _outputFormat = null;
        _callbackBuffer = null;
        _sourceExhausted = false;
    }

    private void EnsureSelfHandle()
    {
        if (_userdata != IntPtr.Zero)
        {
            return;
        }

        _selfHandle = GCHandle.Alloc(this);
        _userdata = GCHandle.ToIntPtr(_selfHandle);
    }

    private void ReleaseSelfHandle()
    {
        if (_userdata == IntPtr.Zero)
        {
            return;
        }

        _userdata = IntPtr.Zero;
        _selfHandle.Free();
    }

    [MemberNotNull(nameof(_provider), nameof(_streamProvider), nameof(_outputFormat))]
    private void EnsureInitialized()
    {
        if (_stream == IntPtr.Zero || _provider == null || _streamProvider == null || _outputFormat == null)
        {
            throw new InvalidOperationException("Must call Init before Play/Pause.");
        }
    }

    private static SdlNative.SDL_AudioFormat TranslateFormat(WaveFormat format)
    {
        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => SdlNative.SDL_AUDIO_F32,
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => SdlNative.SDL_AUDIO_S16,
            WaveFormatEncoding.Pcm when format.BitsPerSample is 24 or 32 => SdlNative.SDL_AUDIO_S32,
            WaveFormatEncoding.Pcm when format.BitsPerSample == 8 => SdlNative.SDL_AudioFormat.SDL_AUDIO_U8,
            _ => throw new NotSupportedException(
                $"Sdl3Out cannot play {format.Encoding} {format.BitsPerSample}-bit audio directly. " +
                "Insert a converter to IEEE float 32 or 16/32-bit PCM upstream."),
        };
    }

    private static (WaveFormat streamFormat, IWaveProvider streamProvider) WrapForStream(
        WaveFormat inputFormat,
        IWaveProvider inputProvider)
    {
        if (inputFormat is not { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 24 })
        {
            return (inputFormat, inputProvider);
        }

        var s32 = new WaveFormat(inputFormat.SampleRate, 32, inputFormat.Channels);
        return (s32, new Pcm24ToS32Provider(inputProvider));
    }

    private static WaveFormat BuildOutputFormat(SdlNative.SDL_AudioSpec spec)
    {
        return spec.format switch
        {
            SdlNative.SDL_AudioFormat.SDL_AUDIO_F32LE or SdlNative.SDL_AudioFormat.SDL_AUDIO_F32BE =>
                WaveFormat.CreateIeeeFloatWaveFormat(spec.freq, spec.channels),
            SdlNative.SDL_AudioFormat.SDL_AUDIO_S16LE or SdlNative.SDL_AudioFormat.SDL_AUDIO_S16BE =>
                new WaveFormat(spec.freq, 16, spec.channels),
            SdlNative.SDL_AudioFormat.SDL_AUDIO_S32LE or SdlNative.SDL_AudioFormat.SDL_AUDIO_S32BE =>
                new WaveFormat(spec.freq, 32, spec.channels),
            SdlNative.SDL_AudioFormat.SDL_AUDIO_U8 => new WaveFormat(spec.freq, 8, spec.channels),
            _ => throw new Sdl3AudioException(
                $"SDL_OpenAudioDeviceStream was given an unsupported audio format (0x{(ushort)spec.format:X4})."),
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AudioStreamCallback(IntPtr userdata, IntPtr stream, int additionalAmount, int totalAmount)
    {
        try
        {
            if (userdata == IntPtr.Zero)
            {
                return;
            }

            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is Sdl3Out self)
            {
                self.AudioStreamCallbackCore(stream, additionalAmount);
            }
        }
        catch
        {
            // Never let managed exceptions cross the native callback boundary.
        }
    }

    private void AudioStreamCallbackCore(IntPtr stream, int additionalAmount)
    {
        if (additionalAmount <= 0 || stream == IntPtr.Zero)
        {
            return;
        }

        var streamProvider = _streamProvider;
        var outputFormat = _outputFormat;
        var callbackBuffer = _callbackBuffer;

        if (_disposed ||
            _state != PlaybackState.Playing ||
            streamProvider == null ||
            outputFormat == null ||
            callbackBuffer == null)
        {
            return;
        }

        try
        {
            FillStream(stream, additionalAmount, streamProvider, callbackBuffer, _silenceByte);
        }
        catch (Exception ex)
        {
            _state = PlaybackState.Stopped;
            RaisePlaybackStopped(ex);
        }
    }

    private void FillStream(
        IntPtr stream,
        int len,
        IWaveProvider streamProvider,
        byte[] callbackBuffer,
        byte silenceByte)
    {
        if (callbackBuffer.Length < len)
        {
            callbackBuffer = EnsureCallbackBuffer(len);
        }

        var buffer = callbackBuffer.AsSpan(0, len);

        int read;
        try
        {
            read = streamProvider.Read(callbackBuffer, 0, len);
        }
        catch
        {
            FillSilence(buffer, silenceByte);
            PutAudioStreamData(stream, callbackBuffer, len);
            throw;
        }

        if (read < 0)
        {
            read = 0;
        }
        else if (read > len)
        {
            read = len;
        }

        if (read < len)
        {
            FillSilence(buffer[read..], silenceByte);
        }

        PutAudioStreamData(stream, callbackBuffer, len);
        Interlocked.Add(ref _bytesPlayed, read);

        if (read == 0 && !_sourceExhausted)
        {
            _sourceExhausted = true;
            _state = PlaybackState.Stopped;
            RaisePlaybackStopped(null);
        }
    }

    private byte[] EnsureCallbackBuffer(int len)
    {
        var buffer = _callbackBuffer;
        if (buffer != null && buffer.Length >= len)
        {
            return buffer;
        }

        buffer = GC.AllocateUninitializedArray<byte>(len);
        _callbackBuffer = buffer;
        return buffer;
    }

    private static void FillSilence(Span<byte> buffer, byte silenceByte)
    {
        if (silenceByte == 0)
        {
            buffer.Clear();
        }
        else
        {
            buffer.Fill(silenceByte);
        }
    }

    private static void PutAudioStreamData(IntPtr stream, byte[] buffer, int len)
    {
        fixed (byte* ptr = buffer)
        {
            if (!SdlNative.SDL_PutAudioStreamData(stream, ptr, len))
            {
                throw new Sdl3AudioException("SDL_PutAudioStreamData failed", Sdl3Audio.GetError());
            }
        }
    }

    private void RaisePlaybackStopped(Exception? exception)
    {
        var handler = PlaybackStopped;
        if (handler == null)
        {
            return;
        }

        var args = new StoppedEventArgs(exception);
        var ctx = _syncContext;
        if (ctx != null)
        {
            ctx.Post(static state =>
            {
                var (h, sender, a) = ((EventHandler<StoppedEventArgs>, object, StoppedEventArgs))state!;
                h(sender, a);
            }, (handler, (object)this, args));
        }
        else
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (h, sender, a) = ((EventHandler<StoppedEventArgs>, object, StoppedEventArgs))state!;
                h(sender, a);
            }, (handler, (object)this, args), preferLocal: false);
        }
    }

    private static IntPtr OpenAudioDeviceStream(
        string? deviceName,
        SdlNative.SDL_AudioSpec desired,
        int desiredBufferFrames,
        IntPtr userdata,
        out SdlNative.SDL_AudioSpec obtained,
        out string? error)
    {
        obtained = default;
        var deviceId = ResolvePlaybackDeviceId(deviceName, out var resolveError);
        if (deviceId == 0)
        {
            error = resolveError;
            return IntPtr.Zero;
        }

        IntPtr stream;
        lock (StreamOpenLock)
        {
            _ = SdlNative.SDL_SetHint(
                SdlNative.SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES,
                desiredBufferFrames.ToString(CultureInfo.InvariantCulture));

            var spec = desired;
            stream = SdlNative.SDL_OpenAudioDeviceStream(deviceId, ref spec, &AudioStreamCallback, userdata);
        }

        if (stream == IntPtr.Zero)
        {
            error = Sdl3Audio.GetError();
            return IntPtr.Zero;
        }

        SdlNative.SDL_AudioSpec src = default;
        SdlNative.SDL_AudioSpec dst = default;
        if (!SdlNative.SDL_GetAudioStreamFormat(stream, &src, &dst))
        {
            SdlNative.SDL_DestroyAudioStream(stream);
            error = $"SDL_GetAudioStreamFormat failed: {Sdl3Audio.GetError()}";
            return IntPtr.Zero;
        }

        obtained = dst.freq != 0 ? dst : src;
        error = null;
        return stream;
    }

    private static uint ResolvePlaybackDeviceId(string? deviceName, out string? error)
    {
        error = null;
        if (deviceName == null)
        {
            return SdlNative.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK;
        }

        var devicesPtr = SdlNative.SDL_GetAudioPlaybackDevices(out var count);
        if (devicesPtr == IntPtr.Zero)
        {
            error = Sdl3Audio.GetError() ?? $"SDL3 playback device '{deviceName}' was not found.";
            return 0;
        }

        try
        {
            if (count <= 0)
            {
                error = $"SDL3 playback device '{deviceName}' was not found.";
                return 0;
            }

            var devices = new ReadOnlySpan<uint>((void*)devicesPtr, count);
            for (var i = 0; i < devices.Length; i++)
            {
                var id = devices[i];
                var name = SdlNative.PtrToStringUTF8(SdlNative.SDL_GetAudioDeviceName(id));
                if (string.Equals(name, deviceName, StringComparison.Ordinal))
                {
                    return id;
                }
            }

            error = $"SDL3 playback device '{deviceName}' was not found.";
            return 0;
        }
        finally
        {
            SdlNative.SDL_free(devicesPtr);
        }
    }

    private static string? CombineOpenErrors(string? primaryError, string? fallbackError)
    {
        if (string.IsNullOrEmpty(primaryError))
        {
            return fallbackError;
        }

        if (string.IsNullOrEmpty(fallbackError))
        {
            return primaryError;
        }

        return $"{primaryError}; fallback: {fallbackError}";
    }

    private static byte GetSilenceByte(SdlNative.SDL_AudioFormat format)
    {
        return format == SdlNative.SDL_AudioFormat.SDL_AUDIO_U8 ? (byte)128 : (byte)0;
    }

    private static int GetCallbackBufferSize(WaveFormat format, int frames)
    {
        var bytesPerFrame = format.Channels * (format.BitsPerSample / 8);
        return Math.Max(1, bytesPerFrame * frames);
    }
}
