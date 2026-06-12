#if NETSTANDARD2_0
namespace ContractGuard.Core;

/// <summary>BCL conveniences the netstandard2.0 surface lacks.</summary>
internal static class Compat
{
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;

    public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;

    public static bool EndsWith(this string text, char value) => text.Length > 0 && text[^1] == value;

    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
        this IEnumerable<TFirst> first, IEnumerable<TSecond> second)
    {
        using IEnumerator<TFirst> a = first.GetEnumerator();
        using IEnumerator<TSecond> b = second.GetEnumerator();
        while (a.MoveNext() && b.MoveNext())
            yield return (a.Current, b.Current);
    }

    public static TValue? GetValueOrDefault<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        => dictionary.TryGetValue(key, out TValue? value) ? value : default;

    public static TValue GetValueOrDefault<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        where TKey : notnull
        => dictionary.TryGetValue(key, out TValue? value) ? value : defaultValue;
}
#endif
