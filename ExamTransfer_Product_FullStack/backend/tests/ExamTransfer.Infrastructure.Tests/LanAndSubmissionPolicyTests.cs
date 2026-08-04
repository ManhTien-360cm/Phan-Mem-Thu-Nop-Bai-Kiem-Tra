using System.Net;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class LanAndSubmissionPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.50.10", true)]
    [InlineData("::ffff:192.168.50.11", true)]
    [InlineData("192.168.51.10", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("not-an-ip", false)]
    public void LanAccessPolicy_OnlyAllowsLoopbackOrConfiguredLocalSubnet(string address, bool expected)
    {
        var options = new ExamTransferOptions();
        options.Discovery.AdditionalAllowedCidrs.Add("192.168.50.0/24");
        var policy = new LanAccessPolicy(Options.Create(options));

        Assert.Equal(expected, policy.IsAllowed(address));
    }

    [Theory]
    [InlineData("192.168.50.0/24", true)]
    [InlineData("10.0.0.0/8", false)]
    [InlineData("10.0.0.0/9", true)]
    [InlineData("172.16.0.0/12", false)]
    [InlineData("172.20.0.0/16", true)]
    [InlineData("192.168.0.0/16", false)]
    [InlineData("192.168.0.0/17", true)]
    [InlineData("0.0.0.0/0", false)]
    [InlineData("8.8.8.0/24", false)]
    [InlineData("192.168.0.0/8", false)]
    [InlineData("not-a-cidr", false)]
    public void LanAccessPolicy_CidrParserAcceptsOnlyContainedPrivateRanges(string cidr, bool expected) =>
        Assert.Equal(expected, LanAccessPolicy.IsValidPrivateCidr(cidr));

    [Fact]
    public void ExamTransferOptionsValidator_FailsInvalidOrBroadCidrs()
    {
        var options = new ExamTransferOptions();
        options.LanAccess.AllowedCidrs.Add("0.0.0.0/0");
        options.LanAccess.TrustDockerDesktopNat = true;
        options.LanAccess.TrustedDockerGatewayCidrs.Add("192.168.65.0/24");

        var result = new ExamTransferOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, x => x.Contains("AllowedCidrs", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, x => x.Contains("/32", StringComparison.Ordinal));
    }

    [Fact]
    public void LanAccessPolicy_NativeModeRefreshesPhysicalSubnetForEveryDecision()
    {
        IReadOnlyList<LanAccessPolicy.NetworkRange> currentRanges =
        [
            LanAccessPolicy.NetworkRange.FromPrefix(IPAddress.Parse("192.168.50.20"), 24)
        ];
        var policy = new LanAccessPolicy(
            [],
            runningInContainer: false,
            localRangeProvider: () => currentRanges);

        var beforeNetworkChange = policy.Evaluate("192.168.50.42");
        currentRanges =
        [
            LanAccessPolicy.NetworkRange.FromPrefix(IPAddress.Parse("10.107.79.254"), 24)
        ];
        var staleSubnet = policy.Evaluate("192.168.50.42");
        var sameWifiSubnet = policy.Evaluate("10.107.79.25");

        Assert.True(beforeNetworkChange.Allowed);
        Assert.Equal("192.168.50.0/24", beforeNetworkChange.MatchedRange);
        Assert.False(staleSubnet.Allowed);
        Assert.Equal("NO_MATCHING_ALLOWED_CIDR", staleSubnet.DeniedReason);
        Assert.True(sameWifiSubnet.Allowed);
        Assert.Equal("Native", sameWifiSubnet.RuntimeMode);
        Assert.Equal("10.107.79.0/24", sameWifiSubnet.MatchedRange);
    }

    [Fact]
    public void LanAccessPolicy_DockerGatewayRequiresExplicitNarrowTrust()
    {
        var gateway = LanAccessPolicy.NetworkRange.FromPrefix(
            IPAddress.Parse("192.168.65.1"),
            32);
        var notConfigured = new LanAccessPolicy(
            [],
            runningInContainer: true);
        var configuredButDisabled = new LanAccessPolicy(
            [gateway],
            [gateway],
            trustDockerDesktopNat: false,
            runningInContainer: true);
        var trusted = new LanAccessPolicy(
            [],
            [gateway],
            trustDockerDesktopNat: true,
            runningInContainer: true);

        Assert.False(notConfigured.Evaluate("192.168.65.1").Allowed);
        var disabled = configuredButDisabled.Evaluate("192.168.65.1");
        Assert.False(disabled.Allowed);
        Assert.Equal("DOCKER_GATEWAY_TRUST_DISABLED", disabled.DeniedReason);
        var allowed = trusted.Evaluate("192.168.65.1");
        Assert.True(allowed.Allowed);
        Assert.Equal("Docker", allowed.RuntimeMode);
        Assert.Equal("docker-gateway:192.168.65.1/32", allowed.MatchedRange);
        Assert.False(trusted.Evaluate("192.168.65.2").Allowed);
    }

    [Theory]
    [InlineData("bailam.zip", true)]
    [InlineData("BAILAM.RAR", true)]
    [InlineData("bai lam.7z", true)]
    [InlineData("bailam.pdf", false)]
    [InlineData("bailam.zip.exe", false)]
    public void StudentSubmissionPolicy_AllowsExactlyTheArchiveExtensions(string fileName, bool expected) =>
        Assert.Equal(expected, StudentSubmissionPolicy.IsAllowedExtension(fileName));

    [Fact]
    public void StudentSubmissionPolicy_UsesExactFixedLimits()
    {
        Assert.Equal(1, StudentSubmissionPolicy.MaxFileCount);
        Assert.Equal(10L * 1024 * 1024, StudentSubmissionPolicy.MaxBytes);
    }

    [Fact]
    public async Task ArchiveSignatureValidator_RejectsRenamedExecutableAndAcceptsMatchingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExamTransfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var valid = Path.Combine(root, "valid.zip");
            var invalid = Path.Combine(root, "renamed.zip");
            await File.WriteAllBytesAsync(valid, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);
            await File.WriteAllBytesAsync(invalid, [0x4D, 0x5A, 0x90, 0, 0, 0, 0, 0]);

            Assert.True(await ArchiveSignatureValidator.MatchesExtensionAsync(valid, "valid.zip"));
            Assert.False(await ArchiveSignatureValidator.MatchesExtensionAsync(invalid, "renamed.zip"));

            foreach (var sample in new[]
            {
                (Name: "valid.rar", Signature: new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0, 0 }),
                (Name: "valid.7z", Signature: new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 })
            })
            {
                var path = Path.Combine(root, sample.Name);
                await File.WriteAllBytesAsync(path, sample.Signature);
                Assert.True(await ArchiveSignatureValidator.MatchesExtensionAsync(path, sample.Name));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
