using System.Text.Json;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Static helpers for sanitizing upstream openclaw wizard payloads. Kept free
/// of XAML/Windows.System dependencies so they can run in xUnit on any host.
/// </summary>
internal static class WizardPayloadHelpers
{
    /// <summary>
    /// Returns a validation error only when the gateway kept the client on the
    /// current step. This distinguishes rejected answers from terminal errors.
    /// </summary>
    public static string ExtractCurrentStepValidationError(JsonElement payload, string currentStepId)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(currentStepId)
            || !payload.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(error.GetString())
            || !payload.TryGetProperty("step", out var step)
            || step.ValueKind != JsonValueKind.Object
            || !step.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String
            || !string.Equals(id.GetString(), currentStepId, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return error.GetString()!;
    }

    /// <summary>
    /// Reads the <c>message</c> field of a wizard step. Upstream is supposed to
    /// send a string; the Gemini CLI OAuth plugin (and possibly others) nests a
    /// note object instead, e.g.
    /// <code>{"type":"text","message":{"type":"note","title":"...","message":"..."}}</code>
    /// Returns the string as-is, "Title\n\nMessage" for a nested object, or
    /// empty (safer than rendering raw JSON in the UI).
    /// </summary>
    public static string ExtractStepMessage(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!step.TryGetProperty("message", out var msg)) return string.Empty;

        return msg.ValueKind switch
        {
            JsonValueKind.String => msg.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Object => FlattenNestedNote(msg),
            _ => string.Empty,
        };
    }

    private static string FlattenNestedNote(JsonElement nested)
    {
        var title = nested.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? (t.GetString() ?? string.Empty)
            : string.Empty;
        var message = nested.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? (m.GetString() ?? string.Empty)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(message))
            return $"{title}\n\n{message}";
        if (!string.IsNullOrWhiteSpace(title))
            return title;
        if (!string.IsNullOrWhiteSpace(message))
            return message;
        return string.Empty;
    }
}
