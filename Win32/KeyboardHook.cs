namespace Gem.Win32;

/// <summary>
/// Win32 keyboard hook / media key simulation bridge.
/// Delegates to Gem.Services.MediaKeyService for fast-path Win32 SendInput dispatch.
/// </summary>
public static class KeyboardHook
{
    public static bool SendMediaKey(ushort virtualKey) => Gem.Services.MediaKeyService.SendMediaKey(virtualKey);
    public static bool PlayPause() => Gem.Services.MediaKeyService.PlayPause();
    public static bool NextTrack() => Gem.Services.MediaKeyService.NextTrack();
    public static bool PreviousTrack() => Gem.Services.MediaKeyService.PreviousTrack();
    public static bool Stop() => Gem.Services.MediaKeyService.Stop();
}
