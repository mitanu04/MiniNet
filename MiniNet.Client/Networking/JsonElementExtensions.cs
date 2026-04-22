using System.Text.Json;

namespace MiniNet.Client.Networking;

internal static class JsonElementExtensions
{
    public static string GetStringProp(this JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

    public static int GetIntProp(this JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;
}
