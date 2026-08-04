using System.Net;
using System.Net.Sockets;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class LanAccessPolicy : ILanAccessPolicy
{
    private readonly IReadOnlyList<NetworkRange> configuredRanges;
    private readonly IReadOnlyList<NetworkRange> trustedDockerGateways;
    private readonly bool trustDockerDesktopNat;
    private readonly bool runningInContainer;
    private readonly Func<IReadOnlyList<NetworkRange>> localRangeProvider;

    public LanAccessPolicy(IOptions<ExamTransferOptions> options)
        : this(
            ParseConfigured(
                    options.Value.LanAccess.AllowedCidrs
                        .Concat(options.Value.Discovery.AdditionalAllowedCidrs))
                .ToList(),
            ParseConfigured(options.Value.LanAccess.TrustedDockerGatewayCidrs).ToList(),
            options.Value.LanAccess.TrustDockerDesktopNat,
            LanNetworkConfiguration.RunningInContainer,
            GetLocalRanges)
    {
    }

    internal LanAccessPolicy(
        IReadOnlyList<NetworkRange> configuredRanges,
        IReadOnlyList<NetworkRange>? trustedDockerGateways = null,
        bool trustDockerDesktopNat = false,
        bool runningInContainer = false,
        Func<IReadOnlyList<NetworkRange>>? localRangeProvider = null)
    {
        this.configuredRanges = configuredRanges;
        this.trustedDockerGateways = trustedDockerGateways ?? [];
        this.trustDockerDesktopNat = trustDockerDesktopNat;
        this.runningInContainer = runningInContainer;
        this.localRangeProvider = localRangeProvider ?? (() => []);
    }

    public bool IsAllowed(string? remoteAddress) => Evaluate(remoteAddress).Allowed;

    public LanAccessDecision Evaluate(string? remoteAddress)
    {
        var runtimeMode = runningInContainer ? "Docker" : "Native";
        var ranges = runningInContainer
            ? configuredRanges
            : configuredRanges.Concat(localRangeProvider()).Distinct().ToList();
        var allowedCidrs = ranges
            .Select(range => range.Cidr)
            .Concat(trustDockerDesktopNat
                ? trustedDockerGateways.Select(range => $"docker-gateway:{range.Cidr}")
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!IPAddress.TryParse(remoteAddress, out var address))
            return Denied("REMOTE_IP_INVALID");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var effectiveClientIp = address.ToString();
        if (IPAddress.IsLoopback(address))
            return Allowed("loopback");
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return Denied("REMOTE_IP_NOT_IPV4");
        if (!IsPrivate(address))
            return Denied("REMOTE_IP_NOT_PRIVATE");

        var configuredGateway = runningInContainer
            ? trustedDockerGateways.FirstOrDefault(range => range.Contains(address))
            : default;
        if (configuredGateway != default)
            return trustDockerDesktopNat
                ? Allowed($"docker-gateway:{configuredGateway.Cidr}")
                : Denied("DOCKER_GATEWAY_TRUST_DISABLED");

        var matched = ranges.FirstOrDefault(range => range.Contains(address));
        if (matched != default)
            return Allowed(matched.Cidr);
        return Denied("NO_MATCHING_ALLOWED_CIDR");

        LanAccessDecision Allowed(string matchedRange) =>
            new(
                true,
                runtimeMode,
                remoteAddress,
                effectiveClientIp,
                allowedCidrs,
                matchedRange,
                "ALLOWED");

        LanAccessDecision Denied(string reason) =>
            new(
                false,
                runtimeMode,
                remoteAddress,
                IPAddress.TryParse(remoteAddress, out var parsed)
                    ? (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString()
                    : null,
                allowedCidrs,
                null,
                reason);
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool TryParseCidr(string value, out NetworkRange range)
        => TryParseCidr(value, out range, out _);

    public static bool IsValidPrivateCidr(string value) =>
        TryParseCidr(value, out _, out _);

    internal static bool TryParseCidr(string value, out NetworkRange range, out int prefix)
    {
        range = default;
        prefix = 0;
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out prefix)
            || !IsPrivateCidr(address, prefix))
            return false;

        range = NetworkRange.FromPrefix(address, prefix);
        return true;
    }

    private static bool IsPrivateCidr(IPAddress address, int prefix)
    {
        if (prefix is < 8 or > 32) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 10) return prefix >= 9;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return prefix >= 13;
        return bytes[0] == 192 && bytes[1] == 168 && prefix >= 17;
    }

    private static IReadOnlyList<NetworkRange> GetLocalRanges() =>
        LanIpv4Network.SelectUsable(LanIpv4Network.GetSystemInterfaces())
            .Select(item => NetworkRange.FromPrefix(item.Address, item.PrefixLength))
            .Distinct()
            .ToList();

    private static IEnumerable<NetworkRange> ParseConfigured(IEnumerable<string> values)
    {
        foreach (var value in values)
            if (TryParseCidr(value, out var range))
                yield return range;
    }

    internal readonly record struct NetworkRange(uint Network, uint Mask, int Prefix)
    {
        public string Cidr => $"{FromUInt32(Network)}/{Prefix}";

        public bool Contains(IPAddress address) =>
            (ToUInt32(address) & Mask) == Network;

        public static NetworkRange FromMask(IPAddress address, IPAddress mask)
        {
            var maskValue = ToUInt32(mask);
            var prefix = Convert.ToString(maskValue, 2).Count(bit => bit == '1');
            return new(ToUInt32(address) & maskValue, maskValue, prefix);
        }

        public static NetworkRange FromPrefix(IPAddress address, int prefix)
        {
            var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
            return new(ToUInt32(address) & mask, mask, prefix);
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress FromUInt32(uint value) =>
            new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
