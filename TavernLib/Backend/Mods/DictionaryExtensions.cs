using System.Collections.Generic;

namespace TavernLib.Backend.Mods;

// net48's Dictionary<TKey,TValue> has no GetValueOrDefault (that overload only
// ships on netstandard2.1+/.NET Core); this stands in for it.
internal static class DictionaryExtensions
{
    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
    {
        return dict.TryGetValue(key, out var value) ? value : default;
    }
}
