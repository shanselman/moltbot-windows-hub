namespace OpenClaw.Tray.Tests;

public sealed class ExtensionsPageContractTests
{
    private static string ReadPage(string extension) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            $"ExtensionsPage.{extension}"));

    [Fact]
    public void InstalledSkills_UseBoundedGridRowForScrolling()
    {
        var xaml = ReadPage("xaml");

        Assert.Contains(
            "x:Name=\"InstalledSkillsList\" Grid.Row=\"2\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"SkillsEmptyState\" Grid.Row=\"2\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionReviews_ShareBoundedRowWithResultsAndScroll()
    {
        var xaml = ReadPage("xaml");

        Assert.Contains(
            "x:Name=\"SkillReviewPanel\" Grid.Row=\"1\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"SkillSearchResultsList\" Grid.Row=\"1\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"PluginReviewPanel\" Grid.Row=\"1\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"PluginSearchResultsList\" Grid.Row=\"1\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "VerticalScrollBarVisibility=\"Auto\""));
    }

    [Fact]
    public void AgentSelector_ResynchronizesWithoutDispatchingSelection()
    {
        var code = ReadPage("xaml.cs");

        Assert.Contains("SynchronizeAgentSelector", code, StringComparison.Ordinal);
        Assert.Contains("AgentCombo.ItemsSource = _viewModel.AgentIds", code, StringComparison.Ordinal);
        Assert.Contains("AgentCombo.SelectedItem = _viewModel.SelectedAgentId", code, StringComparison.Ordinal);
        Assert.Contains("if (_synchronizingAgentSelection ||", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }
        return count;
    }
}
