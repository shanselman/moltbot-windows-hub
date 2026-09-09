using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.Helpers;
using Windows.UI;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// Authoritative production renderer for standalone tool calls and grouped tool activity.
/// </summary>
internal static class ToolCallCardRenderer
{
    private const int ToolDetailMaxChars = 4000;

    private static readonly ConditionalWeakTable<Expander, ActivityExpanderBinding>
        s_activityBindings = new();

    public static Element BuildStandalone(
        ChatTimelinePresentationContext props,
        ChatTimelineItem entry,
        bool isNested = false)
    {
        var details = new List<Element>();
        var displayArgs = entry.ToolArgs is { Count: > 0 }
            ? FormatToolDisplayArgs(entry.ToolArgs)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(displayArgs))
        {
            details.Add(BuildDetailSection(
                LocalizedOrDefault("Chat_Tool_InputSection", "Tool input"),
                displayArgs));
        }
        else if (!string.IsNullOrWhiteSpace(entry.Text))
        {
            details.Add(BuildDetailSection(
                LocalizedOrDefault("Chat_Tool_InputSection", "Tool input"),
                entry.Text));
        }

        if (!string.IsNullOrWhiteSpace(entry.ToolOutput))
        {
            details.Add(BuildDetailSection(
                LocalizedOrDefault(
                    entry.ToolResult == ChatToolCallStatus.Error
                        ? "Chat_Tool_ErrorLabel"
                        : "Chat_Tool_OutputLabel",
                    entry.ToolResult == ChatToolCallStatus.Error
                        ? "Tool error"
                        : "Tool output"),
                entry.ToolOutput));
        }

        var toolName = string.IsNullOrWhiteSpace(entry.ToolName)
            ? LocalizedOrDefault("Chat_Tool_FooterLabel", "Tool")
            : entry.ToolName;
        var statusLabel = StatusLabel(entry.ToolResult);
        var expander = Expander(
                $"{toolName} · {statusLabel}",
                Border(VStack(6, details.ToArray()))
                    .Padding(18, 8, 18, 10))
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId($"ChatToolCall_{SanitizeAutomationId(entry.Id)}")
            .Set(control =>
            {
                if (isNested)
                {
                    control.FontSize = 12;
                    control.MinHeight = 28;
                    control.Padding = new Thickness(4, 0, 4, 0);
                }
            })
            .AutomationName(
                $"{LocalizedOrDefault("Chat_Tool_CallLabel", "Tool call")} {toolName}. {statusLabel}.")
            .WithKey($"tool-expander:{entry.Id}:collapse:{props.ToolCallsCollapseVersion}");

        return Border(expander)
            .Margin(isNested ? 0 : 68, isNested ? 0 : 4, isNested ? 0 : 40, isNested ? 0 : 4)
            .Padding(isNested ? 0 : 12, isNested ? 0 : 8, isNested ? 0 : 12, isNested ? 0 : 8)
            .BorderThickness(isNested ? 0 : 1)
            .CornerRadius(isNested ? 4 : 12)
            .Background(BrushFor(
                isNested
                    ? "SubtleFillColorTransparentBrush"
                    : "CardBackgroundFillColorDefaultBrush",
                isNested
                    ? Color.FromArgb(0, 0, 0, 0)
                    : Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                isNested
                    ? "SubtleFillColorTransparentBrush"
                    : "ControlStrokeColorDefaultBrush",
                isNested
                    ? Color.FromArgb(0, 0, 0, 0)
                    : Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    }

    private static Element BuildDetailSection(string label, string content)
    {
        var body = RichTextBlock(content)
            .Set(text =>
            {
                text.TextWrapping = TextWrapping.Wrap;
                text.FontSize = 12;
                text.FontWeight = FontWeights.Normal;
                text.Foreground = BrushFor("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Black);
                text.FontFamily = new FontFamily("Cascadia Code, Consolas");
                text.IsTextSelectionEnabled = true;
            });

        return VStack(
            4,
            Text(label, 11, FontWeights.SemiBold, "TextFillColorSecondaryBrush"),
            ScrollViewer(body)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                })
                .MaxHeight(240));
    }

    private static string FormatToolDisplayArgs(System.Text.Json.Nodes.JsonObject args)
    {
        var lines = new List<string>();
        foreach (var key in NativeToolProjector.DisplayArgumentKeys)
        {
            if (args[key] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                lines.Add($"{key}: {text}");
            }
        }

        var display = string.Join('\n', lines);
        return display.Length <= ToolDetailMaxChars
            ? display
            : display[..ToolDetailMaxChars] + "\n\u2026(truncated)";
    }

