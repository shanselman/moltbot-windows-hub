using System.Text.Json;
using OpenClaw.SetupEngine.UI;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class WizardPayloadHelpersTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ExtractCurrentStepValidationError_returns_error_for_rejected_answer()
    {
        var payload = Parse("""{"done":false,"error":"Model ID is required","step":{"id":"model","type":"text"}}""");

        Assert.Equal(
            "Model ID is required",
            WizardPayloadHelpers.ExtractCurrentStepValidationError(payload, "model"));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("")]
    public void ExtractCurrentStepValidationError_ignores_non_current_steps(string currentStepId)
    {
        var payload = Parse("""{"done":false,"error":"Model ID is required","step":{"id":"model","type":"text"}}""");

        Assert.Equal(
            string.Empty,
            WizardPayloadHelpers.ExtractCurrentStepValidationError(payload, currentStepId));
    }

    // ---- ExtractStepMessage -----------------------------------------------

    [Fact]
    public void ExtractStepMessage_returns_string_message_unchanged()
    {
        var step = Parse("""{"type":"text","message":"Paste the code:"}""");
        Assert.Equal("Paste the code:", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_field_missing()
    {
        var step = Parse("""{"type":"note"}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_when_null()
    {
        var step = Parse("""{"type":"note","message":null}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_gemini_note_with_title_and_body()
    {
        // Real-world Gemini OAuth payload
        var step = Parse("""
            {
              "type": "text",
              "message": {
                "type": "note",
                "title": "Gemini CLI OAuth",
                "message": "You are running in a remote/VPS environment. Open the URL below..."
              }
            }
            """);
        var result = WizardPayloadHelpers.ExtractStepMessage(step);
        Assert.Equal(
            "Gemini CLI OAuth\n\nYou are running in a remote/VPS environment. Open the URL below...",
            result);
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_message()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","message":"hi"}}""");
        Assert.Equal("hi", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_unwraps_nested_note_with_only_title()
    {
        var step = Parse("""{"type":"text","message":{"type":"note","title":"Heads up"}}""");
        Assert.Equal("Heads up", WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_unknown_object_shape()
    {
        // Defensive: never render raw JSON in the UI. Empty is better than garbage.
        var step = Parse("""{"type":"text","message":{"some":"thing"}}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }

    [Fact]
    public void ExtractStepMessage_returns_empty_for_non_string_non_object()
    {
        var step = Parse("""{"type":"text","message":42}""");
        Assert.Equal(string.Empty, WizardPayloadHelpers.ExtractStepMessage(step));
    }
}
