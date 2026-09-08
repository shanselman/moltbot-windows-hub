using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

/// <summary>
/// View-only props for the composer. <see cref="Session"/> carries the host-mount
/// <see cref="ChatComposerViewModel"/> and <see cref="ChatComposerController"/>;
/// <see cref="OnSendRequested"/> lets the root bump its own #1089 scroll-follow
/// token before a send is attempted, exactly as the pre-D2 root did inline.
/// </summary>
internal sealed record ReactorChatComposerViewProps(
    ChatComposerSession Session,
    ChatComposerInputs Inputs,
    ChatDataSnapshot InputSnapshot,
    Action OnSendRequested,
    bool IsCompact);

/// <summary>
/// Declarative Reactor view for the composer. It owns control construction, popup/
/// control references, caret and focus application, keyboard forwarding, automation
/// properties, and theme/high-contrast resource application. It holds no draft/send/
/// voice/slash workflow state, calls no provider API directly, and performs no
/// lifecycle parsing or attachment security decisions: all of that lives in
/// <see cref="ChatComposerViewModel"/> and <see cref="ChatComposerController"/>,
/// which it reads and calls through <see cref="ReactorChatComposerViewProps.Session"/>.
/// </summary>
internal sealed class ReactorChatComposer : Component<ReactorChatComposerViewProps>
{
    private static readonly string[] ThinkingLevels = ["off", "minimal", "low", "medium", "high"];

    public override Element Render()
    {
        var props = Props;
        var vm = props.Session.ViewModel;
        var controller = props.Session.Controller;
        var inputs = props.Inputs;
        var colorScheme = UseColorScheme();

        // The Reactor view subscribes to the view model exactly once per mount and
        // unsubscribes on unmount. This render-invalidation counter is an adapter
        // detail: it is not a second copy of composer state, only a re-render token.
        var (renderRevision, setRenderRevision) = UseState(vm.RenderRevision, threadSafe: true);
        var inputControl = UseRef<TextBox?>(null);
        var slashPopup = UseRef<Microsoft.UI.Xaml.Controls.Primitives.Popup?>(null);
        var slashPopupContentRef = UseRef<(string Key, FrameworkElement? Content)>((string.Empty, null));
        var controllerRef = UseRef(controller);
        controllerRef.Current = controller;
        var pasteHandler = UseRef<TextControlPasteEventHandler>(async (_, args) =>
        {
            if (GetBitmapClipboardContent() is not { } clipboardContent)
                return;

            // Paste is a synchronous routed event. Suppress the default text paste
            // before awaiting bitmap extraction so a multi-format clipboard cannot
            // insert text alongside the image attachment.
            args.Handled = true;
            await controllerRef.Current.PasteImageAsync(clipboardContent);
        });

        UseEffect((Func<Action>)(() =>
        {
            void OnChanged(object? sender, PropertyChangedEventArgs args) => setRenderRevision(vm.RenderRevision);
            vm.PropertyChanged += OnChanged;
            if (renderRevision != vm.RenderRevision)
                setRenderRevision(vm.RenderRevision);
            return () =>
            {
                vm.PropertyChanged -= OnChanged;
                CloseSlashPopup(slashPopup);
            };
        }), Array.Empty<object>());

        UseEffect((Func<Action>)(() =>
        {
            props.Session.ApplyInputs(inputs);
            return static () => { };
        }), props.InputSnapshot, inputs.CurrentThread);

        var text = vm.Draft;
        var isSending = vm.IsSending;
        var isRecording = vm.IsRecording;
        var slashDisplay = vm.SlashDisplay;

        void FocusAndPlaceCaretAtEnd()
        {
            inputControl.Current?.DispatcherQueue?.TryEnqueue(() =>
            {
                if (inputControl.Current is not { } textBox)
                    return;

                textBox.Focus(FocusState.Programmatic);
                var caret = textBox.Text?.Length ?? 0;
                textBox.SelectionStart = caret;
                textBox.SelectionLength = 0;
            });
        }

        void CommitSlash(string value, ReactorSlashMenuState nextState)
        {
            vm.CommitSlashText(value, nextState);
            FocusAndPlaceCaretAtEnd();
        }

        UseEffect((Func<Action>)(() =>
        {
            if (vm.ShouldRequestCatalogOnOpen())
                controller.RequestCommandCatalog();
            return static () => { };
        }), slashDisplay.ShouldRequestCatalog);
        UseEffect((Func<Action>)(() =>
        {
            vm.ReconcileAfterCatalogRefresh();
            return static () => { };
        }), inputs.AvailableCommands);

        void Send()
        {
            if (!vm.CanSend)
                return;

            props.OnSendRequested();
            _ = controller.SendAsync();
        }

        var modelChoices = inputs.ModelChoices is { Count: > 0 }
            ? inputs.ModelChoices
            : inputs.AvailableModels
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => new ChatModelChoice(model, model))
                .ToArray();
        var selectableModels = modelChoices.Where(model => model.IsSelectable).ToArray();
        var defaultReasoningLabel = Localized("Chat_Composer_Reasoning_Default", "Default");
        var modelNames = new[] { defaultReasoningLabel }
            .Concat(selectableModels.Select(ChatModelLabels.BuildMenuLabel))
            .ToArray();
        var modelIndex = string.IsNullOrWhiteSpace(inputs.CurrentThread.Model)
            ? 0
            : Math.Max(0, Array.FindIndex(
                selectableModels,
                model => model.MatchesModel(inputs.CurrentThread.Model, inputs.CurrentThread.ModelProvider)) + 1);
        var thinkingIndex = string.IsNullOrWhiteSpace(inputs.CurrentThread.ThinkingLevel)
            ? 0
            : Math.Max(0, Array.IndexOf(ThinkingLevels, inputs.CurrentThread.ThinkingLevel) + 1);
        var thinkingNames = new[] { defaultReasoningLabel }
            .Concat(ThinkingLevels)
            .ToArray();
        var actionLabel = inputs.TurnActive
            ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
            : Localized("Chat_Composer_Tooltip_Send", "Send");
        var controlCornerRadius = new CornerRadius(4);

