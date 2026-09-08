using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayReleasePolicyTests
{
    [Fact]
    public void EmbeddedPolicy_IsInternallyValid()
    {
        Assert.Empty(GatewayReleasePolicy.ValidateEmbeddedPolicy());
        Assert.Equal(
            GatewayReleaseStatus.Validated,
            GatewayReleasePolicy.Releases[GatewayReleasePolicy.RecommendedVersion].Status);
        Assert.Equal(
            GatewayReleaseStatus.Validated,
            GatewayReleasePolicy.Releases[GatewayReleasePolicy.FallbackVersion!].Status);
    }

    [Fact]
    public void CandidateEligibility_RequiresValidationGateAndNeverUnlocksRejectedRelease()
    {
        var candidate = new GatewayReleaseEvidence(
            "2026.8.1",
            GatewayReleaseStatus.Candidate,
            GatewayReleasePolicy.ProtocolGeneration,
            "sha512-candidate",
            "https://example.test/v2026.8.1",
            "test candidate");
        var rejected = candidate with
        {
            Status = GatewayReleaseStatus.Rejected,
            RejectionReason = "runtime proof failed"
        };

        Assert.False(GatewayReleasePolicy.IsReleaseEligible(candidate, allowCandidate: false));
        Assert.True(GatewayReleasePolicy.IsReleaseEligible(candidate, allowCandidate: true));
        Assert.False(GatewayReleasePolicy.IsReleaseEligible(rejected, allowCandidate: true));
    }

    [Fact]
    public void PluginLifecycleRelease_RemainsBlockedUntilAPluginCapableCandidateIsValidated()
    {
        Assert.False(GatewayReleasePolicy.RecommendedHasValidatedPluginLifecycle);
        Assert.Equal(
            GatewayReleaseStatus.Rejected,
            GatewayReleasePolicy.Releases[GatewayReleasePolicy.PluginCapableEvidenceRejectedVersion].Status);
        Assert.Contains(
            "provenance",
            GatewayReleasePolicy.Releases[GatewayReleasePolicy.PluginCapableEvidenceRejectedVersion].RejectionReason,
            StringComparison.OrdinalIgnoreCase);
    }

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
    public void ResolveAndApply_DefaultsToExactValidatedRecommendation()
    {
        var config = new SetupConfig();

        var result = GatewayReleasePolicy.ResolveAndApply(config);

        Assert.Equal(GatewayReleaseSelectionMode.Recommended, result.Mode);
        Assert.Equal(GatewayReleasePolicy.RecommendedVersion, config.Gateway.Version);
        Assert.Equal(GatewayReleaseStatus.Validated, result.Evidence?.Status);
        Assert.Equal(GatewayReleasePolicy.ProtocolGeneration, result.ProtocolGeneration);
    }

    [Fact]
    public void ResolveAndApply_RejectsVersionBelowSecurityFloor()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Selection = "exact", Version = "2026.6.10" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayReleasePolicy.ResolveAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.BelowSecurityFloor, error.Kind);
    }

    [Fact]
    public void ResolveAndApply_RejectsCandidateWithoutEvidenceGate()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Selection = "exact",
                Version = GatewayReleasePolicy.EvidenceRejectedVersion
            }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayReleasePolicy.ResolveAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.UnattestedRelease, error.Kind);
        Assert.Contains("provenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAndApply_AllowsValidatedRecommendationAsExactSelection()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Selection = "exact",
                Version = GatewayReleasePolicy.RecommendedVersion
            }
        };

        var result = GatewayReleasePolicy.ResolveAndApply(config);

        Assert.Equal(GatewayReleasePolicy.RecommendedVersion, result.Version);
        Assert.Equal(GatewayReleaseStatus.Validated, result.Evidence?.Status);
    }

    [Fact]
    public void ResolveAndApply_FallbackUsesDistinctValidatedRelease()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Selection = "fallback" }
        };

        var result = GatewayReleasePolicy.ResolveAndApply(config);

        Assert.Equal(GatewayReleaseSelectionMode.Fallback, result.Mode);
        Assert.Equal(GatewayReleasePolicy.FallbackVersion, result.Version);
        Assert.Equal(GatewayReleaseStatus.Validated, result.Evidence?.Status);
        Assert.NotEqual(GatewayReleasePolicy.RecommendedVersion, result.Version);
    }

    [Fact]
    public void ResolveAndApply_RejectedRuntimeReleaseRemainsIneligibleWithValidationGate()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                Selection = "exact",
                Version = GatewayReleasePolicy.RuntimeRejectedVersion
            }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayReleasePolicy.ResolveAndApply(config, allowCandidate: true));

        Assert.Equal(GatewayCompatibilityFailureKind.UnattestedRelease, error.Kind);
        Assert.Contains("wizard", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryApplyFallback_DoesNotRetryTheFallbackAgain()
    {
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);

        Assert.True(GatewayReleasePolicy.TryApplyFallback(config, out var firstError));
        Assert.Null(firstError);
        Assert.Equal(GatewayReleasePolicy.FallbackVersion, config.Gateway.Version);

        Assert.False(GatewayReleasePolicy.TryApplyFallback(config, out var secondError));
        Assert.Contains("already selected", secondError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryApplyFallback_DoesNotOfferProductFallbackForCustomInstaller()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                InstallUrl = "https://example.test/install.sh",
                Version = GatewayReleasePolicy.RecommendedVersion
            }
        };
        GatewayReleasePolicy.ResolveAndApply(config);

        Assert.False(GatewayReleasePolicy.CanRetryWithFallback(config));
        Assert.False(GatewayReleasePolicy.TryApplyFallback(config, out var error));
        Assert.Equal(GatewayReleasePolicy.RecommendedVersion, config.Gateway.Version);
        Assert.Contains("fallback", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryApplyFallback_AcceptsExplicitOfficialInstallerUrl()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                InstallUrl = GatewayReleasePolicy.DefaultInstallUrl
            }
        };
        GatewayReleasePolicy.ResolveAndApply(config);

        Assert.True(GatewayReleasePolicy.CanRetryWithFallback(config));
        Assert.True(GatewayReleasePolicy.TryApplyFallback(config, out var error));
        Assert.Null(error);
        Assert.Equal(GatewayReleasePolicy.FallbackVersion, config.Gateway.Version);
    }

    [Theory]
    [InlineData(GatewayCompatibilityFailureKind.InstalledVersionMismatch, true)]
    [InlineData(GatewayCompatibilityFailureKind.ProtocolMismatch, true)]
    [InlineData(GatewayCompatibilityFailureKind.ServerVersionMismatch, true)]
    [InlineData(GatewayCompatibilityFailureKind.InstalledRuntimeMismatch, false)]
    [InlineData(GatewayCompatibilityFailureKind.InvalidPolicy, false)]
    public void CanRetryWithFallback_DependsOnReleaseAddressableFailure(
        GatewayCompatibilityFailureKind failureKind,
        bool expected)
    {
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);

        Assert.Equal(expected, GatewayReleasePolicy.CanRetryWithFallback(config, failureKind));
    }

    [Fact]
    public void ResolveAndApply_RejectsUnembeddedCandidateEvenWithValidationGate()
    {
        const string version = "2026.8.1";
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { Selection = "exact", Version = version }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayReleasePolicy.ResolveAndApply(
                config,
                allowCandidate: true));

        Assert.Equal(GatewayCompatibilityFailureKind.UnattestedRelease, error.Kind);
    }

    [Fact]
    public void ResolveAndApply_CustomInstallerRequiresExactVersion()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { InstallUrl = "https://example.test/install.sh" }
        };

        var error = Assert.Throws<GatewayCompatibilityException>(
            () => GatewayReleasePolicy.ResolveAndApply(config));

        Assert.Equal(GatewayCompatibilityFailureKind.InvalidPolicy, error.Kind);
    }

    [Fact]
    public void ResolveAndApply_CustomInstallerIsExplicitlyUnverified()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig
            {
                InstallUrl = "https://example.test/install.sh",
                Version = GatewayReleasePolicy.SecurityFloor
            }
        };

        var result = GatewayReleasePolicy.ResolveAndApply(config);

        Assert.True(result.IsCustomInstaller);
        Assert.Null(result.Evidence);
        Assert.Equal(GatewayReleaseSelectionMode.Exact, result.Mode);
    }

    [Fact]
    public void ValidateHandshake_RequiresProtocolFourAndExactServerVersion()
    {
        var config = new SetupConfig();
        GatewayReleasePolicy.ResolveAndApply(config);

        Assert.Null(GatewayReleasePolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo
            {
                Protocol = GatewayReleasePolicy.ProtocolGeneration,
                ServerVersion = GatewayReleasePolicy.RecommendedVersion
            }));

        var protocolError = GatewayReleasePolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo { Protocol = 3, ServerVersion = GatewayReleasePolicy.RecommendedVersion });
        Assert.Equal(GatewayCompatibilityFailureKind.ProtocolMismatch, protocolError?.Kind);

        var versionError = GatewayReleasePolicy.ValidateHandshake(
            config,
            new GatewaySelfInfo { Protocol = 4, ServerVersion = "2026.7.1-2" });
        Assert.Equal(GatewayCompatibilityFailureKind.ServerVersionMismatch, versionError?.Kind);
    }
}
