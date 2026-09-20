using System.Runtime.InteropServices;
using System.Text.Json;
using Gem.Core;
using Gem.Win32;

namespace Gem.Handlers;

/// <summary>
/// Command handler for simulating hotkeys and key combinations via Windows P/Invoke SendInput.
/// Example args: { "keys": ["ctrl", "shift", "esc"] } or { "key": "volume_up" }
/// </summary>
public sealed class HotkeyCommandHandler : ICommandHandler
{
    public string CommandName => "hotkey";

    public async Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        var keyNames = ExtractKeyNames(args);
        if (keyNames.Count == 0)
        {
            return CommandResult.Fail("No keys specified. Provide 'keys' array (e.g. ['ctrl', 'shift', 'esc']) or single 'key'.");
        }

        int delayMs = 50;
        if (args.TryGetProperty("delayMs", out var delayProp) && delayProp.TryGetInt32(out var parsedDelay))
        {
            delayMs = Math.Clamp(parsedDelay, 10, 5000);
        }

        var vkCodes = new List<ushort>();
        foreach (var keyName in keyNames)
        {
            if (!TryResolveVirtualKey(keyName, out var vk))
            {
                return CommandResult.Fail(
                    $"Unknown key name '{keyName}'. Supported: ctrl, alt, shift, win, enter, esc, space, tab, backspace, delete, arrows, f1-f12, a-z, 0-9, volume_up/down/mute, etc."
                );
            }
            vkCodes.Add(vk);
        }

        // Fast-path: single multimedia key dispatch via MediaKeyService (0ms latency, atomic keydown/keyup)
        if (vkCodes.Count == 1 && IsMediaKey(vkCodes[0]))
        {
            bool ok = Gem.Services.MediaKeyService.SendMediaKey(vkCodes[0]);
            return ok
                ? CommandResult.Ok(
                    $"Successfully simulated media key 0x{vkCodes[0]:X2} [{keyNames[0]}] via MediaKeyService.",
                    new
                    {
                        Keys = keyNames,
                        VirtualKeyCodes = vkCodes.Select(v => $"0x{v:X2}"),
                        Method = "MediaKeyService.SendInput"
                    })
                : CommandResult.Fail($"Failed to dispatch media key {keyNames[0]}.");
        }

