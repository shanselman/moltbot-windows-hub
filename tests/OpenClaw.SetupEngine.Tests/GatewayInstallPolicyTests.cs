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

    [Theory]
    [InlineData("2026.9.1")]
    [InlineData("2026.7.1-2")]
    [InlineData("2026.9.2-beta.3")]
    [InlineData("2026.9.2-rc-hotfix.1+build-local")]
    public void PackageVersionParser_AcceptsPublishedVersionShapes(string value)
    {
        Assert.True(GatewayPackageVersion.IsExact(value));
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
    public void ValidateAndApply_AcceptsExactOfficialVersion()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = "2026.8.1" }
        };

        GatewayInstallPolicy.ValidateAndApply(config);

        Assert.Equal("2026.8.1", config.Gateway.Version);
    }

    [Theory]
    [InlineData("latest", "latest")]
    [InlineData("stable", "latest")]
    [InlineData("extended-stable", "extended-stable")]
    [InlineData("beta", "beta")]
    [InlineData("dev", "dev")]
    [InlineData("next", "next")]
    public void ValidateAndApply_AcceptsUpstreamChannelSelectors(string selector, string expected)
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = selector }
        };

        GatewayInstallPolicy.ValidateAndApply(config);

        Assert.Equal(expected, config.Gateway.Version);
    }

    [Fact]
    public void ValidateAndApply_RejectsUnknownOfficialSelector()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Version = "workspace:*" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayInstallPolicy.ValidateAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
    }

    [Theory]
    [InlineData("""{"Gateway":{"Selection":"recommended"}}""", GatewayInstallPolicy.LegacyRecommendedVersion)]
    [InlineData("""{"Gateway":{"Selection":"recommended","Version":"2026.6.34"}}""", "2026.6.34")]
    [InlineData("""{"Gateway":{"Selection":"exact","Version":"2026.6.34"}}""", "2026.6.34")]
    [InlineData("""{"Gateway":{"Version":"2026.6.34"}}""", "2026.6.34")]
    [InlineData("""{"Gateway":{"Selection":"fallback"}}""", GatewayInstallPolicy.LegacyFallbackVersion)]
    [InlineData("""{"Gateway":{"Selection":"fallback","Version":"2026.6.11"}}""", "2026.6.11")]
    public void ValidateAndApply_MigratesLegacySelection(
        string json,
        string? expectedVersion)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<SetupConfig>(
            json,
            SetupConfig.JsonOptions)!;

        GatewayInstallPolicy.ValidateAndApply(config);

        Assert.Null(config.Gateway.Selection);
        Assert.Equal(expectedVersion, config.Gateway.Version);
    }

    [Fact]
    public void ValidateAndApply_ValidatesConfiguredFallback()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { FallbackVersion = "beta" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayInstallPolicy.ValidateAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
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
            Gateway = new GatewayConfig
            {
                Version = "latest",
                InstalledVersion = "2026.8.1"
            }
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

    [Fact]
    public void ConfiguredFallback_IsOfferedOnlyForTypedCompatibilityFailures()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Version = "latest",
                InstalledVersion = "2026.9.1",
                FallbackVersion = "2026.6.34"
            }
        };

        Assert.True(GatewayInstallPolicy.CanRetryWithFallback(
            config,
            GatewayCompatibilityFailureKind.ProtocolMismatch));
        Assert.False(GatewayInstallPolicy.CanRetryWithFallback(
            config,
            GatewayCompatibilityFailureKind.InstalledRuntimeMismatch));

        Assert.True(GatewayInstallPolicy.TryApplyFallback(config, out var error), error);
        Assert.Equal("2026.6.34", config.Gateway.Version);
        Assert.Null(config.Gateway.InstalledVersion);
        Assert.False(GatewayInstallPolicy.CanRetryWithFallback(
            config,
            GatewayCompatibilityFailureKind.ProtocolMismatch));
    }
}
