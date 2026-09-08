namespace OpenClaw.Connection.Tests;

/// <summary>
/// Serializes every test class that mutates process-wide environment variables.
/// <c>Environment.SetEnvironmentVariable</c> has no per-test scope, so two classes
/// running in parallel would see each other's values (and each other's restores).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Connection environment variables";
}