        Element IconButton(
            string glyph,
            string automationName,
            Action onClick,
            bool enabled = true,
            string? automationId = null)
        {
            return Button(
                    TextBlock(glyph).Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                            textBlock,
                            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    }),
                    onClick)
                .AutomationName(automationName)
                .Foreground(Theme.SecondaryText)
                .Resources(resources => resources
                    .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    button.BorderThickness = new Thickness(0);
                    if (!string.IsNullOrWhiteSpace(automationId))
                    {
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                            button,
                            automationId);
                    }
                    ComposerAutomationVisibility.Prepare(button);
                    ToolTipService.SetToolTip(button, automationName);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        Element PickerButton(
            string label,
            string automationName,
            string automationId,
            bool enabled,
            double maxLabelWidth)
        {
            return Button(
                    HStack(
                        4,
                        TextBlock(label).Set(textBlock =>
                        {
                            textBlock.FontSize = 13;
                            textBlock.MaxWidth = maxLabelWidth;
                            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                            textBlock.TextWrapping = TextWrapping.NoWrap;
                        }),
                        TextBlock("\uE70D").Set(textBlock =>
                        {
                            textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                            textBlock.FontSize = 10;
                            textBlock.Margin = new Thickness(2, 4, 0, 0);
                            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                                textBlock,
                                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                        })),
                    () => { })
                .AutomationName(automationName)
                .Foreground(Theme.SecondaryText)
                .Resources(resources => resources
                    .Set("ButtonBackground", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBackgroundPointerOver", Theme.SubtleFill)
                    .Set("ButtonBackgroundPressed", Theme.Ref("SubtleFillColorTertiaryBrush"))
                    .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPointerOver", Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Set("ButtonBorderBrushPressed", Theme.Ref("SubtleFillColorTransparentBrush")))
                .Set(button =>
                {
                    button.Height = 32;
                    button.MinHeight = 32;
                    button.MinWidth = 0;
                    button.Padding = new Thickness(8, 0, 8, 0);
                    button.CornerRadius = controlCornerRadius;
                    button.IsEnabled = enabled;
                    button.BorderThickness = new Thickness(0);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        button,
                        automationId);
                    ComposerAutomationVisibility.Prepare(button);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));
        }

        var attachmentRows = vm.PendingAttachments
            .Select(attachment =>
                (Element)HStack(
                    6,
                    TextBlock(attachment.FileName).FontSize(12),
                    Button("×", () => controller.RemoveAttachment(attachment))
                        .SubtleButton()
                        .AutomationName("Remove attachment")))
            .ToArray();
        var audioLevel = Math.Clamp(vm.VoiceAudioLevel, 0f, 1f);
        var voiceFeedbackText = string.IsNullOrWhiteSpace(vm.VoiceTranscript)
            ? Localized("Chat_Voice_ListeningPrompt", "Listening…")
            : vm.VoiceTranscript;
        var waveformBars = Enumerable.Range(0, 8)
            .Select(index =>
                (Element)Border(Empty())
                    .Width(2)
                    .Height(2 + (audioLevel * (index % 3 == 1 ? 10 : 7)))
                    .CornerRadius(1)
                    .VAlign(VerticalAlignment.Center)
                    .Background(Theme.SecondaryText))
            .ToArray();
        Element voiceFeedback = !isRecording
            ? Empty()
            : Border(
                    HStack(
                        6,
                        Border(Empty())
                            .Width(6)
                            .Height(6)
                            .CornerRadius(3)
                            .Background(Theme.SecondaryText),
                        TextBlock(voiceFeedbackText)
                            .FontSize(11)
                            .Foreground(Theme.SecondaryText),
                        HStack(1, waveformBars)))
                .Padding(8, 4)
                .HAlign(HorizontalAlignment.Left);
        var queuedRows = inputs.QueuedMessages
            .Select((message, index) =>
            {
                var failed = message.SendState == ChatQueuedMessageSendState.Failed;
                var actionKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailed"
                    : "Chat_Composer_QueuedMessageCancel";
                var actionAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageRemoveFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageCancelAutomationFormat";
                var rowAutomationKey = failed
                    ? "Chat_Composer_QueuedMessageFailedAutomationFormat"
                    : "Chat_Composer_QueuedMessageAutomationFormat";
                var action = message.SendState == ChatQueuedMessageSendState.Sending
                    ? Empty()
                    : Button(Localized(actionKey, failed ? "Remove failed message" : "Cancel"),
                            () => controller.CancelQueuedMessage(message.Id))
                        .SubtleButton()
                        .AutomationId($"{(failed ? "ChatQueuedMessageRemoveFailed" : "ChatQueuedMessageCancel")}_{message.Id}")
                        .AutomationName(string.Format(
                            CultureInfo.CurrentCulture,
                            Localized(actionAutomationKey, "{0}: {1}"),
                            index + 1,
                            message.Text));
                var state = failed
                    ? (Element)TextBlock(Localized("Chat_Composer_QueuedMessageFailed", "Failed"))
                        .FontSize(12)
                    : Empty();
                var error = failed && !string.IsNullOrWhiteSpace(message.ErrorText)
                    ? (Element)TextBlock(message.ErrorText!).FontSize(12)
                    : Empty();
                return (Element)HStack(
                        6,
                        VStack(
                                4,
                                state,
                                TextBlock(message.Text).FontSize(12).MaxWidth(260),
                                error)
                            .HAlign(HorizontalAlignment.Left),
                        action)
                    .AutomationName(string.Format(
                        CultureInfo.CurrentCulture,
                        Localized(rowAutomationKey, "{0}"),
                        message.Text));
            })
            .ToArray();
        var queuedCountText = string.Format(
            CultureInfo.CurrentCulture,
            Localized("Chat_Composer_QueuedCountFormat", "{0} queued messages"),
            queuedRows.Length);
        Element queuedPanel = queuedRows.Length == 0
            ? Empty()
            : Border(
                    VStack(
                        8,
                        TextBlock(queuedCountText)
                            .FontSize(13)
                            .Set(textBlock => textBlock.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold),
                        ScrollView(VStack(4, queuedRows))
                            .MaxHeight(props.IsCompact ? 144 : 220)
                            .Set(scrollView =>
                            {
                                scrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                                scrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                                scrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
                                scrollView.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                            })))
                .Set(border => Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
                    border,
                    Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite))
                .AutomationName(queuedCountText);

        var slashPopupVisible = slashDisplay.IsVisible
            && (slashDisplay.IsLoading
                || (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is not null)
                || slashDisplay.Commands.Count > 0);
        var popupCatalogKey = inputs.AvailableCommands is null
            ? "missing"
            : RuntimeHelpers.GetHashCode(inputs.AvailableCommands).ToString(CultureInfo.InvariantCulture);
        var popupArgumentCommandKey = slashDisplay.ArgCommand?.Name
            ?? slashDisplay.ArgCommand?.DisplayName()
            ?? string.Empty;
        var popupStateKey = string.Join(
            "|",
            slashPopupVisible,
            slashDisplay.IsLoading,
            slashDisplay.IsArgsMode,
            popupArgumentCommandKey,
            slashDisplay.Query,
            slashDisplay.SelectedIndex,
            slashDisplay.SelectableCount,
            popupCatalogKey,
            colorScheme);
        FrameworkElement? slashPopupContent;
        if (!slashPopupVisible)
        {
            slashPopupContentRef.Current = (string.Empty, null);
            slashPopupContent = null;
        }
        else if (slashPopupContentRef.Current.Key == popupStateKey)
        {
            slashPopupContent = slashPopupContentRef.Current.Content;
        }
        else if (slashDisplay.IsLoading)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashHintPopup(
                Localized("Chat_Composer_Slash_Loading", "Loading commands...")));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else if (slashDisplay.IsArgsMode && slashDisplay.ArgCommand is { } argCommand)
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashArgPopup(
                argCommand,
                slashDisplay.ArgChoices,
                slashDisplay.SelectedIndex,
                choice => CommitSlash(
                    argCommand.BuildArgInsertionText(choice.Value),
                    ReactorSlashMenuState.Closed)));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }
        else
        {
            slashPopupContent = CreateSlashPopupHost(BuildSlashPopup(
                slashDisplay.Groups,
                slashDisplay.SelectedIndex,
                slashDisplay.Query,
                colorScheme,
                command =>
                {
                    CommitSlash(
                        command.FirstArgChoices().Count > 0 ? command.DisplayName() + " " : command.BuildInsertionText(),
                        command.FirstArgChoices().Count > 0
                            ? new ReactorSlashMenuState(true, string.Empty, 0, true)
                            : ReactorSlashMenuState.Closed);
                }));
            slashPopupContentRef.Current = (popupStateKey, slashPopupContent);
        }

        var input = TextBox(
                text,
                vm.SetDraft,
                PlaceholderFor(inputs.ConnectionState))
            .AutomationId("ChatComposerInput")
            .AutomationName(PlaceholderFor(inputs.ConnectionState))
            .OnKeyDown((sender, args) =>
            {
                if (slashDisplay.IsVisible)
                {
                    switch (args.Key)
                    {
                        case global::Windows.System.VirtualKey.Down when slashDisplay.HasSelection:
                            args.Handled = true;
                            vm.MoveSlashSelection(1);
                            return;

                        case global::Windows.System.VirtualKey.Up when slashDisplay.HasSelection:
                            args.Handled = true;
                            vm.MoveSlashSelection(-1);
                            return;

                        case global::Windows.System.VirtualKey.Enter:
                        case global::Windows.System.VirtualKey.Tab:
                            if (slashDisplay.HasSelection)
                            {
                                args.Handled = true;
                                var commit = vm.CommitSelectedSlashItem();
                                if (commit.Accepted)
                                    FocusAndPlaceCaretAtEnd();
                                return;
                            }

                            if (slashDisplay.IsLoading)
                            {
                                args.Handled = true;
                                if (args.Key == global::Windows.System.VirtualKey.Tab)
                                    vm.DismissSlashMenu();
                                return;
                            }
                             break;

                        case global::Windows.System.VirtualKey.Escape:
                            args.Handled = true;
                            vm.DismissSlashMenu();
                            return;
                    }

                    if (slashDisplay.IsLoading
                        && (args.Key == global::Windows.System.VirtualKey.Up
                            || args.Key == global::Windows.System.VirtualKey.Down))
                    {
                        args.Handled = true;
                        return;
                    }
                }

                if (args.Key != global::Windows.System.VirtualKey.Enter)
                    return;

                args.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    global::Windows.System.VirtualKey.Shift);
                if (shift.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)
                    && sender is Microsoft.UI.Xaml.Controls.TextBox textBox)
                {
                    var current = textBox.Text ?? string.Empty;
                    var start = Math.Clamp(textBox.SelectionStart, 0, current.Length);
                    var end = Math.Clamp(start + textBox.SelectionLength, start, current.Length);
                    vm.SetDraft(current[..start] + "\n" + current[end..]);
                    textBox.SelectionStart = start + 1;
                    textBox.SelectionLength = 0;
                    return;
                }

                Send();
            })
            .TextWrapping(TextWrapping.Wrap)
            .Set(control =>
            {
                inputControl.Current = control;
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                control.MinHeight = 56;
                control.MaxHeight = 200;
                control.FontSize = 14;
                control.Padding = new Thickness(8);
                control.IsEnabled = inputs.ConnectionState == "connected";
                control.AcceptsReturn = false;
                control.BorderThickness = new Thickness(0);
                control.BorderBrush = transparent;
                control.Background = transparent;
                control.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
                control.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
                control.Resources["TextControlBackground"] = transparent;
                control.Resources["TextControlBackgroundFocused"] = transparent;
                control.Resources["TextControlBackgroundPointerOver"] = transparent;
                control.Resources["TextControlBorderBrush"] = transparent;
                control.Resources["TextControlBorderBrushFocused"] = transparent;
                control.Resources["TextControlBorderBrushPointerOver"] = transparent;
                ComposerAutomationVisibility.Prepare(control);
            })
            .OnMount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste += pasteHandler.Current;
                textBox.ContextFlyout = CreateComposerContextFlyout(
                    textBox,
                    () => controllerRef.Current);
            })
            .OnUnmount(control =>
            {
                var textBox = (TextBox)control;
                textBox.Paste -= pasteHandler.Current;
                textBox.ContextFlyout = null;
                ComposerAutomationVisibility.Detach(textBox);
            });
        UseEffect((Func<Action>)(() =>
        {
            if (inputControl.Current is { } anchor)
                DriveSlashPopup(slashPopup, anchor, slashPopupContent, slashPopupVisible);
            else
                CloseSlashPopup(slashPopup);
            return static () => { };
        }), popupStateKey);

        var sessionPicker = MenuFlyout(
            PickerButton(
                inputs.CurrentThread.Title,
                $"{Localized("Chat_Composer_Accessibility_Session", "Session")}: {inputs.CurrentThread.Title}",
                "ChatComposerSessionPicker",
                !inputs.MessageOptionsDisabled && inputs.AvailableChannels.Count > 1,
                props.IsCompact ? 56 : 160),
            inputs.AvailableChannels
                .Select(thread => RadioMenuItem(
                    thread.Title,
                    "chat-sessions",
                    string.Equals(thread.Id, inputs.CurrentThread.Id, StringComparison.Ordinal),
                    () => controller.SelectChannel(thread.Id)))
                .ToArray());

        var modelPickerLabel = modelIndex == 0
            ? defaultReasoningLabel
            : selectableModels[modelIndex - 1].DisplayName;
        var modelPicker = MenuFlyout(
            PickerButton(
                modelPickerLabel,
                $"{Localized("Chat_Composer_Accessibility_Model", "Model")}: {modelPickerLabel}",
                "ChatComposerModelPicker",
                !inputs.MessageOptionsDisabled,
                props.IsCompact ? 68 : 180),
            modelNames
                .Select((modelName, index) => RadioMenuItem(
                    modelName,
                    "chat-models",
                    index == modelIndex,
                    () =>
                    {
                        if (index == 0)
                            controller.ClearModel();
                        else if (index <= selectableModels.Length)
                            controller.SetModel(selectableModels[index - 1].SelectionId);
                    }))
                .ToArray());

        var reasoningPicker = MenuFlyout(
            PickerButton(
                thinkingNames[thinkingIndex],
                $"{Localized("Chat_Composer_Accessibility_Reasoning", "Reasoning")}: {thinkingNames[thinkingIndex]}",
                "ChatComposerReasoningPicker",
                !inputs.MessageOptionsDisabled,
                props.IsCompact ? 54 : 96),
            thinkingNames
                .Select((level, index) => RadioMenuItem(
                    level,
                    "chat-thinking-level",
                    index == thinkingIndex,
                    () =>
                    {
                        if (index == 0)
                            controller.ClearThinkingLevel();
                        else
                            controller.SetThinkingLevel(ThinkingLevels[index - 1]);
                    }))
                .ToArray());

        var attachButton = IconButton(
            "\uE723",
            Localized("Chat_Composer_Tooltip_Attach", "Attach"),
            () => props.Session.HostActions.AttachmentPickerRequest?.Invoke(),
            props.Session.HostActions.AttachmentPickerRequest is not null,
            "ChatComposerAttach");
        var voiceButton = IconButton(
            isRecording
                ? "\uE15B"
                : "\uE720",
            isRecording
                ? Localized("Chat_Composer_Tooltip_Stop", "Stop")
                : Localized("Chat_Composer_Tooltip_Voice", "Voice"),
            () =>
            {
                if (isRecording)
                    controller.StopVoiceRecording();
                else
                    controller.StartVoiceRecording();
            },
            props.Session.HostActions.VoiceCaptureRequest is not null,
            "ChatComposerVoice");
        var speakerButton = IconButton(
            vm.IsSpeakerMuted ? "\uE74F" : "\uE767",
            vm.IsSpeakerMuted ? "Unmute" : "Mute",
            controller.ToggleSpeakerMuted,
            automationId: "ChatComposerSpeakerToggle");
        Element settingsButton = props.IsCompact || props.Session.HostActions.SettingsNavigation is null
            ? Empty()
            : IconButton(
                "\uE713",
                Localized("Chat_Composer_Tooltip_Settings", "Settings"),
                props.Session.HostActions.SettingsNavigation,
                automationId: "ChatComposerSettings");

        Element primaryAction = inputs.TurnActive
            ? IconButton(
                "\uE71A",
                actionLabel,
                controller.Stop,
                automationId: "ChatComposerPrimaryAction")
            : Button(
                    TextBlock("\uE724").Set(textBlock =>
                    {
                        textBlock.FontFamily = FluentIconCatalog.SymbolThemeFontFamily;
                        textBlock.FontSize = 16;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                            textBlock,
                            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    }),
                    Send)
                .AccentButton()
                .AutomationName(actionLabel)
                .Set(button =>
                {
                    button.Width = 32;
                    button.Height = 32;
                    button.MinWidth = 32;
                    button.MinHeight = 32;
                    button.Padding = new Thickness(0);
                    button.CornerRadius = controlCornerRadius;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        button,
                        "ChatComposerPrimaryAction");
                    button.IsEnabled = vm.CanSend;
                    ComposerAutomationVisibility.Prepare(button);
                    ToolTipService.SetToolTip(button, actionLabel);
                })
                .OnUnmount(control => ComposerAutomationVisibility.Detach(
                    (FrameworkElement)control));

        var leftToolbar = HStack(8, attachButton, sessionPicker, modelPicker, reasoningPicker)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);
        var rightToolbar = HStack(8, voiceButton, speakerButton, settingsButton, primaryAction)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center);
        var toolbar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            leftToolbar.Grid(row: 0, column: 0),
            rightToolbar.Grid(row: 0, column: 1));

        var composerChildren = new List<Element>();
        if (isRecording)
            composerChildren.Add(voiceFeedback);
        if (attachmentRows.Length > 0)
            composerChildren.Add(VStack(4, attachmentRows));
        if (queuedRows.Length > 0)
            composerChildren.Add(queuedPanel);
        composerChildren.Add(input);
        composerChildren.Add(toolbar);

        return Border(
            VStack(8, composerChildren.ToArray())
            .Padding(8, 2, 8, 8))
            .BorderThickness(1)
            .CornerRadius(8)
            .Margin(12)
            .Background(Theme.ControlFill)
            .BorderBrush(Theme.ControlStroke)
            .HAlign(HorizontalAlignment.Stretch);
    }

    private static void CloseSlashPopup(Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef)
    {
        if (popupRef.Current is not { } popup)
            return;

        popup.IsOpen = false;
        if (popup.Child is ReactorHostControl host)
            host.Dispose();
        popup.Child = null;
        popup.PlacementTarget = null;
    }

    private static ReactorHostControl CreateSlashPopupHost(Element content)
    {
        var host = new ReactorHostControl();
        host.Mount(_ => content);
        return host;
    }

    private static void DriveSlashPopup(
        Ref<Microsoft.UI.Xaml.Controls.Primitives.Popup?> popupRef,
        TextBox anchor,
        FrameworkElement? content,
        bool visible)
    {
        var popup = popupRef.Current;
        if (popup is null)
        {
            popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup
            {
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true,
            };
            popupRef.Current = popup;
        }

        if (!visible || content is null || anchor.XamlRoot is null)
        {
            CloseSlashPopup(popupRef);
            return;
        }

        content.Width = Math.Max(280, anchor.ActualWidth > 0 ? anchor.ActualWidth : 360);
        popup.XamlRoot = anchor.XamlRoot;
        popup.PlacementTarget = anchor;
        popup.DesiredPlacement = Microsoft.UI.Xaml.Controls.Primitives.PopupPlacementMode.Top;
        if (popup.Child is ReactorHostControl previousHost
            && !ReferenceEquals(previousHost, content))
            previousHost.Dispose();
        popup.Child = content;
        popup.IsOpen = true;
    }

    private static Element BuildSlashHintPopup(string text)
    {
        return SlashShell(
            TextBlock(text)
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .Margin(8, 6, 8, 6));
    }

    private static Element BuildSlashPopup(
        IReadOnlyList<CommandCategoryGroup> groups,
        int selectedIndex,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var rows = new List<Element>();
        var index = 0;
        foreach (var group in groups)
        {
            rows.Add(SlashCategoryHeader(CommandCategories.Label(group.Category)));
            foreach (var command in group.Commands)
            {
                rows.Add(SlashRow(command, index == selectedIndex, query, colorScheme, onPick));
                index++;
            }
        }

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashCategoryHeader(string text)
    {
        return TextBlock((text ?? string.Empty).ToUpperInvariant())
            .FontSize(11)
            .SemiBold()
            .CharacterSpacing(60)
            .Foreground(Theme.TertiaryText)
            .Margin(8, 8, 8, 2);
    }

    private static Element BuildSlashArgPopup(
        GatewayCommand command,
        IReadOnlyList<GatewayCommandArgChoice> choices,
        int selectedIndex,
        Action<GatewayCommandArgChoice> onPick)
    {
        var argDescription = command.Args?.FirstOrDefault()?.Description;
        var headerText = !string.IsNullOrWhiteSpace(argDescription)
            ? $"{command.DisplayName()}  {argDescription}"
            : !string.IsNullOrWhiteSpace(command.Description)
                ? $"{command.DisplayName()}  {command.Description}"
                : command.DisplayName();
        var rows = new List<Element>
        {
            TextBlock(headerText)
                .FontSize(11)
                .SemiBold()
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .Foreground(Theme.TertiaryText)
                .Margin(8, 6, 8, 2),
        };
        for (var index = 0; index < choices.Count; index++)
            rows.Add(SlashArgRow(command, choices[index], index == selectedIndex, onPick));

        return SlashShell(
            ScrollView(VStack(0, rows.ToArray()))
                .MaxHeight(280)
                .Set(scrollViewer =>
                {
                    scrollViewer.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
                    scrollViewer.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
                }));
    }

    private static Element SlashArgRow(
        GatewayCommand command,
        GatewayCommandArgChoice choice,
        bool selected,
        Action<GatewayCommandArgChoice> onPick)
    {
        var label = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label;
        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                HStack(
                    8,
                    TextBlock(label)
                        .FontSize(13)
                        .SemiBold()
                        .VAlign(VerticalAlignment.Center)
                        .Foreground(Theme.PrimaryText),
                    TextBlock($"{command.DisplayName()} {choice.Value}")
                        .FontSize(12)
                        .VAlign(VerticalAlignment.Center)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .MaxLines(1)
                        .Foreground(Theme.SecondaryText)),
                () => onPick(choice))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Choose {label} for {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.BorderThickness = new Thickness(0);
            })
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashShell(Element child)
    {
        return Border(child)
            .Padding(4)
            .CornerRadius(8)
            .Background(Theme.Ref("AcrylicBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.Ref("SurfaceStrokeColorFlyoutBrush"), 1)
            .Translation(0, 0, 32)
            .Set(border => border.Shadow = new ThemeShadow());
    }

    private static Element SlashRow(
        GatewayCommand command,
        bool selected,
        string query,
        ColorScheme colorScheme,
        Action<GatewayCommand> onPick)
    {
        var cells = new List<Element>
        {
            TextBlock(SlashGlyph(command))
                .FontFamily(FluentIconCatalog.SymbolThemeFontFamily)
                .FontSize(14)
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.SecondaryText)
                .AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)
                .Grid(row: 0, column: 0),
            TextBlock(command.DisplayName())
                .FontSize(13)
                .SemiBold()
                .VAlign(VerticalAlignment.Center)
                .Foreground(Theme.PrimaryText)
                .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                .Grid(row: 0, column: 1),
        };
        var args = command.ArgTemplate();
        if (!string.IsNullOrWhiteSpace(args))
        {
            cells.Add(
                TextBlock(args)
                    .FontSize(12)
                    .FontFamily("Consolas")
                    .VAlign(VerticalAlignment.Center)
                    .Foreground(Theme.SecondaryText)
                    .Grid(row: 0, column: 2));
        }

        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            cells.Add(
                TextBlock(command.Description!)
                    .FontSize(12)
                    .VAlign(VerticalAlignment.Center)
                    .HAlign(HorizontalAlignment.Right)
                    .TextAlignment(TextAlignment.Right)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1)
                    .Foreground(Theme.SecondaryText)
                    .Set(textBlock => ApplyQueryHighlight(textBlock, query, colorScheme))
                    .Grid(row: 0, column: 3));
        }

        var options = command.OptionCount();
        if (options > 0)
        {
            cells.Add(SlashBadge($"{options} options").Grid(row: 0, column: 4));
        }

        var background = selected ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush");
        return Button(
                Grid(
                    [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    cells.ToArray())
                    .Set(grid => grid.ColumnSpacing = 8)
                    .VAlign(VerticalAlignment.Center),
                () => onPick(command))
            .Padding(8, 7, 8, 7)
            .HAlign(HorizontalAlignment.Stretch)
            .CornerRadius(6)
            .AutomationName($"Insert {command.DisplayName()}")
            .Resources(resources => resources
                .Set("ButtonBackground", background)
                .Set("ButtonBorderBrush", Theme.Ref("SubtleFillColorTransparentBrush")))
            .Set(button =>
            {
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.BorderThickness = new Thickness(0);
            })
            .OnMount(element =>
            {
                if (selected)
                    element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
    }

    private static Element SlashBadge(string text)
    {
        return Border(
                TextBlock(text)
                    .FontSize(10)
                    .SemiBold()
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")))
            .Padding(6, 1, 6, 1)
            .CornerRadius(4)
            .VAlign(VerticalAlignment.Center)
            .Background(Theme.AccentSecondary);
    }

    private static void ApplyQueryHighlight(TextBlock textBlock, string? query, ColorScheme colorScheme)
    {
        textBlock.TextHighlighters.Clear();
        var text = textBlock.Text ?? string.Empty;
        var normalized = (query ?? string.Empty).Trim().TrimStart('/').Trim();
        if (normalized.Length == 0 || text.Length < normalized.Length || colorScheme == ColorScheme.HighContrast)
            return;

        var isDark = colorScheme == ColorScheme.Dark;
        if (ThemeRef.Resolve("AccentFillColorDefaultBrush", isDark) is not SolidColorBrush accent
            || ThemeRef.Resolve("TextFillColorPrimaryBrush", isDark) is not Brush foreground)
            return;

        var accentColor = accent.Color;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(31, accentColor.R, accentColor.G, accentColor.B)),
            Foreground = foreground,
        };

        for (var index = 0; index <= text.Length - normalized.Length;)
        {
            var found = text.IndexOf(normalized, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                break;
            highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange
            {
                StartIndex = found,
                Length = normalized.Length,
            });
            index = found + normalized.Length;
        }

        if (highlighter.Ranges.Count > 0)
            textBlock.TextHighlighters.Add(highlighter);
    }

    private static string SlashGlyph(GatewayCommand command)
    {
        var name = (command.NativeName ?? command.DisplayName()).Trim().TrimStart('/').ToLowerInvariant()
            .Replace(':', '_')
            .Replace('.', '_')
            .Replace('-', '_');
        return name switch
        {
            "help" or "commands" => "\uE82D",
            "status" or "usage" => "\uE9D9",
            "export" or "export_session" => "\uE896",
            "skill" or "fast" => "\uE945",
            "model" or "models" or "think" => "\uE713",
            "new" => "\uE710",
            "reset" or "redirect" => "\uE72C",
            "compact" => "\uE9F3",
            "stop" => "\uE71A",
            "clear" => "\uE74D",
            "agents" => "\uE7F4",
            "subagents" => "\uE8B7",
            "steer" => "\uE724",
            "tts" => "\uE767",
            _ => "\uE756",
        };
    }

    private static global::Windows.ApplicationModel.DataTransfer.DataPackageView? GetBitmapClipboardContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && content.Contains(
                    global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    ? content
                    : null;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return null;
        }
    }

    private static MenuFlyout CreateComposerContextFlyout(
        TextBox textBox,
        Func<ChatComposerController> getController)
    {
        var undoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Undo,
            textBox.Undo);
        var redoItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Redo,
            textBox.Redo);
        var cutItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Cut,
            textBox.CutSelectionToClipboard);
        var copyItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Copy,
            textBox.CopySelectionToClipboard);
        var pasteItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.Paste,
            () =>
            {
                if (GetBitmapClipboardContent() is { } clipboardContent)
                    _ = getController().PasteImageAsync(clipboardContent);
                else
                    PasteTextFromClipboard(textBox);
            });
        var selectAllItem = CreateStandardMenuItem(
            Microsoft.UI.Xaml.Input.StandardUICommandKind.SelectAll,
            textBox.SelectAll);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            pasteItem,
            "ChatComposerPasteMenuItem");

        var editSeparator = new MenuFlyoutSeparator();
        var selectAllSeparator = new MenuFlyoutSeparator();
        var menu = new MenuFlyout();
        menu.Items.Add(undoItem);
        menu.Items.Add(redoItem);
        menu.Items.Add(editSeparator);
        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(selectAllSeparator);
        menu.Items.Add(selectAllItem);
        menu.Opening += (_, _) =>
        {
            var state = ChatComposerContextMenuState.Project(
                textBox.CanUndo,
                textBox.CanRedo,
                textBox.SelectionLength > 0,
                ClipboardContainsPasteContent(),
                !string.IsNullOrEmpty(textBox.Text));
            undoItem.Visibility = ToVisibility(state.ShowUndo);
            redoItem.Visibility = ToVisibility(state.ShowRedo);
            cutItem.Visibility = ToVisibility(state.ShowCut);
            copyItem.Visibility = ToVisibility(state.ShowCopy);
            pasteItem.Visibility = ToVisibility(state.ShowPaste);
            selectAllItem.Visibility = ToVisibility(state.ShowSelectAll);
            editSeparator.Visibility = ToVisibility(state.ShowEditSeparator);
            selectAllSeparator.Visibility = ToVisibility(state.ShowSelectAllSeparator);
        };
        return menu;
    }

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static MenuFlyoutItem CreateStandardMenuItem(
        Microsoft.UI.Xaml.Input.StandardUICommandKind kind,
        Action execute)
    {
        var command = new Microsoft.UI.Xaml.Input.StandardUICommand(kind);
        command.CanExecuteRequested += (_, args) => args.CanExecute = true;
        command.ExecuteRequested += (_, _) => execute();
        return new MenuFlyoutItem
        {
            Command = command,
            Visibility = Visibility.Collapsed,
        };
    }

    private static bool ClipboardContainsPasteContent()
    {
        try
        {
            var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            return content is not null
                && (content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                    || content.Contains(
                        global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard access failed: {ex.Message}");
            return false;
        }
    }

    private static void PasteTextFromClipboard(TextBox textBox)
    {
        try
        {
            textBox.PasteFromClipboard();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OpenClawTray.Services.Logger.Debug(
                $"Reactor chat composer: clipboard text paste failed: {ex.Message}");
        }
    }

    private static string PlaceholderFor(string connectionState) => connectionState switch
    {
        "connected" => Localized("Chat_Composer_Placeholder_Connected", "Message Assistant (Enter to send)"),
        "connecting" => Localized("Chat_Composer_Placeholder_Connecting", "Connecting…"),
        "incompatible-gateway" => Localized(
            "Chat_Composer_Placeholder_IncompatibleGateway",
            "Gateway update required: incompatible version"),
        _ => Localized("Chat_Composer_Placeholder_NotConnected", "Not connected"),
    };

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

internal static class ComposerAutomationVisibility
{
    public static void Prepare(FrameworkElement control)
    {
        Detach(control);
        if (HasUsableLayout(control))
        {
            ApplyReadyState(control);
            return;
        }

        control.IsHitTestVisible = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        control.Loaded += OnLoaded;
        control.SizeChanged += OnSizeChanged;
    }

    public static void Detach(FrameworkElement control)
    {
        control.Loaded -= OnLoaded;
        control.SizeChanged -= OnSizeChanged;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        TryEnableHitTesting(sender);

    private static void TryEnableHitTesting(object sender)
    {
        if (sender is not FrameworkElement control || !HasUsableLayout(control))
            return;

        ApplyReadyState(control);
    }

    private static void ApplyReadyState(FrameworkElement control)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            control,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Control);
        control.IsHitTestVisible = true;
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
            .FromElement(control)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(control);
        peer?.RaisePropertyChangedEvent(
            Microsoft.UI.Xaml.Automation.AutomationElementIdentifiers.IsOffscreenProperty,
            true,
            false);
        Detach(control);
    }

    private static bool HasUsableLayout(FrameworkElement control) =>
        control.IsLoaded
        && control.Visibility == Visibility.Visible
        && control.ActualWidth > 0
        && control.ActualHeight > 0;
}
