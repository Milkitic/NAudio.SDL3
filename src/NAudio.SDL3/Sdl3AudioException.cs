namespace NAudio.SDL3;

public sealed class Sdl3AudioException : Exception
{
    public Sdl3AudioException(string message) : base(message)
    {
    }

    public Sdl3AudioException(string message, string? sdlError)
        : base(string.IsNullOrEmpty(sdlError) ? message : $"{message}: {sdlError}")
    {
        SdlError = sdlError;
    }

    public string? SdlError { get; }
}
