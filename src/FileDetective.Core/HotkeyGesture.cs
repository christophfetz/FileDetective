using System.Globalization;

namespace FileDetective.Core;

/// <summary>Modifier keys of a global hotkey. The values equal the Win32 <c>MOD_*</c> flags.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A hotkey as stored in <c>settings.json</c> (<c>"Alt+Space"</c>): modifiers plus one key (virtual-key code). Only a
/// fixed set of keys is allowed, and the combination needs at least Alt, Ctrl or Win, because a hotkey of a plain
/// letter (or Shift+letter) would block typing in every program.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly (string Name, uint Key)[] NamedKeys =
    [
        ("Space", 0x20), ("Tab", 0x09), ("Enter", 0x0D), ("Backspace", 0x08), ("Insert", 0x2D), ("Delete", 0x2E),
        ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
    ];

    private static readonly Dictionary<string, string> GermanNames = new(StringComparer.Ordinal)
    {
        ["Ctrl"] = "Strg",
        ["Space"] = "Leertaste",
        ["Enter"] = "Eingabe",
        ["Backspace"] = "Rücktaste",
        ["Insert"] = "Einfg",
        ["Delete"] = "Entf",
        ["Home"] = "Pos1",
        ["End"] = "Ende",
        ["PageUp"] = "BildAuf",
        ["PageDown"] = "BildAb",
        ["Left"] = "Links",
        ["Up"] = "Hoch",
        ["Right"] = "Rechts",
        ["Down"] = "Runter",
    };

    /// <summary>Alt+Space (PLAN.md 3).</summary>
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Alt, 0x20);

    /// <summary>True if the key is one of the allowed keys and the modifiers contain Alt, Ctrl or Win.</summary>
    public bool IsValid => KeyName(VirtualKey) is not null
        && (Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Win)) != 0;

    /// <summary>True if <paramref name="virtualKey"/> may be the key of a hotkey (letters, digits, F1-F24, a few named keys).</summary>
    public static bool IsAllowedKey(uint virtualKey) => KeyName(virtualKey) is not null;

    /// <summary>Parses <c>"Ctrl+Alt+K"</c>; case-insensitive, any order of the parts. Returns false for invalid input.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL" or "STRG":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT" or "UMSCHALT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key != 0 || !TryParseKey(raw, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        var result = new HotkeyGesture(modifiers, key);
        if (!result.IsValid)
        {
            return false;
        }

        gesture = result;
        return true;
    }

    /// <summary>Text for <c>settings.json</c>: always English and in a fixed order (<c>Ctrl+Alt+Shift+Win+Key</c>).</summary>
    public string Format() => Format(null);

    /// <summary>Text for the user; with a German UI culture <c>Strg</c>, <c>Leertaste</c> and so on.</summary>
    public string Format(CultureInfo? uiCulture)
    {
        bool german = uiCulture is not null && uiCulture.TwoLetterISOLanguageName == "de";
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add(Localize("Ctrl", german));
        }

        if ((Modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add(german ? "Umschalt" : "Shift");
        }

        if ((Modifiers & HotkeyModifiers.Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(Localize(KeyName(VirtualKey) ?? "?", german));
        return string.Join('+', parts);
    }

    public override string ToString() => Format();

    private static string Localize(string name, bool german)
        => german && GermanNames.TryGetValue(name, out string? text) ? text : name;

    private static bool TryParseKey(string text, out uint key)
    {
        key = 0;
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                key = c;
                return true;
            }

            return false;
        }

        if ((text[0] is 'F' or 'f')
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            key = (uint)(0x70 + f - 1);
            return true;
        }

        foreach (var (name, code) in NamedKeys)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                key = code;
                return true;
            }
        }

        // German spellings as shown in the German UI.
        foreach (var (name, german) in GermanNames)
        {
            if (string.Equals(german, text, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (known, code) in NamedKeys)
                {
                    if (known == name)
                    {
                        key = code;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string? KeyName(uint key)
    {
        if (key is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return ((char)key).ToString();
        }

        if (key is >= 0x70 and <= 0x87)
        {
            return "F" + (key - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        }

        foreach (var (name, code) in NamedKeys)
        {
            if (code == key)
            {
                return name;
            }
        }

        return null;
    }
}
