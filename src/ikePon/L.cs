using System.Collections.Generic;
using System.Windows;

namespace ikePon;

/// <summary>
/// Localization helper. Call L.Load() once at startup, then L.S(key) / L.F(key, args) anywhere.
/// Thread-safe for reads after initial load.
/// </summary>
public static class L
{
    private static Dictionary<string, string> _strings = [];

    public static void Load(ResourceDictionary resources)
    {
        var dict = new Dictionary<string, string>();
        foreach (object key in resources.Keys)
        {
            if (key is string k && resources[k] is string v)
                dict[k] = v;
        }
        _strings = dict;
    }

    public static string S(string key) =>
        _strings.TryGetValue(key, out var v) ? v : $"[{key}]";

    public static string F(string key, params object[] args) =>
        string.Format(S(key), args);
}
