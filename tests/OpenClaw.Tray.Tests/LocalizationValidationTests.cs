using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Validates that all localization resource files are consistent with en-us.
/// Catches missing/extra keys and broken format placeholders early — before translation PRs land.
/// </summary>
public class LocalizationValidationTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> LocalizableXamlAttributes = new(StringComparer.Ordinal)
    {
        "Content",
        "Description",
        "Header",
        "Message",
        "PlaceholderText",
        "Text",
        "Title",
    };

    private static readonly HashSet<string> InvariantOrDeferredResourceKeys = new(StringComparer.Ordinal)
    {
        "CanvasWindow_TextBlock_31.Text",
        "CanvasWindow_winexWindowEx_2.Title",
        "ChatWindow_winexWindowEx_2.Title",
        "HubWindow_winexWindowEx_2.Title",
        "TitleText.Text",
        "TokenPromptBox.Header",
        "TokenTextBox.Header",
        "TrayMenuWindow_winexWindowEx_2.Title",
        "Onboarding_Connection_QrButton",
        "Onboarding_Connection_Token",
        "WindowTitle_TrayMenu",
        "WindowTitle_Update",
        // VoiceOverlayWindow window-title key — matches the convention
        // for ChatWindow / HubWindow / CanvasWindow / TrayMenuWindow.
        "VoiceOverlayWindow_winexWindowEx_2.Title",
        // Brand name — identical across all locales.
        "ConnectionPage_TopologyTailscale",
        // Native engine executable/product name. Keep the exact llama-server
        // spelling in every locale so it matches diagnostics and process names.
        "LocalAiPage_EngineHeading.Text",
        // Product/feature name — "OpenClaw Onboard" is kept identical across
        // all locales (the card's description and button are translated).
        "SettingsPage_OnboardWizard_Header.Text",
        // Technical product term — "Gateway" is kept in English across
        // supported locales per onboarding terminology.
        "DiagnosticsPage_Section_Gateway.Text",
        // Sample IDs / brand identifiers — same across locales.
        "VoiceSettingsPage_ElevenLabsVoiceIdBox.PlaceholderText",
        "VoiceSettingsPage_ElevenLabsModelBox.PlaceholderText",
        "VoiceSettingsPage_MiniMaxModelBox.PlaceholderText",
        // Capability command identifier — should match the wire/API name.
        "NotificationsPage_MetadataSystemRun",
        // Punctuation-only layout format; localized parts are supplied by
        // separate placeholders and should keep the same visual separator.
        "ConfigPage_AccessSummaryFormat",
        // NodesPage detail labels — "Version", "Hardware", and "PATH" are
        // technical loanwords that read the same in every supported locale
        // in this app's audience. Translating them adds no clarity and
        // mixing scripts in a single detail row reads worse than keeping
        // the English label.
        "NodesPage_Label_Version",
        "NodesPage_Label_Hardware",
        "NodesPage_Label_PathEnv",
        // PermissionsPage runtime strings — declared in resw for localization
        // pipeline coverage but seeded English-only across all locales until
        // translations land. Fetched at runtime via LocalizationHelper.
        "PermissionsPage_FeaturesDescription_Enabled",
        "PermissionsPage_FeaturesDescription_Disabled",
        "PermissionsPage_Cap_Browser_Label",
        "PermissionsPage_Cap_Browser_Description",
        "PermissionsPage_Cap_Camera_Label",
        "PermissionsPage_Cap_Camera_Description",
        "PermissionsPage_Cap_Canvas_Label",
        "PermissionsPage_Cap_Canvas_Description",
        "PermissionsPage_Cap_Screen_Label",
        "PermissionsPage_Cap_Screen_Description",
        "PermissionsPage_Cap_Location_Label",
        "PermissionsPage_Cap_Location_Description",
        "PermissionsPage_Cap_Tts_Label",
        "PermissionsPage_Cap_Tts_Description",
        "PermissionsPage_Cap_Stt_Label",
        "PermissionsPage_Cap_Stt_Description",
        "PermissionsPage_Cap_Ollama_Label",
        "PermissionsPage_Cap_Ollama_Description",
        "PermissionsPage_Cap_SystemRun_Label",
        "PermissionsPage_Cap_SystemRun_Description",
        "PermissionsPage_NodeStatus_Disabled",
        // About / Gateway info strings added as part of the Windows-native
        // settings reorg are seeded in English across locales until the next
        // translation pass lands. They are still present in every locale file
        // so key parity stays strict; this set only documents deferred copy.
        "SettingsPage_About.Text",
        "SettingsPage_GatewayInfoExpander.Header",
        "SettingsPage_GatewayInfoLabel_Version.Text",
        "SettingsPage_GatewayInfoLabel_Protocol.Text",
        "SettingsPage_GatewayInfoLabel_AuthMode.Text",
        "SettingsPage_GatewayInfoLabel_Uptime.Text",
        "PermissionsPage_NodeStatus_DisabledDetails",
        "PermissionsPage_NodeStatus_Active",
        "PermissionsPage_NodeStatus_ActiveDetailsFormat",
        "PermissionsPage_NodeStatus_NoCapabilities",
        "PermissionsPage_NodeStatus_NotConnected",
        "PermissionsPage_NodeStatus_NotConnectedDetails",
        "PermissionsPage_McpStatus_TokenReady",
        "PermissionsPage_McpStatus_TokenPending",
        "PermissionsPage_McpStatus_TokenCopied",
        "PermissionsPage_McpStatus_TokenNotFound",
        "PermissionsPage_McpStatus_UrlCopied",
        "PermissionsPage_RulesCount_None",
        "PermissionsPage_RulesCount_One",
        "PermissionsPage_RulesCount_ManyFormat",
        "PermissionsPage_Allowlist_NoConfig",
        "PermissionsPage_Allowlist_NoCommands",
        "PermissionsPage_Allowlist_ParseFailed",
        "PermissionsPage_McpStatus_TokenReadFailedFormat",
        // Session key display formatter: "{agent} / {slot}" intentionally
        // uses an invariant separator while the surrounding notification copy
        // and common labels ("Main chat") are localized.
        "AppNotification_ExecApprovalPending_AgentSlotLabelFormat",
        // Chat runtime warning seeded English-only until translations land.
        "Chat_Composer_Placeholder_IncompatibleGateway",
        // InstancesPage / ConnectionPage new strings — seeded English across
        // all locales until translations land. Same precedent as the
        // PermissionsPage runtime keys above. The Manage expander body reuses
        // the existing NodesPage_* resource keys (Rename, Forget, Version,
        // Hardware, etc.) which already have full translations.
        "InstancesPage_Title.Text",
        "InstancesPage_Subtitle.Text",
        "InstancesPage_RefreshLabel.Text",
        "InstancesPage_ConnectionWarning.Title",
        "InstancesPage_ConnectionWarning.Message",
        "InstancesPage_EmptyTitle.Text",
        "InstancesPage_Status_Active",
        "InstancesPage_Status_Idle",
        "InstancesPage_Status_Inactive",
        "InstancesPage_Status_Disconnected",
        "InstancesPage_StatusTooltip_Active_Format",
        "InstancesPage_StatusTooltip_Idle_Format",
        "InstancesPage_StatusTooltip_Inactive_Format",
        "InstancesPage_StatusTooltip_Disconnected",
        "InstancesPage_Role_AccessibilityFormat",
        "InstanceManage_CopyNodeId_AccessibilityName",
        "InstancesPage_Reason_Self",
        "InstancesPage_Reason_Connect",
        "InstancesPage_Reason_Disconnect",
        "InstancesPage_Reason_NodeConnect",
        "InstancesPage_Reason_NodeDisconnect",
        "InstancesPage_Reason_Launch",
        "InstancesPage_Reason_Heartbeat",
        "InstancesPage_Reason_Refresh",
        "InstancesPage_Reason_Resync",
        "InstancesPage_ContextMenu_CopyDebug",
        "InstancesPage_CapabilitiesCount_Format",
        "InstancesPage_CommandsCount_Format",
        "InstancesPage_Presence_AccessibilityFormat",
        "InstancesPage_UpdateReason_Tooltip_Format",
        "InstancesPage_UpdateReason_Tooltip_NoReason",
        "ConnectionPage_NodePairing_Title.Text",
        "ConnectionPage_NodePairing_Subtitle.Text",
        "ConnectionPage_CopyTrustApproval.Content",
        "ConnectionPage_NodeApprovalRequired",
        "ConnectionPage_NodeReapprovalRequired",
        "ConnectionPage_NodeBodyApprovalRequired",
        "ConnectionPage_NodeBodyReapprovalRequired",
        "ConnectionPage_NodeTrustApprovalHelp",
        "ConnectionPage_NodeTrustDiscoveryHelp",
        "ConnectionPage_NodeReconnectAfterApproval",
        "ConnectionPage_NodeSurfaceNone",
        "ConnectionPage_NodeEffectiveCapabilities",
        "ConnectionPage_NodeEffectiveCommands",
        "ConnectionPage_NodeEffectivePermissions",
        "ConnectionPage_NodePendingDeclaredCapabilities",
        "ConnectionPage_NodePendingDeclaredCommands",
        "ConnectionPage_NodePendingDeclaredPermissions",
        // Node-card capability pills and technical-details disclosure — seeded
        // English-only across all 5 locales using the deferred-translation pattern.
        "ConnectionPage_NodeTechnicalDetails.Text",
        "ConnectionPage_NodePillState_Active",
        "ConnectionPage_NodePillState_Pending",
        "ConnectionPage_NodePillState_NeedsGatewayToken",
        "ConnectionPage_NodePillState_Off",
        "ConnectionPage_NodeCap_Device",
        "ConnectionPage_NodeCap_System",
        "ConnectionStatusWindow.Title",
        // Hard-coded XAML strings resolved by issue #491 — seeded English-only across
        // all 5 locales using the deferred-translation pattern. Translations are a
        // follow-up tracked separately. Same precedent as the PermissionsPage and
        // InstancesPage runtime keys above.
        "ConnectionPage_GatewayURL.PlaceholderText",
        "ConnectionPage_SSHHost.PlaceholderText",
        "ConnectionPage_SSHUser.PlaceholderText",
        "ConnectionStatusWindow_SSHHost.PlaceholderText",
        "ConnectionStatusWindow_SSHUser.PlaceholderText",
        "ConnectionStatusWindow_WsLocalhost18790.PlaceholderText",
        "ConnectionStatusWindow_WsLocalhost18790.Text",
        // ConfigPage runtime reconnect dialog strings — seeded English-only
        // across all locales until translations land. Fetched at runtime via
        // LocalizationHelper and follows the deferred-translation pattern used
        // by other recently added runtime strings above.
        "ConfigPage_ReconnectDialogAccepted",
        "ConfigPage_ReconnectDialogBody",
        "ConfigPage_ReconnectDialogTitle",
        "ConfigPage_ReconnectDialogWaiting",
        "CronPage_AmericaChicago.Content",
        "CronPage_AmericaDenver.Content",
        "CronPage_AmericaLosAngeles.Content",
        "CronPage_AmericaNewYork.Content",
        "CronPage_AsiaTokyo.Content",
        "CronPage_EuropeBerlin.Content",
        "CronPage_EuropeLondon.Content",
        "CronPage_UTC.Content",
        "SandboxPage_16MiB.Content",
        "SandboxPage_1MiB.Content",
        "SandboxPage_64MiB.Content",
        "SandboxPage_SystemRun.Text",
        // SessionsPage runtime accessibility strings — seeded English-only across
        // all 5 locales using the deferred-translation pattern. These are
        // tooltip / AutomationProperties.Name overrides on the OpenChat button.
        // Same precedent as the PermissionsPage / InstancesPage / ConfigPage
        // runtime keys above.
        "SessionsPage_OpenChatButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip",
        "SessionsPage_OpenChatButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        // CronPage / InfoBar / HubWindow nav / CommandCenter runtime strings —
        // seeded English-only across all 5 locales using the deferred-translation
        // pattern. Fetched at runtime via LocalizationHelper. Same precedent as
        // the PermissionsPage / InstancesPage / SessionsPage runtime keys above.
        "CronPage_JobCompleted",
        "CronPage_JobCompletedRanSuccessfully",
        "BindingsPage_CouldNotLoadBindings",
        "ConfigPage_CheckingConfigPermissions",
        "ConfigPage_ConfigUnavailable",
        "ConfigPage_ConfigIsReadOnly",
        // ConnectionPage gateway terminal controls — surfaced after PR #597
        // landed in master. Seeded English-only across all 5 locales using the
        // same deferred-translation pattern as the AgentEventsPage / SkillsPage
        // / CronPage entries above. The Description_Format key takes the WSL
        // distro name as {0} and is formatted in ConnectionPage.xaml.cs.
        "ConnectionPage_GatewayHostControlsDescription_Format",
        // GatewayHostAccess plan strings (terminal label / tooltip / disabled
        // reasons). Resolved in the classifier via LocalizationHelper so the
        // OpenTerminal button and any consumers of DisabledReason show
        // localized text.
        "GatewayHostAccess_OpenTerminalLabel",
        "GatewayHostAccess_OpenSshTerminalLabel",
        "GatewayHostAccess_OpenTerminalInWslTooltip_Format",
        "GatewayHostAccess_OpenSshTerminalTooltip_Format",
        "GatewayHostAccess_NoTerminalAccess",
        "GatewayHostAccess_NoWslOrSshDisabled",
        "Command_GoToConnection_Title",
        "Command_GoToConnection_Subtitle",
        "Command_GoToChat_Title",
        "Command_GoToChat_Subtitle",
        "Command_GoToSessions_Title",
        "Command_GoToSessions_Subtitle",
        "Command_GoToAgentEvents_Title",
        "Command_GoToAgentEvents_Subtitle",
        "Command_GoToSkills_Title",
        "Command_GoToSkills_Subtitle",
        "Command_GoToCron_Title",
        "Command_GoToCron_Subtitle",
        "Command_GoToWorkspace_Title",
        "Command_GoToWorkspace_Subtitle",
        "Command_GoToChannels_Title",
        "Command_GoToChannels_Subtitle",
        "Command_GoToInstances_Title",
        "Command_GoToInstances_Subtitle",
        "Command_GoToConfig_Title",
        "Command_GoToConfig_Subtitle",
        "Command_GoToUsage_Title",
        "Command_GoToUsage_Subtitle",
        "Command_GoToBindings_Title",
        "Command_GoToBindings_Subtitle",
        "Command_GoToPermissions_Title",
        "Command_GoToPermissions_Subtitle",
        "Command_GoToSettings_Title",
        "Command_GoToSettings_Subtitle",
        "Command_GoToDiagnostics_Title",
        "Command_GoToDiagnostics_Subtitle",
        "Command_OpenChatWindow_Title",
        "Command_OpenChatWindow_Subtitle",
        "Command_OpenDashboard_Title",
        "Command_OpenDashboard_Subtitle",
        "Command_ToggleNodeMode_Title",
        "Command_ToggleCamera_Title",
        "Command_ToggleCanvas_Title",
        "Command_ToggleScreenCapture_Title",
        "Command_ToggleBrowserControl_Title",
        "Command_Subtitle_CurrentlyOn",
        "Command_Subtitle_CurrentlyOff",
        "CommandCenter_AuthFailed",
        "CommandCenter_NodePendingApproval",
        "CommandCenter_GatewayConnectionError",
        "CommandCenter_GatewayNotConnected",
        "CommandCenter_GatewayHealthStale",
        "CommandCenter_NoChannelsReported",
        "CommandCenter_WaitingForGatewayHealth",
        "CommandCenter_NoChannelsRunning",
        "CommandCenter_NoNodesReported",
        "CommandCenter_UsageCostsMissing",
        "CommandCenter_BrowserProxyAuthMayNeed",
        "CommandCenter_SshTunnelPortNotListening",
        "CommandCenter_NoLocalGatewayListener",
        "CommandCenter_BrowserProxySshForwardNotListening",
        "CommandCenter_BrowserProxyHostNotDetected",
        // WorkspacePage session file rail strings — seeded English across all
        // locales until translations land. Same deferred-translation precedent
        // as the InstancesPage / ConnectionPage runtime keys above.
        "WorkspacePage_SearchBox.PlaceholderText",
        "WorkspacePage_ParentFolderButton.Content",
        "WorkspacePage_OpenConnectionSettings.Content",
        "WorkspacePage_NoSearchResults.Text",
        "WorkspacePage_NoFolderResults",
        "WorkspacePage_LoadErrorMessage",
        "WorkspacePage_UnsupportedMessage",
        "WorkspacePage_Badge_Missing",
        "WorkspacePage_Badge_Edited",
        "WorkspacePage_Badge_Read",
        "WorkspacePage_FolderNote",
        "WorkspacePage_FileUnavailable",
        "WorkspacePage_BrowserOnlyFileNote",
        "WorkspacePage_OpenFolder",
        "WorkspacePage_CopyPath",
        "WorkspacePage_CopyPathFailed",
        "WorkspacePage_FileType_File",
        "WorkspacePage_FileType_Folder",
        "WorkspacePage_RootFolder",
        "WorkspacePage_SearchResultsPath",
        "WorkspacePage_BrowserTruncated",
        // "GB" is an internationally recognized SI byte-size abbreviation kept identical
        // across every supported locale (matches how NodesPage_Label_* technical loanwords
        // above are handled); only the surrounding sentence is translated per locale.
        "LocalAi_Reason_GigabytesFormat",
    };

    private static readonly HashSet<string> EnUsOnlyFallbackResourceKeys = new(StringComparer.Ordinal);

    private static readonly string[] RequiredRuntimeOnboardingKeys =
    [
        "Onboarding_Ready_Node_ScreenCapture",
        "Onboarding_Ready_Node_ScreenCapture_Sub",
        "Onboarding_Ready_Node_Camera",
        "Onboarding_Ready_Node_Camera_Sub",
        "Onboarding_Ready_Node_SystemCmd",
        "Onboarding_Ready_Node_SystemCmd_Sub",
        "Onboarding_Ready_Node_Canvas",
        "Onboarding_Ready_Node_Canvas_Sub",
        "Onboarding_Ready_Node_Notify",
        "Onboarding_Ready_Node_Notify_Sub",
    ];

    private static readonly string[] RequiredLocalizedAccessibilityKeys =
    [
        "SandboxPage_UnavailablePrimaryButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_PresetLockedButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_PresetBalancedButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_PresetPermissiveButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_AllowWindowsUi.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_DocsAccessCombo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_DownloadsAccessCombo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SandboxPage_DesktopAccessCombo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "SettingsPage_NotificationSoundComboBox.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "VoiceSettingsPage_LanguageCombo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
        "HubWindow_MainNavigation.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
    ];

    private static readonly IReadOnlyDictionary<char, byte> Windows1252Bytes = BuildWindows1252ByteMap();

    private static readonly IReadOnlyDictionary<char, byte> CodePage437Bytes = BuildCodePage437ByteMap();

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static string GetStringsDirectory() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings");

    private static Dictionary<string, string> LoadResw(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Descendants("data")
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Element("value")?.Value ?? string.Empty);
    }

    private static Dictionary<char, byte> BuildWindows1252ByteMap()
    {
        var map = Enumerable.Range(0, 256)
            .ToDictionary(i => (char)i, i => (byte)i);

        map['€'] = 0x80;
        map['‚'] = 0x82;
        map['ƒ'] = 0x83;
        map['„'] = 0x84;
        map['…'] = 0x85;
        map['†'] = 0x86;
        map['‡'] = 0x87;
        map['ˆ'] = 0x88;
        map['‰'] = 0x89;
        map['Š'] = 0x8A;
        map['‹'] = 0x8B;
        map['Œ'] = 0x8C;
        map['Ž'] = 0x8E;
        map['‘'] = 0x91;
        map['’'] = 0x92;
        map['“'] = 0x93;
        map['”'] = 0x94;
        map['•'] = 0x95;
        map['–'] = 0x96;
        map['—'] = 0x97;
        map['˜'] = 0x98;
        map['™'] = 0x99;
        map['š'] = 0x9A;
        map['›'] = 0x9B;
        map['œ'] = 0x9C;
        map['ž'] = 0x9E;
        map['Ÿ'] = 0x9F;

        return map;
    }

    private static Dictionary<char, byte> BuildCodePage437ByteMap()
    {
        var map = Enumerable.Range(0, 128)
            .ToDictionary(i => (char)i, i => (byte)i);

        const string high =
            "ÇüéâäàåçêëèïîìÄÅ" +
            "ÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
            "áíóúñÑªº¿⌐¬½¼¡«»" +
            "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
            "└┴┬├─┼╞╟╚╔╩╦╠═╬╧" +
            "╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
            "αßΓπΣσµτΦΘΩδ∞φε∩" +
            "≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

        for (var i = 0; i < high.Length; i++)
            map[high[i]] = (byte)(i + 128);

        return map;
    }

    private static bool TryDecodeKnownMojibake(string value, out string decoded)
    {
        return TryDecodeMojibake(value, Windows1252Bytes, out decoded)
            || TryDecodeMojibake(value, CodePage437Bytes, out decoded);
    }

    private static bool TryDecodeMojibake(
        string value,
        IReadOnlyDictionary<char, byte> byteMap,
        out string decoded)
    {
        decoded = string.Empty;

        for (var start = 0; start < value.Length; start++)
        {
            if (!byteMap.ContainsKey(value[start]))
                continue;

            var runEnd = start;
            while (runEnd < value.Length && byteMap.ContainsKey(value[runEnd]))
                runEnd++;

            for (var length = runEnd - start; length >= 2; length--)
            {
                for (var offset = start; offset <= runEnd - length; offset++)
                {
                    var candidate = value.Substring(offset, length);
                    if (TryDecodeMojibakeRun(candidate, byteMap, out decoded))
                        return true;
                }
            }

            start = runEnd;
        }

        return false;
    }

    private static bool TryDecodeMojibakeRun(
        string value,
        IReadOnlyDictionary<char, byte> byteMap,
        out string decoded)
    {
        decoded = string.Empty;

        if (!value.Any(c => c > 0x7F))
            return false;

        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
            bytes[i] = byteMap[value[i]];

        try
        {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return !string.Equals(decoded, value, StringComparison.Ordinal) && IsPlausibleDecodedMojibake(decoded);
    }

    private static bool IsPlausibleDecodedMojibake(string decoded) =>
        decoded.Any(char.IsLetter) &&
        decoded.All(c =>
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category != UnicodeCategory.Control &&
                   category != UnicodeCategory.Format &&
                   category != UnicodeCategory.NonSpacingMark &&
                   category != UnicodeCategory.SpacingCombiningMark &&
                   category != UnicodeCategory.EnclosingMark;
        });

    private static bool IsNonLocalizableXamlValue(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        !Regex.IsMatch(value, @"\p{L}", RegexOptions.CultureInvariant) ||
        value.StartsWith("{Binding", StringComparison.Ordinal) ||
        value.StartsWith("{x:Bind", StringComparison.Ordinal) ||
        value.StartsWith("{StaticResource", StringComparison.Ordinal) ||
        value.StartsWith("{ThemeResource", StringComparison.Ordinal) ||
        value.StartsWith("{TemplateBinding", StringComparison.Ordinal) ||
        value.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetNonEnglishLocaleDirectories(string stringsDir) =>
        Directory.GetDirectories(stringsDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "en-us", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    private static bool IsInvariantValue(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Regex.IsMatch(value, @"^\d+(\.\d+)?[Kk]?$", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(value, @"^(v\d|[A-Z0-9._%+-]+://|~?/|\.NET|WinUI|WinAppSDK|OpenClaw$|GitHub|MCP|JSON|API|HTTP|HTTPS|SSH|TLS|WebView2|OAuth|QR$|Cron$|main$|user$|machine-name$)", RegexOptions.CultureInvariant) ||
        value.Contains("openclaw://", StringComparison.Ordinal) ||
        value.Contains("github.com", StringComparison.Ordinal) ||
        value.Contains("openclaw.ai", StringComparison.Ordinal) ||
        value.Contains("localhost", StringComparison.Ordinal) ||
        value.Contains("ws://", StringComparison.Ordinal) ||
        value.Contains("wss://", StringComparison.Ordinal) ||
        value.Contains("http://", StringComparison.Ordinal) ||
        value.Contains("https://", StringComparison.Ordinal) ||
        value.Contains("~/", StringComparison.Ordinal);

    [Fact]
    public void MojibakeDetector_FindsWindows1252Substrings()
    {
        Assert.True(TryDecodeKnownMojibake("连接状态 è¿ž 👍", out var decoded));
        Assert.Contains("连", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void MojibakeDetector_FindsCodePage437Substrings()
    {
        Assert.True(TryDecodeKnownMojibake("SSH ΘÜºΘüô", out var decoded));
        Assert.Contains("隧道", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void MojibakeDetector_AllowsLegitimateUnicode()
    {
        Assert.False(TryDecodeKnownMojibake("连接状态 Café 👍", out _));
    }

    /// <summary>
    /// Keys whose value is a Latin-script loanword (e.g. "OK") that reads
    /// natively in English/French/Dutch but should still be translated for
    /// non-Latin scripts (zh-CN, zh-TW). For these keys the test permits
    /// fr-fr and nl-nl to be identical to en-us while zh-cn and zh-tw differ —
    /// the "all-or-nothing" rule does not apply.
    /// </summary>
    private static readonly HashSet<string> LatinScriptInvariantResourceKeys = new(StringComparer.Ordinal)
    {
        "Update_OK",
        "Onboarding_IncompleteSetup_Close",
        "ChatPage_OK",
        "ConnectionPage_ViaSSH",
    };

    // Locales whose translations are allowed to remain identical to en-us
    // for keys in LatinScriptInvariantResourceKeys (e.g. "OK"). The check in
    // Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales requires the
    // set of locales sharing the en-us value to *exactly* equal this set.
    //
    // Pitfall: adding a new Latin-script locale (say de-de) that also uses
    // "OK" verbatim will break that test unless de-de is added here too. If
    // you add such a locale, update this set; if you add a non-Latin-script
    // locale, do nothing.
    private static readonly HashSet<string> LatinScriptLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        "fr-fr",
        "nl-nl",
        "pt-br",
    };

    private static bool IsInvariantOrDeferred(string key, string value) =>
        InvariantOrDeferredResourceKeys.Contains(key)
        || IsInvariantValue(value)
        || key.StartsWith("ChannelsPage_", StringComparison.Ordinal)
        || key.StartsWith("DiagnosticsPage_", StringComparison.Ordinal)
        || key.StartsWith("SettingsRow_", StringComparison.Ordinal)
        // Title-bar status pill + notifications bell flyout strings. Seeded
        // English-only across all five .resw files using the deferred-translation
        // pattern; translations land in a follow-up.
        || key.StartsWith("HubWindow_StatusFlyout_", StringComparison.Ordinal)
        || key.StartsWith("HubWindow_StatusPill_", StringComparison.Ordinal)
        || key.StartsWith("HubWindow_Pill_", StringComparison.Ordinal)
        || key.StartsWith("HubWindow_Role_", StringComparison.Ordinal)
        || key.StartsWith("HubWindow_Bell_", StringComparison.Ordinal)
        || key.StartsWith("NotificationsFlyout_", StringComparison.Ordinal)
        // V2 onboarding redesign strings (V2_*) are intentionally English-only at first
        // ship. They live in V2Strings.DefaultEnUs and the cutover seeded them into all
        // five .resw files with English values. Translations land in a follow-up.
        || key.StartsWith("V2_", StringComparison.Ordinal);

    [Fact]
    public void AllLocales_HaveExactlySameKeysAsEnUs()
    {
        var stringsDir = GetStringsDirectory();
        var referencePath = Path.Combine(stringsDir, "en-us", "Resources.resw");
        Assert.True(File.Exists(referencePath), $"Reference file not found: {referencePath}");

        var referenceKeys = LoadResw(referencePath).Keys
            .Except(EnUsOnlyFallbackResourceKeys)
            .ToHashSet(StringComparer.Ordinal);

        var localeDirs = GetNonEnglishLocaleDirectories(stringsDir);

        Assert.NotEmpty(localeDirs);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            Assert.True(File.Exists(reswPath), $"Expected Resources.resw for locale '{locale}'.");

            var localeKeys = LoadResw(reswPath).Keys.ToHashSet(StringComparer.Ordinal);

            var missing = referenceKeys.Except(localeKeys).OrderBy(k => k).ToList();
            var extra = localeKeys
                .Except(referenceKeys)
                .Except(EnUsOnlyFallbackResourceKeys)
                .OrderBy(k => k)
                .ToList();

            Assert.True(missing.Count == 0,
                $"Locale '{locale}' is missing {missing.Count} key(s): {string.Join(", ", missing.Take(10))}");
            Assert.True(extra.Count == 0,
                $"Locale '{locale}' has {extra.Count} unexpected key(s): {string.Join(", ", extra.Take(10))}");
        }
    }

    [Fact]
    public void AccessibilityNames_ExistInEveryLocale()
    {
        foreach (var localeDir in Directory.GetDirectories(GetStringsDirectory()).OrderBy(p => p, StringComparer.Ordinal))
        {
            var locale = Path.GetFileName(localeDir);
            var resources = LoadResw(Path.Combine(localeDir, "Resources.resw"));
            var missing = RequiredLocalizedAccessibilityKeys
                .Where(key => !resources.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Locale '{locale}' is missing localized accessibility names: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void XamlControlsWithXUid_HaveMatchingEnUsResources()
    {
        var winUiRoot = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var resourceKeys = LoadResw(Path.Combine(GetStringsDirectory(), "en-us", "Resources.resw"))
            .Keys
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var xamlPath in Directory.EnumerateFiles(winUiRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(IsSourceXaml)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(TestRepositoryPaths.GetRepositoryRoot(), xamlPath);
            var doc = XDocument.Load(xamlPath, LoadOptions.SetLineInfo);

            foreach (var element in doc.Descendants())
            {
                var uid = element.Attribute(XamlNamespace + "Uid")?.Value;
                if (string.IsNullOrWhiteSpace(uid))
                    continue;

                foreach (var attribute in element.Attributes())
                {
                    var attributeName = attribute.Name.LocalName;
                    if (!LocalizableXamlAttributes.Contains(attributeName) ||
                        IsNonLocalizableXamlValue(attribute.Value))
                    {
                        continue;
                    }

                    var key = $"{uid}.{attributeName}";
                    if (!resourceKeys.Contains(key))
                    {
                        var line = element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                            ? lineInfo.LineNumber
                            : 0;
                        missing.Add($"{relativePath}:{line} missing {key}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0,
            "Every localizable XAML attribute on an x:Uid element must have an en-us Resources.resw key. Missing: " +
            string.Join("; ", missing.Take(50)));
    }

    private static bool IsSourceXaml(string path)
    {
        var relative = Path.GetRelativePath(TestRepositoryPaths.GetRepositoryRoot(), path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Contains("bin", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllLocales_PreserveFormatPlaceholders()
    {
        var stringsDir = GetStringsDirectory();
        var referenceResw = LoadResw(Path.Combine(stringsDir, "en-us", "Resources.resw"));

        var keysWithPlaceholders = referenceResw
            .Where(kv => Regex.IsMatch(kv.Value, @"\{\d+\}"))
            .ToList();

        if (keysWithPlaceholders.Count == 0)
            return;

        var localeDirs = GetNonEnglishLocaleDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var localeResw = LoadResw(reswPath);

            foreach (var (key, enValue) in keysWithPlaceholders)
            {
                if (!localeResw.TryGetValue(key, out var localeValue))
                    continue;

                var enPlaceholders = Regex.Matches(enValue, @"\{\d+\}")
                    .Select(m => m.Value).OrderBy(p => p).ToList();
                var localePlaceholders = Regex.Matches(localeValue, @"\{\d+\}")
                    .Select(m => m.Value).OrderBy(p => p).ToList();

                Assert.True(enPlaceholders.SequenceEqual(localePlaceholders),
                    $"Locale '{locale}', key '{key}': expected placeholders " +
                    $"[{string.Join(", ", enPlaceholders)}] but found " +
                    $"[{string.Join(", ", localePlaceholders)}]");
            }
        }
    }

    [Fact]
    public void AllSupportedLocaleDirectories_Exist()
    {
        var stringsDir = GetStringsDirectory();
        string[] expected = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br"];

        foreach (var locale in expected)
        {
            var dir = Path.Combine(stringsDir, locale);
            Assert.True(Directory.Exists(dir), $"Locale directory missing: {locale}");
            Assert.True(File.Exists(Path.Combine(dir, "Resources.resw")),
                $"Resources.resw missing for locale: {locale}");
        }
    }

    [Fact]
    public void LanguageOverrideWhitelist_MatchesSupportedLocaleDirectories()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        var whitelist = Regex.Match(
            appSource,
            @"string\[\]\s+allowedLocales\s*=\s*\[(?<locales>[^\]]+)\]",
            RegexOptions.CultureInvariant);

        Assert.True(whitelist.Success, "App language override whitelist was not found.");

        var allowedLocales = Regex.Matches(whitelist.Groups["locales"].Value, @"""(?<locale>[^""]+)""")
            .Select(match => match.Groups["locale"].Value)
            .OrderBy(locale => locale, StringComparer.OrdinalIgnoreCase);
        var resourceLocales = Directory.GetDirectories(GetStringsDirectory())
            .Select(Path.GetFileName)
            .Where(locale => locale is not null)
            .Cast<string>()
            .OrderBy(locale => locale, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(resourceLocales, allowedLocales);
    }

    [Fact]
    public void AllLocales_ContainOnboardingKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var keys = LoadResw(reswPath).Keys;
            var onboardingKeys = keys.Where(k => k.StartsWith("Onboarding_")).ToList();

            Assert.True(onboardingKeys.Count > 0,
                $"Locale '{locale}' has no Onboarding_* keys");
        }
    }

    [Fact]
    public void AllLocales_ContainRuntimeOnboardingKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var keys = LoadResw(reswPath).Keys.ToHashSet(StringComparer.Ordinal);
            var missing = RequiredRuntimeOnboardingKeys
                .Where(key => !keys.Contains(key))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Locale '{locale}' is missing runtime onboarding key(s): {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void NoLocale_HasDuplicateKeys()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var doc = System.Xml.Linq.XDocument.Load(reswPath);
            var names = doc.Descendants("data")
                .Select(e => e.Attribute("name")!.Value)
                .ToList();

            var duplicates = names.GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0,
                $"Locale '{locale}' has duplicate keys: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void NoLocale_HasWindows1252MojibakeValues()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);
        var offenders = new List<string>();

        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            foreach (var (key, value) in LoadResw(reswPath))
            {
                if (TryDecodeKnownMojibake(value, out var decoded))
                    offenders.Add($"{locale}::{key} decodes to '{decoded}'");
            }
        }

        Assert.True(offenders.Count == 0,
            $"Found {offenders.Count} Windows-1252 mojibake resource value(s): " +
            string.Join("; ", offenders.Take(20)));
    }

    [Fact]
    public void AllLocales_HaveSameKeyCount()
    {
        var stringsDir = GetStringsDirectory();
        var referencePath = Path.Combine(stringsDir, "en-us", "Resources.resw");
        var referenceResw = LoadResw(referencePath);
        var referenceCount = referenceResw.Count - EnUsOnlyFallbackResourceKeys.Count(referenceResw.ContainsKey);

        var localeDirs = Directory.GetDirectories(stringsDir);
        foreach (var localeDir in localeDirs)
        {
            var locale = Path.GetFileName(localeDir);
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;

            var localeResw = LoadResw(reswPath);
            var count = localeResw.Count - EnUsOnlyFallbackResourceKeys.Count(localeResw.ContainsKey);
            Assert.Equal(referenceCount, count);
        }
    }

    /// <summary>
    /// Catches empty-value drift: an intentionally-cleared translation would surface in
    /// the UI either as a blank string OR (with LocalizationHelper.GetString's fallback)
    /// as the raw resource key. Either is a shipping bug; this test makes it a build
    /// failure instead.
    /// </summary>
    [Fact]
    public void NoLocale_HasEmptyOrWhitespaceValues()
    {
        var stringsDir = GetStringsDirectory();
        var localeDirs = Directory.GetDirectories(stringsDir);
        var empties = new List<string>();
        foreach (var localeDir in localeDirs)
        {
            var reswPath = Path.Combine(localeDir, "Resources.resw");
            if (!File.Exists(reswPath)) continue;
            var locale = Path.GetFileName(localeDir);
            foreach (var (key, value) in LoadResw(reswPath))
            {
                if (string.IsNullOrWhiteSpace(value))
                    empties.Add($"{locale}::{key}");
            }
        }
        Assert.True(empties.Count == 0,
            $"Found {empties.Count} resw entries with empty/whitespace values: {string.Join(", ", empties)}");
    }

    [Fact]
    public void Resources_AreTranslatedAllOrNoneAcrossNonEnglishLocales()
    {
        var stringsDir = GetStringsDirectory();
        var referenceResw = LoadResw(Path.Combine(stringsDir, "en-us", "Resources.resw"));
        var localeResw = GetNonEnglishLocaleDirectories(stringsDir)
            .Select(d => (Locale: Path.GetFileName(d), Resources: LoadResw(Path.Combine(d, "Resources.resw"))))
            .ToList();

        Assert.NotEmpty(localeResw);

        var partial = new List<string>();
        var identicalWithoutRationale = new List<string>();

        foreach (var (key, enValue) in referenceResw.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var identicalLocales = localeResw
                .Where(l => l.Resources.TryGetValue(key, out var value) && value == enValue)
                .Select(l => l.Locale)
                .ToList();

            if (identicalLocales.Count == 0)
                continue;

            if (identicalLocales.Count != localeResw.Count)
            {
                // Allow Latin-script loanwords (e.g. "OK") to be identical
                // across en-us/fr-fr/nl-nl while still being translated for
                // non-Latin-script locales (zh-CN, zh-TW).
                if (LatinScriptInvariantResourceKeys.Contains(key)
                    && identicalLocales.All(l => LatinScriptLocales.Contains(l))
                    && LatinScriptLocales.All(l => identicalLocales.Contains(l, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }
                partial.Add($"{key} ({enValue}) identical in [{string.Join(", ", identicalLocales)}]");
                continue;
            }

            if (!IsInvariantOrDeferred(key, enValue))
                identicalWithoutRationale.Add($"{key} ({enValue})");
        }

        Assert.True(partial.Count == 0,
            "Resources must be translated in all non-English locales or invariant in all. Partial entries: " +
            string.Join("; ", partial.Take(20)));
        Assert.True(identicalWithoutRationale.Count == 0,
            "Resources identical to en-us in every non-English locale need an invariant/deferred rationale. Entries: " +
            string.Join("; ", identicalWithoutRationale.Take(20)));
    }
}
