namespace ZapretRemote.Models;

/// <summary>
/// Represents an active IPv4 network interface candidate for LAN proxy binding.
/// </summary>
public sealed record LanAdapterInfo(
    string Name,
    string Description,
    string IpAddress,
    bool IsVirtual,
    bool HasGateway)
{
    public string DisplayName => IsVirtual
        ? $"{IpAddress} — {Name} [вирт.]"
        : $"{IpAddress} — {Name}";

    public override string ToString() => DisplayName;
}
