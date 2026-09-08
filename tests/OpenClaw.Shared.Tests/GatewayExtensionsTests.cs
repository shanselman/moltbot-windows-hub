using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayExtensionsTests
{
    [Fact]
    public void GatewayFeatureSet_FromHelloOk_PreservesExactDistinctMethodsAndEvents()
    {
        using var document = JsonDocument.Parse("""
        {
          "features": {
            "methods": ["skills.search", "plugins.list", "skills.search", ""],
            "events": ["skills.changed", "plugins.changed", 12]
          }
        }
        """);

        var features = GatewayFeatureSet.FromHelloOk(document.RootElement);

        Assert.Equal(["skills.search", "plugins.list"], features.Methods);
        Assert.Equal(["skills.changed", "plugins.changed"], features.Events);
        Assert.True(features.SupportsMethod("plugins.list"));
        Assert.False(features.SupportsMethod("Plugins.List"));
    }

    [Theory]
    [InlineData(true, false, false, false, true, SkillReadinessState.Disabled)]
    [InlineData(false, true, false, false, true, SkillReadinessState.Blocked)]
    [InlineData(false, false, true, false, false, SkillReadinessState.Blocked)]
    [InlineData(false, false, false, true, false, SkillReadinessState.Incompatible)]
    [InlineData(false, false, false, false, false, SkillReadinessState.NeedsSetup)]
    [InlineData(false, false, false, false, true, SkillReadinessState.Ready)]
    public void SkillStatusEntry_Readiness_DoesNotConflateEnabledWithEligible(
        bool disabled,
        bool allowlistBlocked,
        bool agentBlocked,
        bool platformIncompatible,
        bool eligible,
        SkillReadinessState expected)
    {
        var skill = new SkillStatusEntry
        {
            Disabled = disabled,
            BlockedByAllowlist = allowlistBlocked,
            BlockedByAgentFilter = agentBlocked,
            PlatformIncompatible = platformIncompatible,
            Eligible = eligible,
        };

        Assert.Equal(expected, skill.Readiness);
    }

    [Fact]
    public void GatewayRequestException_PreservesConsentTokenAndRedactsOtherSecrets()
    {
        using var document = JsonDocument.Parse("""
        {
          "ok": false,
          "error": {
            "code": "UNAVAILABLE",
            "message": "Consent required",
            "details": {
              "capabilityConsentCode": "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId": "voice-call",
              "reviewToken": "review-token-exact",
              "apiToken": "must-not-survive"
            }
          }
        }
        """);

        var exception = GatewayRequestException.FromResponse(
            "plugins.install",
            document.RootElement,
            "request failed");

        Assert.Equal("plugins.install", exception.Method);
        Assert.Equal("UNAVAILABLE", exception.Code);
        Assert.Equal("review-token-exact", exception.Details!.Value.GetProperty("reviewToken").GetString());
        Assert.Equal("[REDACTED]", exception.Details.Value.GetProperty("apiToken").GetString());
    }

    [Fact]
    public void PluginCapabilityConsentDetails_ParsesExactGatewayShape()
    {
        using var document = JsonDocument.Parse("""
        {
          "ok": false,
          "error": {
            "code": "UNAVAILABLE",
            "message": "Consent required",
            "details": {
              "capabilityConsentCode": "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId": "voice-call",
              "reviewToken": "review-token-exact",
              "widened": {
                "tools": ["voice.call"],
                "mcpServers": ["voice"]
              }
            }
          }
        }
        """);
        var exception = GatewayRequestException.FromResponse(
            "plugins.install",
            document.RootElement,
            "request failed");

        var parsed = PluginCapabilityConsentDetails.TryParse(exception, out var consent);

        Assert.True(parsed);
        Assert.NotNull(consent);
        Assert.Equal("voice-call", consent!.PluginId);
        Assert.Equal("review-token-exact", consent.ReviewToken);
        Assert.Equal(["voice.call"], consent.Widened.Tools);
        Assert.Equal(["voice"], consent.Widened.McpServers);
    }

    [Fact]
    public void PluginCapabilityConsentDetails_RejectsUnknownFields()
    {
        using var document = JsonDocument.Parse("""
        {
          "error": {
            "details": {
              "capabilityConsentCode": "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId": "voice-call",
              "reviewToken": "review-token-exact",
              "unexpected": true
            }
          }
        }
        """);
        var exception = GatewayRequestException.FromResponse(
            "plugins.install",
            document.RootElement,
            "request failed");

        Assert.False(PluginCapabilityConsentDetails.TryParse(exception, out _));
    }
}
