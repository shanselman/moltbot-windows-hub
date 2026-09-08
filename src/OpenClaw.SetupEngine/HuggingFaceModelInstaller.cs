using OpenClaw.Shared.Inference.Catalog;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine;

internal enum HuggingFaceModelInstallDisposition
{
    Downloaded,
    ReusedVerified,
}

/// <summary>
/// Distinguishes the two long-running phases so the UI can label them apart. Verifying
/// a multi-gigabyte candidate takes minutes and must not be reported as a download.
/// </summary>
internal enum HuggingFaceModelInstallPhase
{
    Downloading,
    Verifying,
}

internal sealed record HuggingFaceModelInstallProgress(
    long CompletedBytes,
    long TotalBytes,
    HuggingFaceModelInstallPhase Phase = HuggingFaceModelInstallPhase.Downloading)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1)
        : 0;
}

/// <param name="CacheRoot">
/// The hub cache root this model was installed into, recorded so later validation does
/// not depend on the ambient <c>HF_HUB_CACHE</c>/<c>HF_HOME</c> environment.
/// </param>
internal sealed record HuggingFaceModelInstallResult(
    string ModelPath,
    string CacheRoot,
    HuggingFaceModelInstallDisposition Disposition,
    bool CreatedThisRun);

internal class HuggingFaceModelInstallException : Exception
{
    public HuggingFaceModelInstallException(string message)
        : base(message)
    {
    }

    public HuggingFaceModelInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class TransientHuggingFaceModelInstallException : HuggingFaceModelInstallException
{
    public TransientHuggingFaceModelInstallException(string message)
        : base(message)
    {
    }
}

internal interface IHuggingFaceModelAcquirer
{
    Task<HuggingFaceModelInstallResult> InstallAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken);

    void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install);

    void RemovePartialModel(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model);
}