    public static Element BuildActivity(
        ChatTimelinePresentationContext props,
        ChatToolActivityRow activity,
        ChatToolActivityExpansionState expansionState) =>
        Component<ToolActivityCard, ToolActivityCardProps>(
            new ToolActivityCardProps(props, activity, expansionState));

    internal static Element BuildActivityCore(
        ChatTimelinePresentationContext props,
        ChatToolActivityRow activity,
        bool isExpanded,
        Action<bool> onUserExpansionChanged)
    {
        var summary = activity.Summary
            ?? throw new ArgumentException("An activity row requires a summary.", nameof(activity));
        var summaryText = FormatSummary(summary);
        string AutomationName(bool expanded) => string.Format(
            LocalizedOrDefault(
                "Chat_Activity_AutomationFormat",
                "Activity: {0}. {1} tools. {2}."),
            summaryText,
            summary.ToolCount,
            expanded
                ? LocalizedOrDefault("Chat_Activity_Expanded", "Expanded")
                : LocalizedOrDefault("Chat_Activity_Collapsed", "Collapsed"));
        var collapsedAutomationName = AutomationName(expanded: false);
        var expandedAutomationName = AutomationName(expanded: true);
        Element details = isExpanded
            ? VStack(
                0,
                activity.Tools
                    .Select(tool => BuildStandalone(props, tool, isNested: true)
                        .WithKey($"activity-tool:{tool.Id}"))
                    .ToArray())
            : Empty();

        var expander = Expander(
                summaryText,
                details)
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .AutomationId($"ChatToolActivity_{SanitizeAutomationId(activity.Tools[0].Id)}")
            .Set(control =>
            {
                ApplyActivityExpander(
                    control,
                    onUserExpansionChanged,
                    isExpanded,
                    collapsedAutomationName,
                    expandedAutomationName);
            })
            .AutomationName(isExpanded ? expandedAutomationName : collapsedAutomationName)
            .WithKey(activity.Key);

        return Border(expander)
            .Margin(68, 4, 40, 4)
            .Padding(8, 4)
            .Background(BrushFor(
                "CardBackgroundFillColorDefaultBrush",
                Color.FromArgb(0x24, 0x80, 0x80, 0x80)))
            .BorderBrush(BrushFor(
                "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x40, 0x80, 0x80, 0x80)))
            .BorderThickness(1)
            .CornerRadius(12);
    }

    internal static string FormatSummary(ChatToolActivitySummary summary)
        => ChatToolActivityFormatter.Format(summary, CreateFormatTemplates());

    private static ChatToolActivityFormatTemplates CreateFormatTemplates() => new(
        CommandOne: LocalizedOrDefault("Chat_Activity_CommandOne", "Ran {0} command"),
        CommandMany: LocalizedOrDefault("Chat_Activity_CommandMany", "Ran {0} commands"),
        ReadOne: LocalizedOrDefault("Chat_Activity_ReadOne", "read {0} file"),
        ReadMany: LocalizedOrDefault("Chat_Activity_ReadMany", "read {0} files"),
        EditOne: LocalizedOrDefault("Chat_Activity_EditOne", "edited {0} file"),
        EditMany: LocalizedOrDefault("Chat_Activity_EditMany", "edited {0} files"),
        WriteOne: LocalizedOrDefault("Chat_Activity_WriteOne", "wrote {0} file"),
        WriteMany: LocalizedOrDefault("Chat_Activity_WriteMany", "wrote {0} files"),
        SearchOne: LocalizedOrDefault("Chat_Activity_SearchOne", "ran {0} search"),
        SearchMany: LocalizedOrDefault("Chat_Activity_SearchMany", "ran {0} searches"),
        FetchOne: LocalizedOrDefault("Chat_Activity_FetchOne", "fetched {0} page"),
        FetchMany: LocalizedOrDefault("Chat_Activity_FetchMany", "fetched {0} pages"),
        GenericOne: LocalizedOrDefault("Chat_Activity_GenericOne", "used {0} tool"),
        GenericMany: LocalizedOrDefault("Chat_Activity_GenericMany", "used {0} tools"),
        GenericNamed: LocalizedOrDefault("Chat_Activity_GenericNamed", "used {1}"),
        GenericNamedRepeated: LocalizedOrDefault(
            "Chat_Activity_GenericNamedRepeated",
            "used {1} {0} times"),
        Running: LocalizedOrDefault("Chat_Activity_RunningFormat", "Running {0}"),
        ToolFallback: LocalizedOrDefault("Chat_Tool_FooterLabel", "Tool"));

    private static void ApplyActivityExpander(
        Expander control,
        Action<bool> onUserExpansionChanged,
        bool isExpanded,
        string collapsedAutomationName,
        string expandedAutomationName)
    {
        if (!s_activityBindings.TryGetValue(control, out var binding))
        {
            binding = new ActivityExpanderBinding();
            control.Expanding += (_, _) =>
            {
                if (!binding.IsApplying)
                {
                    binding.OnUserExpansionChanged?.Invoke(true);
                    AutomationProperties.SetName(control, binding.ExpandedAutomationName);
                }
            };
            control.Collapsed += (_, _) =>
            {
                if (!binding.IsApplying)
                {
                    binding.OnUserExpansionChanged?.Invoke(false);
                    AutomationProperties.SetName(control, binding.CollapsedAutomationName);
                }
            };
            s_activityBindings.Add(control, binding);
        }

        binding.OnUserExpansionChanged = onUserExpansionChanged;
        binding.CollapsedAutomationName = collapsedAutomationName;
        binding.ExpandedAutomationName = expandedAutomationName;
        binding.IsApplying = true;
        try
        {
            control.IsExpanded = isExpanded;
        }
        finally
        {
            binding.IsApplying = false;
        }
    }

    private static string StatusLabel(ChatToolCallStatus? status) => status switch
    {
        null or ChatToolCallStatus.InProgress => LocalizedOrDefault("Chat_Status_Running", "Running"),
        ChatToolCallStatus.Error => LocalizedOrDefault("Chat_Status_Error", "Error"),
        ChatToolCallStatus.Interrupted => LocalizedOrDefault("Chat_Status_Interrupted", "Interrupted"),
        _ => LocalizedOrDefault("Chat_Status_Done", "Done"),
    };

    private static string SanitizeAutomationId(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static TextBlockElement Text(
        string text,
        double fontSize,
        global::Windows.UI.Text.FontWeight weight,
        string foregroundResource) =>
        TextBlock(text)
            .TextWrapping(TextWrapping.Wrap)
            .FontSize(fontSize)
            .FontWeight(weight)
            .Foreground(BrushFor(foregroundResource, Microsoft.UI.Colors.Black));

    private static string LocalizedOrDefault(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static Brush BrushFor(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true
            && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private sealed class ActivityExpanderBinding
    {
        public Action<bool>? OnUserExpansionChanged { get; set; }
        public bool IsApplying { get; set; }
        public string CollapsedAutomationName { get; set; } = string.Empty;
        public string ExpandedAutomationName { get; set; } = string.Empty;
    }
}

internal sealed record ToolActivityCardProps(
    ChatTimelinePresentationContext Timeline,
    ChatToolActivityRow Activity,
    ChatToolActivityExpansionState ExpansionState);

/// <summary>
/// Owns activity disclosure rerenders so nested tool controls exist only while expanded.
/// </summary>
internal sealed class ToolActivityCard : Component<ToolActivityCardProps>
{
    public override Element Render()
    {
        var (_, setRenderVersion) = UseState(0, threadSafe: true);
        var renderVersion = UseRef(0);
        var summary = Props.Activity.Summary
            ?? throw new ArgumentException("An activity row requires a summary.", nameof(Props));
        var isExpanded = Props.ExpansionState.IsExpanded(
            Props.Activity.Key,
            summary,
            Props.Timeline.ToolCallsCollapseVersion);

        void OnUserExpansionChanged(bool expanded)
        {
            Props.ExpansionState.SetExplicit(
                Props.Activity.Key,
                expanded,
                Props.Timeline.ToolCallsCollapseVersion);
            var nextRenderVersion = renderVersion.Current + 1;
            renderVersion.Current = nextRenderVersion;
            setRenderVersion(nextRenderVersion);
        }

        return ToolCallCardRenderer.BuildActivityCore(
            Props.Timeline,
            Props.Activity,
            isExpanded,
            OnUserExpansionChanged);
    }
}
