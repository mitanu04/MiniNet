using System.Text.RegularExpressions;

namespace MiniNet.Core;

public readonly struct MacAddress : IEquatable<MacAddress>
{
    private static readonly Regex Format = new(@"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    public static readonly MacAddress Broadcast = new("FF:FF:FF:FF:FF:FF");

    private readonly string _value;

    public MacAddress(string value)
    {
        if (!Format.IsMatch(value))
            throw new ArgumentException($"Invalid MAC address format: '{value}'", nameof(value));
        _value = value.ToUpperInvariant();
    }

    public bool IsBroadcast => this == Broadcast;

    public bool Equals(MacAddress other) => string.Equals(_value, other._value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is MacAddress m && Equals(m);
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;
    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(MacAddress left, MacAddress right) => left.Equals(right);
    public static bool operator !=(MacAddress left, MacAddress right) => !left.Equals(right);

    public static implicit operator MacAddress(string s) => new(s);
}
