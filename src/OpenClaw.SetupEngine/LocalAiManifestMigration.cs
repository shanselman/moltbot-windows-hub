using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Upgrades an installation manifest written before the model moved into the shared
/// Hugging Face hub cache.
/// </summary>
/// <remarks>
/// Schema 3 stored <c>modelPath</c> relative to the Local AI root
/// (<c>models\&lt;org&gt;\&lt;repo&gt;\&lt;revision&gt;\&lt;file&gt;.gguf</c>); schema 4 stores an
/// absolute path in the hub cache plus the cache root it lives in. Without this
/// migration every existing installation fails reconciliation, and the only offered
/// recovery -- uninstall -- deletes a weights file that is tens of gigabytes and then
/// downloads the identical bytes again. Relocating the existing file is a rename on
/// the same volume, so the upgrade costs nothing and preserves the download.
/// </remarks>
internal static class LocalAiManifestMigration
{
    private const int HubCacheSchemaVersion = 4;
    private const int LegacyLocalModelSchemaVersion = 3;

    /// <summary>
    /// Rewrites a schema-3 manifest in place, relocating its weights file into the hub
    /// cache. Returns true when a manifest was migrated. Any manifest that is absent,
    /// already current, unreadable, or whose weights cannot be relocated is left
    /// untouched for the caller's normal validation to reject.
    /// </summary>
    public static async Task<bool> TryUpgradeLegacyManifestAsync(
        LocalAiPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.ManifestPath))
            return false;

        JsonObject manifest;
        try
        {
            string json = await File.ReadAllTextAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(json) is not JsonObject parsed)
                return false;
            manifest = parsed;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (manifest["schemaVersion"]?.GetValue<int>() != LegacyLocalModelSchemaVersion)
            return false;

        if (!TryResolveLegacyModel(paths, manifest, out string legacyModelPath, out string cacheRoot, out string hubModelPath))
            return false;

        try
        {
            if (!RelocateWeights(legacyModelPath, cacheRoot, hubModelPath))
                return false;

            manifest["schemaVersion"] = HubCacheSchemaVersion;
            manifest["modelPath"] = hubModelPath;
            manifest["modelCacheRoot"] = cacheRoot;
            await WriteAtomicallyAsync(paths, manifest, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryResolveLegacyModel(
        LocalAiPaths paths,
        JsonObject manifest,
        out string legacyModelPath,
        out string cacheRoot,
        out string hubModelPath)
    {
        legacyModelPath = "";
        cacheRoot = "";
        hubModelPath = "";

        if (manifest["modelPath"]?.GetValue<string>() is not { Length: > 0 } relativeModelPath ||
            manifest["modelId"]?.GetValue<string>() is not { Length: > 0 } modelId ||
            manifest["modelAsset"] is not JsonObject modelAsset ||
            modelAsset["fileName"]?.GetValue<string>() is not { Length: > 0 } fileName)
        {
            return false;
        }

        string[] identity = modelId.Split('@');
        if (identity.Length != 2)
            return false;

        try
        {
            // ResolveContainedPath rejects anything that escapes the app-owned root, so a
            // tampered manifest cannot point the migration at an arbitrary file.
            legacyModelPath = paths.ResolveContainedPath(relativeModelPath, "modelPath");
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return false;
        }

        cacheRoot = HuggingFaceHubCache.ResolveCacheRoot();
        return HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            identity[0],
            identity[1],
            fileName,
            out hubModelPath,
            out _,
            out _);
    }

    /// <summary>
    /// Moves the legacy weights file to its hub cache snapshot path. A file already
    /// present at the destination wins: it is the standard location and may be shared
    /// with other tools, so it is never overwritten. Returns false when neither
    /// location holds the weights, leaving the manifest unmigrated.
    /// </summary>
    private static bool RelocateWeights(string legacyModelPath, string cacheRoot, string hubModelPath)
    {
        if (!HuggingFaceHubCache.TryValidateManagedPath(cacheRoot, hubModelPath, out string destination, out _))
            return false;

        if (File.Exists(destination))
            return true;
        if (!File.Exists(legacyModelPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(legacyModelPath, destination);
        return true;
    }

    private static async Task WriteAtomicallyAsync(
        LocalAiPaths paths,
        JsonObject manifest,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            paths.RootDirectory,
            $".{Path.GetFileName(paths.ManifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, paths.ManifestPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup must not mask the migration result.
            }
        }
    }
}
