using System.Net;

namespace MiniNet.Core;

public readonly struct 
    IpAddress : IEquatable<IpAddress>
{
    private readonly string _value;

    public IpAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException($"Invalid IPv4 address: '{value}'", nameof(value));
        _value = parsed.ToString();
    }

    public bool Equals(IpAddress other) => string.Equals(_value, other._value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is IpAddress ip && Equals(ip);
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;
    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(IpAddress left, IpAddress right) => left.Equals(right);
    public static bool operator !=(IpAddress left, IpAddress right) => !left.Equals(right);

    public static implicit operator IpAddress(string s) => new(s);
}
