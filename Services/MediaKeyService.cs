using System.Runtime.InteropServices;
using Gem.Win32;

namespace Gem.Services;

/// <summary>
/// Service for simulating global multimedia key events (Play/Pause, Next Track, Prev Track, Stop)
/// via Win32 SendInput with keybd_event fallback.
/// Sends KEYEVENTF_KEYDOWN and immediately KEYEVENTF_KEYUP with 0ms latency.
/// </summary>
public static class MediaKeyService
{
    /// <summary>
    /// Dispatches KEYEVENTF_KEYDOWN and immediately KEYEVENTF_KEYUP for the specified virtual key code.
    /// </summary>
    public static bool SendMediaKey(ushort virtualKey)
    {
        ushort scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0);

        var inputs = new INPUT[2];

        // 1. KeyDown event
        inputs[0] = new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = NativeConstants.KEYEVENTF_EXTENDEDKEY,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        // 2. KeyUp event (immediately follows)
        inputs[1] = new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = NativeConstants.KEYEVENTF_KEYUP | NativeConstants.KEYEVENTF_EXTENDEDKEY,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == (uint)inputs.Length)
        {
            return true;
        }

        // Fallback: If blocked by UIPI (Error 5) or SendInput failed, dispatch via keybd_event
        NativeMethods.keybd_event((byte)virtualKey, (byte)scanCode, NativeConstants.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event((byte)virtualKey, (byte)scanCode, NativeConstants.KEYEVENTF_KEYUP | NativeConstants.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Toggles play/pause playback (VK_MEDIA_PLAY_PAUSE = 0xB3).
    /// </summary>
    public static bool PlayPause() => SendMediaKey(NativeConstants.VK_MEDIA_PLAY_PAUSE);

    /// <summary>
    /// Skips to the next media track (VK_MEDIA_NEXT_TRACK = 0xB0).
    /// </summary>
    public static bool NextTrack() => SendMediaKey(NativeConstants.VK_MEDIA_NEXT_TRACK);

    /// <summary>
    /// Goes back to the previous media track (VK_MEDIA_PREV_TRACK = 0xB1).
    /// </summary>
    public static bool PreviousTrack() => SendMediaKey(NativeConstants.VK_MEDIA_PREV_TRACK);

    /// <summary>
    /// Stops media playback (VK_MEDIA_STOP = 0xB2).
    /// </summary>
    public static bool Stop() => SendMediaKey(NativeConstants.VK_MEDIA_STOP);
}
