using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System.ComponentModel;

namespace OpenClawTray.Pages;

public sealed partial class ExtensionsPage : Page
{
    private ExtensionsPageViewModel? _viewModel;
    private bool _showingInstalledSkills = true;
    private bool _showingInstalledPlugins = true;
    private SkillReviewPresentation? _skillReview;
    private PluginReviewPresentation? _pluginReview;

    public ExtensionsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        SkillFilterCombo.SelectedIndex = 0;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as ExtensionsPageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            AgentCombo.ItemsSource = _viewModel.AgentIds;
            AgentCombo.SelectedItem = _viewModel.SelectedAgentId;
        }
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (_viewModel is null)
            return;
        SkillsProgress.Visibility = _viewModel.IsLoadingSkills || _viewModel.IsSearchingSkills
            ? Visibility.Visible
            : Visibility.Collapsed;
        SkillsCountText.Text = _viewModel.SkillCountText;
        SkillsEmptyState.Visibility = !_viewModel.IsLoadingSkills &&
            _showingInstalledSkills && !_viewModel.HasVisibleSkills
                ? Visibility.Visible
                : Visibility.Collapsed;

        var message = _viewModel.ErrorMessage ?? _viewModel.StatusMessage;
        PageInfoBar.IsOpen = !string.IsNullOrWhiteSpace(message);
        PageInfoBar.Message = message ?? string.Empty;
        PageInfoBar.Severity = _viewModel.ErrorMessage is null
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Error;

        PluginsProgress.Visibility = _viewModel.IsLoadingPlugins || _viewModel.IsSearchingPlugins
            ? Visibility.Visible
            : Visibility.Collapsed;
        PluginsCountText.Text = _viewModel.PluginCountText;
        PluginsEmptyState.Visibility = !_viewModel.IsLoadingPlugins &&
            _showingInstalledPlugins && !_viewModel.HasInstalledPlugins
                ? Visibility.Visible
                : Visibility.Collapsed;
        var pluginMessage = _viewModel.PluginErrorMessage ?? _viewModel.PluginStatusMessage;
        PluginInfoBar.IsOpen = !string.IsNullOrWhiteSpace(pluginMessage);
        PluginInfoBar.Message = pluginMessage ?? string.Empty;
        PluginInfoBar.Severity = _viewModel.PluginErrorMessage is null
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Error;
    }

    private void OnInstalledSkillsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledSkills = true;
        InstalledSkillsPanel.Visibility = Visibility.Visible;
        DiscoverSkillsPanel.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    private void OnDiscoverSkillsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledSkills = false;
        InstalledSkillsPanel.Visibility = Visibility.Collapsed;
        DiscoverSkillsPanel.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void OnAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || AgentCombo.SelectedItem is not string agentId)
            return;
        _ = _viewModel.SelectAgentAsync(agentId);
    }

    private void OnSkillFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || SkillFilterCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !Enum.TryParse<SkillListFilter>(tag, out var filter))
        {
            return;
        }
        _viewModel.SetSkillFilter(filter);
    }

    private void OnSearchSkillsClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(SearchSkillsAsync, new AppLogger(), nameof(OnSearchSkillsClick));

    private void OnSkillSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AsyncEventHandlerGuard.Run(SearchSkillsAsync, new AppLogger(), nameof(OnSkillSearchKeyDown));
    }

    private Task SearchSkillsAsync() =>
        _viewModel?.SearchSkillsAsync(SkillSearchBox.Text) ?? Task.CompletedTask;

    private void OnReviewSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewSkillAsync(sender),
            new AppLogger(),
            nameof(OnReviewSkillClick));

    private async Task ReviewSkillAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: SkillSearchItemPresentation item })
            return;
        var review = await _viewModel.ReviewSkillAsync(item);
        if (review is null)
        {
            UpdateState();
            return;
        }

        _skillReview = review;
        SkillReviewName.Text = review.Item.Name;
        SkillReviewSummary.Text = review.Summary;
        SkillReviewPublisher.Text = LocalizationHelper.Format("ExtensionsPage_ReviewPublisherFormat", review.Publisher);
        SkillReviewVersion.Text = LocalizationHelper.Format("ExtensionsPage_ReviewVersionFormat", review.Version);
        SkillReviewMetadata.Text = review.Metadata;
        UnscannedSkillWarning.Message = LocalizationHelper.GetString("ExtensionsPage_UnscannedWarning");
        UnscannedSkillWarning.IsOpen = review.RequiresUnscannedConfirmation;
        UnscannedSkillAcknowledge.Visibility = review.RequiresUnscannedConfirmation
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnscannedSkillAcknowledge.IsChecked = false;
        InstallReviewedSkillButton.IsEnabled = !review.RequiresUnscannedConfirmation;
        SkillReviewPanel.Visibility = Visibility.Visible;
    }

    private void OnUnscannedAcknowledgeChanged(object sender, RoutedEventArgs e)
    {
        if (_skillReview?.RequiresUnscannedConfirmation == true)
            InstallReviewedSkillButton.IsEnabled = UnscannedSkillAcknowledge.IsChecked == true;
    }

    private void OnCancelSkillReviewClick(object sender, RoutedEventArgs e)
    {
        _skillReview = null;
        SkillReviewPanel.Visibility = Visibility.Collapsed;
    }

    private void OnInstallReviewedSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            InstallReviewedSkillAsync,
            new AppLogger(),
            nameof(OnInstallReviewedSkillClick));

    private async Task InstallReviewedSkillAsync()
    {
        if (_viewModel is null || _skillReview is null)
            return;
        InstallReviewedSkillButton.IsEnabled = false;
        var acknowledged = _skillReview.RequiresUnscannedConfirmation &&
            UnscannedSkillAcknowledge.IsChecked == true;
        var outcome = await _viewModel.InstallSkillAsync(_skillReview.Item, acknowledged);
        ShowOutcome(outcome);
        if (outcome.Succeeded)
        {
            _skillReview = null;
            SkillReviewPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            InstallReviewedSkillButton.IsEnabled = !_skillReview.RequiresUnscannedConfirmation || acknowledged;
        }
    }

    private void OnToggleSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSkillActionAsync(sender, update: false),
            new AppLogger(),
            nameof(OnToggleSkillClick));

    private void OnUpdateSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSkillActionAsync(sender, update: true),
            new AppLogger(),
            nameof(OnUpdateSkillClick));

    private async Task RunSkillActionAsync(object sender, bool update)
    {
        if (_viewModel is null || sender is not Button { Tag: SkillListItemPresentation item } button)
            return;
        button.IsEnabled = false;
        try
        {
            var outcome = update
                ? await _viewModel.UpdateSkillAsync(item)
                : await _viewModel.ToggleSkillAsync(item);
            ShowOutcome(outcome);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowOutcome(SkillActionOutcome outcome)
    {
        PageInfoBar.Title = LocalizationHelper.GetString(outcome.Succeeded
            ? "ExtensionsPage_ActionCompleteTitle"
            : "ExtensionsPage_ActionCouldNotCompleteTitle");
        PageInfoBar.Message = outcome.Message;
        PageInfoBar.Severity = outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        PageInfoBar.IsOpen = true;
    }

    private void OnOpenExternalLinkClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OpenExternalLinkAsync(sender),
            new AppLogger(),
            nameof(OnOpenExternalLinkClick));

    private static async Task OpenExternalLinkAsync(object sender)
    {
        if (sender is not HyperlinkButton { Tag: string raw } ||
            !HttpUrlValidator.TryParse(raw, out var canonical, out _) ||
            canonical is null)
        {
            return;
        }
        await global::Windows.System.Launcher.LaunchUriAsync(new Uri(canonical));
    }

    private void OnInstalledPluginsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledPlugins = true;
        InstalledPluginsPanel.Visibility = Visibility.Visible;
        DiscoverPluginsPanel.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    private void OnDiscoverPluginsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledPlugins = false;
        InstalledPluginsPanel.Visibility = Visibility.Collapsed;
        DiscoverPluginsPanel.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void OnSearchPluginsClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(SearchPluginsAsync, new AppLogger(), nameof(OnSearchPluginsClick));

    private void OnPluginSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AsyncEventHandlerGuard.Run(SearchPluginsAsync, new AppLogger(), nameof(OnPluginSearchKeyDown));
    }

    private Task SearchPluginsAsync() =>
        _viewModel?.SearchPluginsAsync(PluginSearchBox.Text) ?? Task.CompletedTask;

    private void OnReviewInstalledPluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewInstalledPluginAsync(sender),
            new AppLogger(),
            nameof(OnReviewInstalledPluginClick));

    private async Task ReviewInstalledPluginAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: PluginListItemPresentation item })
            return;
        ApplyPluginReview(await _viewModel.ReviewPluginAsync(item));
    }

    private void OnReviewSearchPluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewSearchPluginAsync(sender),
            new AppLogger(),
            nameof(OnReviewSearchPluginClick));

    private async Task ReviewSearchPluginAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: PluginSearchItemPresentation item })
            return;
        ApplyPluginReview(await _viewModel.ReviewPluginAsync(item));
    }

    private void ApplyPluginReview(PluginReviewPresentation? review)
    {
        if (review is null)
        {
            UpdateState();
            return;
        }
        _pluginReview = review;
        _showingInstalledPlugins = false;
        InstalledPluginsPanel.Visibility = Visibility.Collapsed;
        DiscoverPluginsPanel.Visibility = Visibility.Visible;
        PluginReviewName.Text = review.Name;
        PluginReviewDescription.Text = review.Description;
        PluginReviewVersion.Text = LocalizationHelper.Format("ExtensionsPage_ReviewVersionFormat", review.Version);
        PluginReviewOrigin.Text = LocalizationHelper.Format("ExtensionsPage_PluginOriginFormat", review.Origin);
        PluginReviewSurfaces.Text = review.DeclaredSurfaces;
        PluginReviewTrust.Text = LocalizationHelper.Format("ExtensionsPage_PluginTrustFormat", review.Trust);
        PluginReviewPanel.Visibility = Visibility.Visible;
    }

    private void OnClosePluginReviewClick(object sender, RoutedEventArgs e)
    {
        _pluginReview = null;
        PluginReviewPanel.Visibility = Visibility.Collapsed;
    }
}
