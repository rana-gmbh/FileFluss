// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;

namespace FileFluss.Native;

/// <summary>What the current keyboard layout types for a key, so shortcuts follow the key caps rather than US key codes.</summary>
public static class KeyboardLayout
{
    private const uint MapVkToChar = 2;

    /// <summary>The unshifted character a virtual key types on the current layout ('=' for VK_OEM_PLUS on US, '+' on German); null when none.</summary>
    public static char? CharFor(int virtualKey)
    {
        var value = MapVirtualKey((uint)virtualKey, MapVkToChar);

        // The high bit marks a dead key (´ ` ^ on many layouts); the low word is still its character.
        var character = (char)(value & 0xFFFF);
        return character == '\0' ? null : character;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
