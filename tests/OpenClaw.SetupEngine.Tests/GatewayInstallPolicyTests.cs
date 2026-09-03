using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayInstallPolicyTests
{
    [Theory]
    [InlineData("2026.7.1")]
    [InlineData("2026.7.1-2")]
    public void StableVersionParser_AcceptsCorrectionReleases(string value)
    {
        Assert.True(GatewayReleaseVersion.TryParse(value, out _));
    }

    [Theory]
    [InlineData("2026.7.2-beta.7")]
    [InlineData("latest")]
    [InlineData("2026.13.1")]
    public void StableVersionParser_RejectsPrereleaseTagsAndInvalidValues(string value)
    {
        Assert.False(GatewayReleaseVersion.TryParse(value, out _));
    }

    [Fact]
    public void ValidateAndApply_DefaultsOfficialInstallerToNpmLatest()
    {
        var config = new SetupConfig();

        GatewayInstallPolicy.ValidateAndApply(config);

        Assert.Null(config.Gateway.Version);
        Assert.True(GatewayInstallPolicy.IsOfficialInstallerUrl(config.Gateway.InstallUrl));
    }

    [Fact]
    public void ValidateAndApply_RejectsOfficialVersionPinInProductMode()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = "2026.8.1" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayInstallPolicy.ValidateAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
        Assert.Contains("npm latest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAndApply_AllowsExactVersionOnlyForCandidateValidation()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = "2026.8.1" }
        };

        GatewayInstallPolicy.ValidateAndApply(config, allowExactCandidate: true);

        Assert.Equal("2026.8.1", config.Gateway.Version);
    }

    [Fact]
    public void ValidateAndApply_CustomInstallerRequiresExactVersion()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "https://example.test/install.sh" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayInstallPolicy.ValidateAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
    }

    [Fact]
    public void ValidateAndApply_CustomInstallerAcceptsExactVersion()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                InstallUrl = "https://example.test/install.sh",
                Version = "2026.8.1"
            }
        };

        GatewayInstallPolicy.ValidateAndApply(config);

        Assert.Equal("2026.8.1", config.Gateway.Version);
    }

    [Fact]
    public void ValidateAndApplyValidationPackage_RequiresExactVersion()
    {
        var config = new SetupConfig();

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayInstallPolicy.ValidateAndApplyValidationPackage(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
    }

    [Fact]
    public void ValidateHandshake_RequiresProtocolFourAndInstalledServerVersion()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = "2026.8.1" }
        };

        Assert.Null(GatewayInstallPolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo
            {
                Protocol = GatewayInstallPolicy.ProtocolGeneration,
                ServerVersion = "2026.8.1"
            }));

        var protocolError = GatewayInstallPolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo { Protocol = 3, ServerVersion = "2026.8.1" });
        Assert.Equal(GatewayCompatibilityFailureKind.ProtocolMismatch, protocolError?.Kind);

        var versionError = GatewayInstallPolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo { Protocol = 4, ServerVersion = "2026.8.2" });
        Assert.Equal(GatewayCompatibilityFailureKind.ServerVersionMismatch, versionError?.Kind);
    }
}