/// <summary>
/// Downloads one immutable Hugging Face GGUF, verifies its exact byte count and
/// SHA-256 digest, and atomically promotes it beside its partial file. A partial
/// left by process termination is resumed with an HTTP range request. Any
/// observed setup failure or cancellation removes the partial file.
/// </summary>
internal sealed class HuggingFaceModelInstaller : IHuggingFaceModelAcquirer
{
    private const int BufferSize = 1024 * 1024;
    private const int ProgressIntervalBytes = 4 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private const int MaximumDownloadAttempts = 4;

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    public HuggingFaceModelInstaller(HttpClient httpClient) =>
        (_httpClient, _retryDelay) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)), Task.Delay);

    internal HuggingFaceModelInstaller(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> retryDelay) =>
        (_httpClient, _retryDelay) =
            (httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
             retryDelay ?? throw new ArgumentNullException(nameof(retryDelay)));

    public event EventHandler<HuggingFaceModelInstallProgress>? ProgressChanged;

    public async Task<HuggingFaceModelInstallResult> InstallAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Weights.Role != ArtifactRole.ModelWeights ||
            model.Weights.Source is not HuggingFaceRevisionSource source)
        {
            throw new HuggingFaceModelInstallException(
                "The Local AI model must be an immutable Hugging Face weights artifact.");
        }

        string cacheRoot = HuggingFaceHubCache.ResolveCacheRoot();
        if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                cacheRoot,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out string modelPath,
                out string partialPath,
                out string pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        if (Directory.Exists(modelPath))
            throw new HuggingFaceModelInstallException("The managed Local AI model path is an existing directory.");
        if (Directory.Exists(partialPath))
            throw new HuggingFaceModelInstallException("The managed Local AI partial model path is an existing directory.");

        if (File.Exists(modelPath))
        {
            if (await VerifyFileAsync(cacheRoot, modelPath, model.Weights, progress, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new HuggingFaceModelInstallResult(
                    modelPath,
                    cacheRoot,
                    HuggingFaceModelInstallDisposition.ReusedVerified,
                    CreatedThisRun: false);
            }

            RemoveUnverifiedSnapshot(cacheRoot, modelPath);
        }

        // The snapshot directory is created lazily, immediately before the first write,
        // so an install that fails earlier leaves no empty revision behind in this
        // shared cache for `hf cache scan` and friends to report.
        if (!HuggingFaceHubCache.TryValidateManagedPath(
                cacheRoot,
                modelPath,
                out modelPath,
                out pathError) ||
            !HuggingFaceHubCache.TryValidateManagedPath(
                cacheRoot,
                partialPath,
                out partialPath,
                out pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        // A standard hub-cache download made by huggingface_hub, the hf CLI, or
        // llama.cpp lands in the content-addressed blobs store or under another
        // revision's snapshot. Reuse it instead of re-downloading when it
        // matches the pinned size and SHA-256 digest exactly.
        if (await TryReuseVerifiedCandidateAsync(
                cacheRoot,
                source,
                model.Weights,
                modelPath,
                progress,
                cancellationToken).ConfigureAwait(false))
        {
            // The link is this run's creation: deleting it on rollback removes only the
            // extra directory entry, never the pre-existing blob it points at.
            return new HuggingFaceModelInstallResult(
                modelPath,
                cacheRoot,
                HuggingFaceModelInstallDisposition.ReusedVerified,
                CreatedThisRun: true);
        }

        var promoted = false;
        var preservePartial = false;
        try
        {
            bool verifiedCompletePartial = File.Exists(partialPath) &&
                new FileInfo(partialPath).Length == model.Weights.SizeBytes &&
                await VerifyFileAsync(cacheRoot, partialPath, model.Weights, progress, cancellationToken)
                    .ConfigureAwait(false);
            if (!verifiedCompletePartial)
            {
                if (File.Exists(partialPath) &&
                    new FileInfo(partialPath).Length >= model.Weights.SizeBytes)
                {
                    TryDeletePartial(cacheRoot, partialPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
                await DownloadAndVerifyAsync(
                        cacheRoot,
                        model.Weights,
                        partialPath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                    cacheRoot,
                    source.RepositoryId,
                    source.RevisionSha,
                    model.Weights.RelativePath,
                    out string revalidatedModelPath,
                    out string revalidatedPartialPath,
                    out pathError) ||
                !string.Equals(modelPath, revalidatedModelPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(partialPath, revalidatedPartialPath, StringComparison.OrdinalIgnoreCase) ||
                !HuggingFaceHubCache.TryValidateManagedPath(
                    cacheRoot,
                    modelPath,
                    out string writableModelPath,
                    out pathError) ||
                !HuggingFaceHubCache.TryValidateManagedPath(
                    cacheRoot,
                    partialPath,
                    out string writablePartialPath,
                    out pathError))
            {
                throw new HuggingFaceModelInstallException(
                    string.IsNullOrWhiteSpace(pathError)
                        ? "The Local AI model paths changed before promotion."
                        : pathError);
            }

            if (File.Exists(modelPath))
            {
                throw new HuggingFaceModelInstallException(
                    "The Local AI model target appeared while the download was in progress.");
            }

            File.Move(writablePartialPath, writableModelPath);
            promoted = true;
            return new HuggingFaceModelInstallResult(
                modelPath,
                cacheRoot,
                HuggingFaceModelInstallDisposition.Downloaded,
                CreatedThisRun: true);
        }
        catch (OperationCanceledException)
        {
            preservePartial = File.Exists(partialPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException)
        {
            preservePartial = File.Exists(partialPath);
            throw;
        }
        finally
        {
            if (!promoted && !preservePartial)
            {
                TryDeletePartial(cacheRoot, partialPath);
                TryRemoveEmptySnapshotDirectory(cacheRoot, partialPath);
            }
        }
    }

    /// <summary>
    /// Removes the pinned revision's snapshot directory when this run created it and
    /// left nothing in it. The hub cache is shared, so a failed install must not leave a
    /// bogus empty revision for other tools' cache scans to report.
    /// </summary>
    private static void TryRemoveEmptySnapshotDirectory(string cacheRoot, string partialPath)
    {
        try
        {
            if (Path.GetDirectoryName(partialPath) is not { Length: > 0 } snapshotDirectory ||
                !HuggingFaceHubCache.TryValidateManagedPath(
                    cacheRoot,
                    snapshotDirectory,
                    out string validatedDirectory,
                    out _) ||
                !Directory.Exists(validatedDirectory))
            {
                return;
            }

            // A non-recursive delete fails on a non-empty directory, which is exactly the
            // guard wanted: anything else in this revision belongs to somebody else.
            Directory.Delete(validatedDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not mask the acquisition result.
        }
    }

    public void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (!install.CreatedThisRun)
            return;

        if (!HuggingFaceHubCache.TryValidateManagedPath(
                install.CacheRoot,
                install.ModelPath,
                out string deletePath,
                out string error))
        {
            throw new InvalidDataException(error);
        }

        if (File.Exists(deletePath))
            File.Delete(deletePath);
        TryRemoveEmptySnapshotDirectory(install.CacheRoot, deletePath);
    }

    public void RemovePartialModel(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Weights.Source is not HuggingFaceRevisionSource source)
            throw new InvalidDataException("The Local AI model does not have immutable Hugging Face provenance.");
        string cacheRoot = HuggingFaceHubCache.ResolveCacheRoot();
        if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                cacheRoot,
                source.RepositoryId,
                source.RevisionSha,
                model.Weights.RelativePath,
                out _,
                out string partialPath,
                out string error))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error) ? "The Local AI partial model path is invalid." : error);
        }

        if (Directory.Exists(partialPath))
            throw new InvalidDataException("The Local AI partial model path is an existing directory.");
        if (File.Exists(partialPath))
        {
            if (!HuggingFaceHubCache.TryValidateManagedPath(
                    cacheRoot,
                    partialPath,
                    out string deletePath,
                    out error))
            {
                throw new InvalidDataException(error);
            }
            File.Delete(deletePath);
        }
    }

    /// <summary>
    /// Deletes a pinned snapshot entry whose content failed the pinned size and digest
    /// check. A plain file is removed under the strict managed-path rules. The one
    /// standard exception is a snapshot symbolic link into this repository's own
    /// <c>blobs</c> directory: <c>huggingface_hub</c> writes exactly that, and refusing
    /// to unlink it would leave setup permanently stuck on a bad blob. Only the link is
    /// removed; the blob it names is left for its owner to manage.
    /// </summary>
    private static void RemoveUnverifiedSnapshot(string cacheRoot, string modelPath)
    {
        if (HuggingFaceHubCache.TryValidateManagedPath(
                cacheRoot,
                modelPath,
                out string deletePath,
                out string error))
        {
            File.Delete(deletePath);
            return;
        }

        if (!HuggingFaceHubCache.TryValidateSnapshotReadPath(
                cacheRoot,
                modelPath,
                out string linkPath,
                out _) ||
            new FileInfo(linkPath).LinkTarget is null)
        {
            throw new HuggingFaceModelInstallException(error);
        }

        File.Delete(linkPath);
    }

    private async Task DownloadAndVerifyAsync(
        string cacheRoot,
        PinnedArtifact artifact,
        string partialPath,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAndVerifyAttemptAsync(
                        cacheRoot,
                        artifact,
                        partialPath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException or TransientHuggingFaceModelInstallException &&
                attempt < MaximumDownloadAttempts &&
                !cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.FromSeconds(1 << (attempt - 1));
                await _retryDelay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Looks for the pinned model content outside the pinned revision's snapshot:
    /// the content-addressed blob named by the pinned SHA-256 and same-named
    /// snapshot entries in other revisions of the repository. A candidate is
    /// accepted only after the standard snapshot-read validation (which accepts
    /// the one snapshot-to-blob symlink layout and rejects anything else) plus the
    /// pinned size and digest check. Accepted content is hardlinked into the
    /// pinned snapshot path so the manifest keeps its canonical form and rollback
    /// removes only the link, never the pre-existing blob.
    /// </summary>
    private static async Task<bool> TryReuseVerifiedCandidateAsync(
        string cacheRoot,
        HuggingFaceRevisionSource source,
        PinnedArtifact artifact,
        string modelPath,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!HuggingFaceHubCache.TryGetReuseCandidates(
                cacheRoot,
                source.RepositoryId,
                Path.GetFileName(artifact.RelativePath),
                artifact.Sha256,
                out IReadOnlyList<string> candidates,
                out _))
        {
            return false;
        }

        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await VerifyFileAsync(cacheRoot, candidate, artifact, progress, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                // A verified candidate may itself be the standard snapshot-to-blob
                // symbolic link. Hard-linking a reparse point would only duplicate the
                // link, so resolve it to the blob and link the content instead.
                if (!TryResolveLinkSource(cacheRoot, candidate, out string linkSource))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                if (HuggingFaceHubCache.TryCreateHardLink(modelPath, linkSource))
                    return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            // A candidate that cannot be linked must not block the download path.
        }

        return false;
    }

    /// <summary>
    /// Resolves a verified reuse candidate to the concrete file whose content should be
    /// hard-linked: the candidate itself when it is a regular file, or the blob a
    /// standard snapshot link points at. The resolved target must still pass the strict
    /// managed-path rules, so a link out of the cache can never become a hard link.
    /// </summary>
    private static bool TryResolveLinkSource(string cacheRoot, string candidate, out string linkSource)
    {
        linkSource = candidate;
        if (new FileInfo(candidate).LinkTarget is null)
            return true;

        FileSystemInfo? target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
        return target is not null &&
            HuggingFaceHubCache.TryValidateManagedPath(cacheRoot, target.FullName, out linkSource, out _);
    }

    private async Task DownloadAndVerifyAttemptAsync(
        string cacheRoot,
        PinnedArtifact artifact,
        string partialPath,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        long resumeOffset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (resumeOffset < 0 || resumeOffset >= artifact.SizeBytes)
        {
            TryDeletePartial(cacheRoot, partialPath);
            resumeOffset = 0;
        }

        // Hashing a multi-gigabyte partial takes minutes, so it happens before the
        // request is sent: holding an unread response body open for that long invites
        // the server to drop the connection and waste the whole attempt.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (resumeOffset > 0)
        {
            await HashExistingPartialAsync(cacheRoot, partialPath, hash, progress, artifact.SizeBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        using HttpResponseMessage response = await SendWithValidatedRedirectsAsync(
                artifact.DownloadUri,
                resumeOffset,
                cancellationToken)
            .ConfigureAwait(false);

        bool append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeOffset > 0 && !append && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face range request failed with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }
        if (resumeOffset == 0 && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face download failed with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (append)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.From != resumeOffset || range.To is null || range.Length != artifact.SizeBytes)
            {
                throw new HuggingFaceModelInstallException(
                    "The Hugging Face range response did not match the partial model file.");
            }
        }
        else
        {
            // The server ignored the range: the body restarts from zero, so the digest
            // of the bytes already hashed above must be discarded.
            resumeOffset = 0;
            hash.GetHashAndReset();
        }

        long expectedBodyBytes = artifact.SizeBytes - resumeOffset;
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBodyBytes)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face response declared {contentLength} bytes; expected {expectedBodyBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!HuggingFaceHubCache.TryValidateManagedPath(
                cacheRoot,
                partialPath,
                out string writablePartialPath,
                out string pathError))
        {
            throw new HuggingFaceModelInstallException(pathError);
        }

        await using var destination = new FileStream(
            writablePartialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

        long completed = resumeOffset;
        long lastReported = completed;
        Report(progress, completed, artifact.SizeBytes);
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            completed += read;
            if (completed > artifact.SizeBytes)
                throw new HuggingFaceModelInstallException("The Hugging Face response exceeded the pinned model size.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (completed - lastReported >= ProgressIntervalBytes)
            {
                Report(progress, completed, artifact.SizeBytes);
                lastReported = completed;
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        if (completed != artifact.SizeBytes)
        {
            throw new HuggingFaceModelInstallException(
                $"The Hugging Face response contained {completed} bytes; expected {artifact.SizeBytes} bytes.");
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(artifact.Sha256.Value)))
        {
            throw new HuggingFaceModelInstallException("The Hugging Face model SHA-256 digest did not match its pin.");
        }

        Report(progress, completed, artifact.SizeBytes);
    }

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        Uri initialUri,
        long resumeOffset,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUri(initialUri, initialRequest: true);
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (resumeOffset > 0)
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            Uri observed = response.RequestMessage?.RequestUri ?? current;
            ValidateDownloadUri(observed, initialRequest: false);
            if (!IsRedirect(response.StatusCode))
            {
                if (IsTransientStatus(response.StatusCode))
                {
                    int statusCode = (int)response.StatusCode;
                    string reason = response.StatusCode.ToString();
                    response.Dispose();
                    throw new TransientHuggingFaceModelInstallException(
                        $"The Hugging Face download returned transient HTTP status {statusCode} ({reason}).");
                }

                return response;
            }

            if (redirect == MaximumRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(observed, response.Headers.Location);
            response.Dispose();
            ValidateDownloadUri(next, initialRequest: false);
            current = next;
        }

        throw new HuggingFaceModelInstallException("The Hugging Face download exceeded the redirect limit.");
    }

    private static void ValidateDownloadUri(Uri uri, bool initialRequest)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new HuggingFaceModelInstallException("The model download URI must be credential-free HTTPS.");
        }

        bool allowed = string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            (!initialRequest &&
             (uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
            throw new HuggingFaceModelInstallException("The model download redirected to an untrusted host.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode is >= 500 and <= 599;

    private static async Task HashExistingPartialAsync(
        string cacheRoot,
        string partialPath,
        IncrementalHash hash,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (!HuggingFaceHubCache.TryOpenSnapshotReadPath(
                cacheRoot,
                partialPath,
                out FileStream? stream,
                out _,
                out string error))
        {
            throw new HuggingFaceModelInstallException(error);
        }

        await using FileStream validatedStream = stream!;
        await HashStreamAsync(validatedStream, hash.AppendData, progress, totalBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies one file against its pinned size and SHA-256 digest, reporting hashing
    /// progress. Multi-gigabyte weights take minutes to hash, so a silent verification
    /// is indistinguishable from a hang.
    /// </summary>
    internal static async Task<bool> VerifyFileAsync(
        string cacheRoot,
        string path,
        PinnedArtifact artifact,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!HuggingFaceHubCache.TryOpenSnapshotReadPath(
                cacheRoot,
                path,
                out FileStream? stream,
                out _,
                out _))
        {
            return false;
        }

        await using FileStream validatedStream = stream!;
        if (validatedStream.Length != artifact.SizeBytes)
            return false;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await HashStreamAsync(validatedStream, hash.AppendData, progress, artifact.SizeBytes, cancellationToken)
            .ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            hash.GetHashAndReset(),
            Convert.FromHexString(artifact.Sha256.Value));
    }

    private static async Task HashStreamAsync(
        Stream stream,
        Action<byte[], int, int> append,
        IProgress<HuggingFaceModelInstallProgress>? progress,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long hashed = 0;
        long lastReported = 0;
        progress?.Report(new HuggingFaceModelInstallProgress(0, totalBytes, HuggingFaceModelInstallPhase.Verifying));
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            append(buffer, 0, read);
            hashed += read;
            if (hashed - lastReported >= ProgressIntervalBytes)
            {
                progress?.Report(new HuggingFaceModelInstallProgress(
                    hashed,
                    totalBytes,
                    HuggingFaceModelInstallPhase.Verifying));
                lastReported = hashed;
            }
        }
    }

    private void Report(
        IProgress<HuggingFaceModelInstallProgress>? progress,
        long completed,
        long total)
    {
        var value = new HuggingFaceModelInstallProgress(completed, total);
        progress?.Report(value);
        ProgressChanged?.Invoke(this, value);
    }

    private static void TryDeletePartial(string cacheRoot, string partialPath)
    {
        try
        {
            if (HuggingFaceHubCache.TryValidateManagedPath(
                    cacheRoot,
                    partialPath,
                    out string deletePath,
                    out _) &&
                File.Exists(deletePath))
            {
                File.Delete(deletePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not mask the acquisition result.
        }
    }
}
