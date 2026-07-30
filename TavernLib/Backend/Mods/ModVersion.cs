using System;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Exactly "MAJOR.MINOR.PATCH" - no pre-release/build metadata. A version that
/// isn't exactly three numeric segments is rejected, so ordering never has to
/// guess how to rank a pre-release tag.
/// </summary>
public readonly struct ModVersion : IComparable<ModVersion>, IEquatable<ModVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    private ModVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public static ModVersion Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v))
            throw new ModManagerException("Version must be a non-empty string.");
        var parts = v.Trim().Split('.');
        if (parts.Length != 3)
            throw new ModManagerException(
                $"Version '{v}' isn't MAJOR.MINOR.PATCH: exactly three numeric segments are required (no pre-release/build metadata).");
        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
            throw new ModManagerException($"Version '{v}' has a non-numeric segment; only digits are allowed.");
        return new ModVersion(major, minor, patch);
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

    public int CompareTo(ModVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public bool Equals(ModVersion other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;
    public override bool Equals(object obj) => obj is ModVersion other && Equals(other);
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Major;
            hash = hash * 397 ^ Minor;
            hash = hash * 397 ^ Patch;
            return hash;
        }
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;
    public static bool operator ==(ModVersion a, ModVersion b) => a.Equals(b);
    public static bool operator !=(ModVersion a, ModVersion b) => !a.Equals(b);
}
