using System.Runtime.Versioning;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

[SupportedOSPlatform("windows")]
public sealed class ProgramArgumentTests : IDisposable
{
    private static readonly string[] s_expectedValueOptionNames =
    [
        "--config",
        "--log-path",
        "--json-output",
        "--data-dir",
        "--local-data-dir",
        "--distro-name",
        "--gateway-port",
        "--gateway-candidate-package",
        "--tailscale-auth",
        "--tailscale-hostname",
        "--autostart-name",
        "--startup-task-name",
    ];

    private static readonly string[] s_expectedFlagOptionNames =
    [
        "--headless",
        "--rollback-on-failure",
        "--no-rollback-on-failure",
        "--dry-run",
        "--wizard-only",
        "--uninstall",
        "--confirm-destructive",
        "--preserve-logs",
        "--tailscale",
        "--tailscale-trust-auth",
        "--validate-gateway-candidate",
    ];

    private readonly string _tempDir;

    public ProgramArgumentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"program-argument-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    public static TheoryData<string> ValueOptionNames
        => new(Program.ValueOptionNames);

    public static TheoryData<string> FlagOptionNames
        => new(Program.FlagOptionNames);

    public static TheoryData<string> EmptyConfigContents
        => new("", " \t\r\n");

    public static TheoryData<string, string> WhitespaceValueCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var optionName in Program.ValueOptionNames)
            {
                foreach (var value in new[] { " ", "\t", "\r\n" })
                    cases.Add(optionName, value);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_RejectsMissingValue(string optionName)
    {
        var parsed = Program.TryParseArguments([optionName], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_RejectsAnotherOptionAsValue(string optionName)
    {
        var parsed = Program.TryParseArguments(
            [optionName, "--headless"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_RejectsEmptyValue(string optionName)
    {
        var parsed = Program.TryParseArguments([optionName, ""], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(WhitespaceValueCases))]
    public void TryParseArguments_RejectsWhitespaceValue(string optionName, string value)
    {
        var parsed = Program.TryParseArguments([optionName, value], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Fact]
    public void TryParseArguments_RejectsDuplicateValueOption()
    {
        var parsed = Program.TryParseArguments(
            ["--data-dir", "first", "--data-dir", "second"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("--data-dir may only be specified once.", error);
    }

    [Fact]
    public void OptionContract_MatchesExpectedOptions()
    {
        Assert.Equal(s_expectedValueOptionNames, Program.ValueOptionNames);
        Assert.Equal(s_expectedFlagOptionNames, Program.FlagOptionNames);
    }

    [Fact]
    public void TryParseArguments_ParsesEverySeparatedValueOption()
    {
        var args = Program.ValueOptionNames
            .SelectMany((option, index) => new[] { option, $"value-{index}" })
            .ToArray();

        var parsed = Program.TryParseArguments(args, out var result, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        for (var i = 0; i < Program.ValueOptionNames.Count; i++)
            Assert.Equal($"value-{i}", result.GetValue(Program.ValueOptionNames[i]));
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_ParsesEqualsValue(string optionName)
    {
        var parsed = Program.TryParseArguments(
            [$"{optionName}=value=with=equals"],
            out var result,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("value=with=equals", result.GetValue(optionName));
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_AcceptsValueOptionCaseInsensitively(string optionName)
    {
        var parsed = Program.TryParseArguments(
            [$"{optionName.ToUpperInvariant()}=value"],
            out var result,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("value", result.GetValue(optionName));
    }

    [Theory]
    [MemberData(nameof(ValueOptionNames))]
    public void TryParseArguments_RejectsEmptyEqualsValue(string optionName)
    {
        var parsed = Program.TryParseArguments([$"{optionName}="], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(WhitespaceValueCases))]
    public void TryParseArguments_RejectsWhitespaceEqualsValue(string optionName, string value)
    {
        var parsed = Program.TryParseArguments([$"{optionName}={value}"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} requires a value.", error);
    }

    [Theory]
    [MemberData(nameof(FlagOptionNames))]
    public void TryParseArguments_AcceptsBareFlagCaseInsensitively(string optionName)
    {
        var mixedCaseName = optionName.ToUpperInvariant();

        var parsed = Program.TryParseArguments([mixedCaseName], out var result, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.True(result.HasFlag(optionName));
    }

    [Theory]
    [MemberData(nameof(FlagOptionNames))]
    public void TryParseArguments_RejectsFlagValue(string optionName)
    {
        var parsed = Program.TryParseArguments([$"{optionName}=true"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"{optionName} does not accept a value.", error);
    }

    [Fact]
    public void TryParseArguments_AcceptsDuplicateFlags()
    {
        var parsed = Program.TryParseArguments(
            ["--dry-run", "--DRY-RUN"],
            out var result,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.True(result.HasFlag("--dry-run"));
    }

    [Theory]
    [InlineData("--confg", "Unknown option '--confg'.")]
    [InlineData("--confg=missing.json", "Unknown option '--confg=missing.json'.")]
    [InlineData("--", "Unknown option '--'.")]
    public void TryParseArguments_RejectsUnknownOption(string token, string expectedError)
    {
        var parsed = Program.TryParseArguments([token], out _, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("-x")]
    [InlineData("")]
    public void TryParseArguments_RejectsUnexpectedPositionalArgument(string token)
    {
        var parsed = Program.TryParseArguments([token], out _, out var error);

        Assert.False(parsed);
        Assert.Equal($"Unexpected argument '{token}'.", error);
    }

    [Fact]
    public void TryParseArguments_ReportsFirstInvalidToken()
    {
        var parsed = Program.TryParseArguments(
            ["--dry-run", "--confg", "missing.json", "--unknown"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("Unknown option '--confg'.", error);
    }

    [Fact]
    public void TryParseArguments_RejectsPositionalBeforeKnownOption()
    {
        var parsed = Program.TryParseArguments(
            ["unexpected", "--dry-run"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("Unexpected argument 'unexpected'.", error);
    }

    [Fact]
    public async Task Main_RejectsConfigFollowedByFlagBeforeLoadingBundledDefault()
    {
        var exitCode = await Program.Main(["--config", "--headless"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsMissingEqualsConfigBeforeLoadingBundledDefault()
    {
        var configPath = Path.Combine(_tempDir, "equals-missing.json");

        var exitCode = await Program.Main([$"--config={configPath}", "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsMisspelledConfigBeforeLoadingBundledDefault()
    {
        var configPath = Path.Combine(_tempDir, "valid.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var exitCode = await Program.Main(["--confg", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitMissingConfig()
    {
        var configPath = Path.Combine(_tempDir, "missing.json");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitMalformedConfig()
    {
        var configPath = Path.Combine(_tempDir, "malformed.json");
        await File.WriteAllTextAsync(configPath, "{ not-json");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Theory]
    [MemberData(nameof(EmptyConfigContents))]
    public async Task Main_RejectsExplicitEmptyConfig(string contents)
    {
        var configPath = Path.Combine(_tempDir, $"empty-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, contents);

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitNullConfig()
    {
        var configPath = Path.Combine(_tempDir, "null.json");
        await File.WriteAllTextAsync(configPath, "null");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitUnreadableConfig()
    {
        var configPath = Path.Combine(_tempDir, "locked.json");
        await File.WriteAllTextAsync(configPath, "{}");
        await using var configLock = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_RejectsExplicitConfigWithInvalidPathSyntax()
    {
        var exitCode = await Program.Main(["--config", "invalid\0path.json", "--dry-run"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsExplicitValidConfig()
    {
        var configPath = Path.Combine(_tempDir, "valid.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsExplicitValidEqualsConfig()
    {
        var configPath = Path.Combine(_tempDir, "valid-equals.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var exitCode = await Program.Main([$"--config={configPath}", "--dry-run"]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsOfficialVersionPinWithoutValidationFlag()
    {
        var configPath = Path.Combine(_tempDir, "candidate.json");
        await File.WriteAllTextAsync(
            configPath,
            """{"Gateway":{"Version":"2026.8.1"}}""");

        var exitCode = await Program.Main(["--config", configPath, "--dry-run"]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_AllowsExactOfficialVersionForCandidateValidation()
    {
        var configPath = Path.Combine(_tempDir, "candidate-validation.json");
        await File.WriteAllTextAsync(
            configPath,
            """{"Gateway":{"Version":"2026.8.1"}}""");

        var exitCode = await Program.Main(
            ["--config", configPath, "--dry-run", "--validate-gateway-candidate"]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsExactPublishedCandidateWithValidationFlag()
    {
        const string version = "2026.8.1";
        var configPath = Path.Combine(_tempDir, "external-candidate.json");
        await File.WriteAllTextAsync(
            configPath,
            $"{{\"Gateway\":{{\"Version\":\"{version}\"}}}}");

        var exitCode = await Program.Main(
            [
                "--config", configPath,
                "--dry-run",
                "--validate-gateway-candidate"
            ]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_AcceptsUnembeddedCandidatePackageOnlyForHeadlessRollbackValidation()
    {
        const string version = "2026.8.1";
        var configPath = Path.Combine(_tempDir, "external-package-candidate.json");
        var packagePath = Path.Combine(_tempDir, "openclaw-current.tgz");
        await File.WriteAllTextAsync(
            configPath,
            $"{{\"Gateway\":{{\"Version\":\"{version}\"}}}}");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);

        var exitCode = await Program.Main(
            [
                "--config", configPath,
                "--dry-run",
                "--headless",
                "--rollback-on-failure",
                "--validate-gateway-candidate",
                "--gateway-candidate-package", packagePath
            ]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Main_RejectsCandidatePackageOutsideExplicitValidationMode()
    {
        var configPath = Path.Combine(_tempDir, "package-without-validation.json");
        var packagePath = Path.Combine(_tempDir, "openclaw-current.tgz");
        await File.WriteAllTextAsync(configPath, "{}");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);

        var exitCode = await Program.Main(
            [
                "--config", configPath,
                "--dry-run",
                "--gateway-candidate-package", packagePath
            ]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void SerializeJsonOutput_IncludesRequiresRestart()
    {
        var config = new SetupConfig();
        GatewayInstallPolicy.ValidateAndApply(config);
        var result = new PipelineResult(PipelineOutcome.Failed, "ensure-wsl-platform")
        {
            RequiresRestart = true,
        };

        using var document = JsonDocument.Parse(
            Program.SerializeJsonOutput(result, config, "journal.jsonl"));
        Assert.True(document.RootElement.TryGetProperty("requiresRestart", out var requiresRestart));
        Assert.True(requiresRestart.GetBoolean());
    }
}
