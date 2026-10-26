using System;
using System.Globalization;
using System.Linq;

namespace TavernLib.Backend.Mods;

/// <summary>Exactly MAJOR.MINOR.PATCH, with no pre-release forms so ordering never guesses. Twin of
/// version.py's _parse_version.</summary>
public readonly struct ModVersion(int major, int minor, int patch) : IComparable<ModVersion>
{
    public int Major { get; } = major;
    public int Minor { get; } = minor;
    public int Patch { get; } = patch;

    public static ModVersion Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            throw new ModManagerException("Version must be a non-empty string.");
        var parts = v.Trim().Split('.');
        if (parts.Length != 3)
            throw new ModManagerException(
                $"Version '{v}' isn't MAJOR.MINOR.PATCH: exactly three numeric segments are required (no pre-release/build metadata).");
        return new ModVersion(Segment(v, parts[0]), Segment(v, parts[1]), Segment(v, parts[2]));
    }

    // ASCII digits only, as version.py's _SEGMENT: int.TryParse alone would also take a sign, inner
    // whitespace and other cultures' digits.
    private static int Segment(string v, string part)
    {
        if (part.Length == 0 || part.Any(c => c < '0' || c > '9'))
            throw new ModManagerException($"Version '{v}' has a non-numeric segment; only digits are allowed.");
        if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new ModManagerException($"Version '{v}' has a segment larger than {int.MaxValue}.");
        return n;
    }

    public static bool TryParse(string v, out ModVersion result)
    {
        try
        {
            result = Parse(v);
            return true;
        }
        catch (ModManagerException)
        {
            result = default;
            return false;
        }
    }

    public int CompareTo(ModVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;
}
