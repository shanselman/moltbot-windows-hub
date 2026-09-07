using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Security-boundary regression: llama-server's own error text (identified by a non-null
/// <c>CompletePageArgs.Detail</c>) must never be scanned for a URL to render as a clickable help
/// link. Unlike OpenClaw's own curated failure messages, that text is server-controlled
/// diagnostic evidence; scanning it for a URL could let a compromised or malicious local model
/// server plant a navigable link in the completion UI. Source-text contract test because
/// <c>CompletePage</c> is a WinUI <c>Page</c> that requires a XAML host to instantiate.
/// </summary>
public sealed class CompletePageLocalAiHelpLinkContractTests
{
    [Fact]
    public void CompletePage_NeverExtractsHelpLinkFromLocalAiFailureText()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));

        Assert.Contains(
            "var helpUrl = args.Detail is null ? ExtractHelpUrl(errorMessage) : null;",
            source);
    }

    /// <summary>
    /// The displayed/tooltipped Local AI log directory must go through the same MSIX-container
    /// path translation as the setup log link, or the text shown to the user (and copied via the
    /// tooltip) will not match the real on-disk location that <c>RevealInExplorer</c> opens.
    /// </summary>
    [Fact]
    public void CompletePage_ResolvesRealPathForServerLogDirectoryDisplay()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));

        Assert.Contains(
            "var displayDirectory = LogFileLauncher.ResolveRealPath(detail.LogDirectory);",
            source);
        Assert.Contains("ViewServerLogLink.Content = $\"Open Local AI logs → {displayDirectory}\";", source);
    }

    /// <summary>
    /// The link must not advertise a folder that is unavailable because setup failed before log
    /// initialization or because another cleanup removed it. The diagnostic text remains useful
    /// because it was captured before the router restart.
    /// </summary>
    [Fact]
    public void CompletePage_HidesServerLogLinkWhenDirectoryIsUnavailable()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        string method = ExtractMethod(source, "private void ShowServerDiagnostics(");

        Assert.Contains("if (Directory.Exists(displayDirectory))", method);
        AssertInOrder(
            method,
            "if (Directory.Exists(displayDirectory))",
            "ViewServerLogLink.Visibility = Visibility.Visible;",
            "else",
            "ViewServerLogLink.Visibility = Visibility.Collapsed;");
    }

    private static string ExtractMethod(string source, string methodSignature)
    {
        int start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find method starting with '{methodSignature}'.");
        int braceDepth = 0;
        int bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, "Expected an opening brace for the method body.");
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                braceDepth++;
            else if (source[index] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return source[start..(index + 1)];
            }
        }
        throw new InvalidOperationException("Unbalanced braces while extracting method body.");
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        int previousIndex = -1;
        foreach (string fragment in fragments)
        {
            int index = source.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{fragment}' after the previous fragment.");
            previousIndex = index;
        }
    }
}