        try
        {
            // 1. Primary mechanism: Win32 SendInput
            var downInputs = vkCodes.Select(vk => CreateKeyInput(vk, isKeyUp: false)).ToArray();
            uint downResult = NativeMethods.SendInput((uint)downInputs.Length, downInputs, Marshal.SizeOf<INPUT>());

            if (downResult != downInputs.Length)
            {
                int error = Marshal.GetLastWin32Error();

                // If blocked by Windows UIPI (Error 5: Access Denied / elevated foreground window),
                // fall back to keybd_event to ensure reliable hotkey simulation.
                if (error == 5)
                {
                    foreach (var vk in vkCodes)
                    {
                        byte scan = (byte)NativeMethods.MapVirtualKey(vk, 0);
                        uint flags = IsExtendedKey(vk) ? NativeConstants.KEYEVENTF_EXTENDEDKEY : 0;
                        NativeMethods.keybd_event((byte)vk, scan, flags, UIntPtr.Zero);
                    }

                    await Task.Delay(delayMs, cancellationToken);

                    foreach (var vk in vkCodes.AsEnumerable().Reverse())
                    {
                        byte scan = (byte)NativeMethods.MapVirtualKey(vk, 0);
                        uint flags = NativeConstants.KEYEVENTF_KEYUP | (IsExtendedKey(vk) ? NativeConstants.KEYEVENTF_EXTENDEDKEY : 0);
                        NativeMethods.keybd_event((byte)vk, scan, flags, UIntPtr.Zero);
                    }

                    return CommandResult.Ok(
                        $"Simulated hotkey [{string.Join(" + ", keyNames)}] (SendInput encountered UIPI Error 5; dispatched via keybd_event).",
                        new
                        {
                            Keys = keyNames,
                            VirtualKeyCodes = vkCodes.Select(v => $"0x{v:X2}"),
                            Method = "keybd_event (UIPI fallback)",
                            DelayMs = delayMs
                        }
                    );
                }

                return CommandResult.Fail($"Failed sending KeyDown inputs via SendInput. Win32 error code: {error}");
            }

            // Small delay to allow Windows to register key combination
            await Task.Delay(delayMs, cancellationToken);

            // 2. Send Key-Up events in reverse sequence
            var upInputs = vkCodes.AsEnumerable().Reverse().Select(vk => CreateKeyInput(vk, isKeyUp: true)).ToArray();
            uint upResult = NativeMethods.SendInput((uint)upInputs.Length, upInputs, Marshal.SizeOf<INPUT>());
            if (upResult != upInputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                return CommandResult.Fail($"Failed sending KeyUp inputs via SendInput. Win32 error code: {error}");
            }

            return CommandResult.Ok(
                $"Successfully simulated hotkey sequence via SendInput: [{string.Join(" + ", keyNames)}].",
                new
                {
                    Keys = keyNames,
                    VirtualKeyCodes = vkCodes.Select(v => $"0x{v:X2}"),
                    Method = "SendInput",
                    InputsDispatched = downResult + upResult,
                    DelayMs = delayMs
                }
            );
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed executing hotkey simulation: {ex.Message}");
        }
    }

    private static List<string> ExtractKeyNames(JsonElement args)
    {
        var list = new List<string>();

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("keys", out var keysProp) && keysProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in keysProp.EnumerateArray())
                {
                    var k = element.GetString();
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        list.Add(k.Trim());
                    }
                }
            }
            else if (args.TryGetProperty("key", out var singleKeyProp) && singleKeyProp.ValueKind == JsonValueKind.String)
            {
                var k = singleKeyProp.GetString();
                if (!string.IsNullOrWhiteSpace(k))
                {
                    list.Add(k.Trim());
                }
            }
        }
        else if (args.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in args.EnumerateArray())
            {
                var k = element.GetString();
                if (!string.IsNullOrWhiteSpace(k))
                {
                    list.Add(k.Trim());
                }
            }
        }
        else if (args.ValueKind == JsonValueKind.String)
        {
            var k = args.GetString();
            if (!string.IsNullOrWhiteSpace(k))
            {
                list.Add(k.Trim());
            }
        }

        return list;
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool isKeyUp)
    {
        uint flags = 0;
        if (isKeyUp)
        {
            flags |= NativeConstants.KEYEVENTF_KEYUP;
        }

        if (IsExtendedKey(virtualKey))
        {
            flags |= NativeConstants.KEYEVENTF_EXTENDEDKEY;
        }

        ushort scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0);

        return new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort vk)
    {
        return vk switch
        {
            NativeConstants.VK_UP or NativeConstants.VK_DOWN or NativeConstants.VK_LEFT or NativeConstants.VK_RIGHT or
            NativeConstants.VK_HOME or NativeConstants.VK_END or NativeConstants.VK_PRIOR or NativeConstants.VK_NEXT or
            NativeConstants.VK_INSERT or NativeConstants.VK_DELETE or
            NativeConstants.VK_LWIN or NativeConstants.VK_RWIN or NativeConstants.VK_APPS or
            NativeConstants.VK_VOLUME_MUTE or NativeConstants.VK_VOLUME_DOWN or NativeConstants.VK_VOLUME_UP or
            NativeConstants.VK_MEDIA_NEXT_TRACK or NativeConstants.VK_MEDIA_PREV_TRACK or
            NativeConstants.VK_MEDIA_STOP or NativeConstants.VK_MEDIA_PLAY_PAUSE => true,
            _ => false
        };
    }

    private static bool IsMediaKey(ushort vk) =>
        vk is NativeConstants.VK_MEDIA_PLAY_PAUSE
           or NativeConstants.VK_MEDIA_NEXT_TRACK
           or NativeConstants.VK_MEDIA_PREV_TRACK
           or NativeConstants.VK_MEDIA_STOP;

    private static bool TryResolveVirtualKey(string name, out ushort vk)
    {
        string normalized = name.Trim().ToLowerInvariant().Replace("-", "_");

        // Single letter 'a' - 'z'
        if (normalized.Length == 1 && normalized[0] >= 'a' && normalized[0] <= 'z')
        {
            vk = (ushort)char.ToUpperInvariant(normalized[0]);
            return true;
        }

        // Single digit '0' - '9'
        if (normalized.Length == 1 && normalized[0] >= '0' && normalized[0] <= '9')
        {
            vk = (ushort)normalized[0];
            return true;
        }

        switch (normalized)
        {
            // Modifiers
            case "ctrl":
            case "control":
                vk = NativeConstants.VK_CONTROL;
                return true;
            case "alt":
            case "menu":
                vk = NativeConstants.VK_MENU;
                return true;
            case "shift":
                vk = NativeConstants.VK_SHIFT;
                return true;
            case "win":
            case "windows":
            case "super":
            case "lwin":
                vk = NativeConstants.VK_LWIN;
                return true;
            case "rwin":
                vk = NativeConstants.VK_RWIN;
                return true;
            case "apps":
                vk = NativeConstants.VK_APPS;
                return true;

            // Common actions
            case "enter":
            case "return":
                vk = NativeConstants.VK_RETURN;
                return true;
            case "esc":
            case "escape":
                vk = NativeConstants.VK_ESCAPE;
                return true;
            case "tab":
                vk = NativeConstants.VK_TAB;
                return true;
            case "space":
            case "spacebar":
                vk = NativeConstants.VK_SPACE;
                return true;
            case "back":
            case "backspace":
                vk = NativeConstants.VK_BACK;
                return true;
            case "del":
            case "delete":
                vk = NativeConstants.VK_DELETE;
                return true;
            case "ins":
            case "insert":
                vk = NativeConstants.VK_INSERT;
                return true;
            case "home":
                vk = NativeConstants.VK_HOME;
                return true;
            case "end":
                vk = NativeConstants.VK_END;
                return true;
            case "pageup":
            case "pgup":
                vk = NativeConstants.VK_PRIOR;
                return true;
            case "pagedown":
            case "pgdn":
                vk = NativeConstants.VK_NEXT;
                return true;
            case "printscreen":
            case "prtsc":
            case "snapshot":
                vk = NativeConstants.VK_SNAPSHOT;
                return true;
            case "caps":
            case "capslock":
                vk = NativeConstants.VK_CAPITAL;
                return true;
            case "pause":
                vk = NativeConstants.VK_PAUSE;
                return true;

            // Arrows
            case "up":
            case "arrowup":
                vk = NativeConstants.VK_UP;
                return true;
            case "down":
            case "arrowdown":
                vk = NativeConstants.VK_DOWN;
                return true;
            case "left":
            case "arrowleft":
                vk = NativeConstants.VK_LEFT;
                return true;
            case "right":
            case "arrowright":
                vk = NativeConstants.VK_RIGHT;
                return true;

            // Media & Volume
            case "volume_mute":
            case "mute":
                vk = NativeConstants.VK_VOLUME_MUTE;
                return true;
            case "volume_down":
            case "volumedown":
                vk = NativeConstants.VK_VOLUME_DOWN;
                return true;
            case "volume_up":
            case "volumeup":
                vk = NativeConstants.VK_VOLUME_UP;
                return true;
            case "media_next":
                vk = NativeConstants.VK_MEDIA_NEXT_TRACK;
                return true;
            case "media_prev":
                vk = NativeConstants.VK_MEDIA_PREV_TRACK;
                return true;
            case "media_play_pause":
            case "play_pause":
                vk = NativeConstants.VK_MEDIA_PLAY_PAUSE;
                return true;
            case "media_stop":
                vk = NativeConstants.VK_MEDIA_STOP;
                return true;

            // Function keys
            case "f1": vk = NativeConstants.VK_F1; return true;
            case "f2": vk = NativeConstants.VK_F2; return true;
            case "f3": vk = NativeConstants.VK_F3; return true;
            case "f4": vk = NativeConstants.VK_F4; return true;
            case "f5": vk = NativeConstants.VK_F5; return true;
            case "f6": vk = NativeConstants.VK_F6; return true;
            case "f7": vk = NativeConstants.VK_F7; return true;
            case "f8": vk = NativeConstants.VK_F8; return true;
            case "f9": vk = NativeConstants.VK_F9; return true;
            case "f10": vk = NativeConstants.VK_F10; return true;
            case "f11": vk = NativeConstants.VK_F11; return true;
            case "f12": vk = NativeConstants.VK_F12; return true;

            default:
                vk = 0;
                return false;
        }
    }
}
