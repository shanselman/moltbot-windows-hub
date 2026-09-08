using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class LocalAiInstallRecoveryTests
{
    [Fact]
    public async Task ModelInstall_ResumesExactPartialWithValidatedRange()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(cacheRoot, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes[..4]);
        RangeHeaderValue? observedRange = null;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            observedRange = request.Headers.Range;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(modelBytes[4..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                4,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        var result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal("bytes=4-", observedRange?.ToString());
        Assert.Equal(modelPath, result.ModelPath);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ModelInstall_ServerIgnoringRangeRestartsFromZero()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(cacheRoot, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, "old"u8.ToArray());
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(modelBytes),
        }));

        await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
    }

    [Fact]
    public async Task ModelInstall_InvalidRangeDeletesUntrustedPartial()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(cacheRoot, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes[..4]);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(modelBytes[4..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                3,
                modelBytes.Length - 2,
                modelBytes.Length);
            return response;
        }));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            new HuggingFaceModelInstaller(client).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ModelInstall_TransientFailurePreservesResumablePartial()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "a-model-large-enough-to-retry"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(cacheRoot, model);
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            var content = new StreamContent(new ThrowAfterPrefixStream(modelBytes[(int)offset..], 2));
            var response = new HttpResponseMessage(
                offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = content,
            };
            if (offset > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset,
                    modelBytes.Length - 1,
                    modelBytes.Length);
            }
            return response;
        }));
        var installer = new HuggingFaceModelInstaller(
            client,
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None));

        Assert.True(File.Exists(partialPath));
        Assert.InRange(new FileInfo(partialPath).Length, 1, modelBytes.Length - 1);
    }

    [Fact]
    public async Task ModelInstall_VerifiedCompletePartialPromotesWithoutHttp()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(cacheRoot, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified complete partial.")));

        await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [SymbolicLinkFact]
    public async Task ModelInstall_ReusesVerifiedStandardHubSnapshotSymlinkWithoutHttp()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string repositoryDirectory = Directory.GetParent(
            Directory.GetParent(Path.GetDirectoryName(modelPath)!)!.FullName)!.FullName;
        string blobsDirectory = Path.Combine(repositoryDirectory, "blobs");
        string blobPath = Path.Combine(blobsDirectory, model.Weights.Sha256.Value);
        Directory.CreateDirectory(blobsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(blobPath, modelBytes);
        SymbolicLinkSupport.CreateSymbolicLink(
            modelPath,
            Path.GetRelativePath(Path.GetDirectoryName(modelPath)!, blobPath));

        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified hub-cache snapshot link.")));

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            TestComponent(),
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        Assert.False(result.CreatedThisRun);
        Assert.Equal(modelPath, result.ModelPath);
    }

    [Fact]
    public async Task ModelInstall_ReusesVerifiedBlobFromOtherRevisionWithoutHttp()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string repositoryDirectory = Directory.GetParent(
            Directory.GetParent(Path.GetDirectoryName(modelPath)!)!.FullName)!.FullName;
        string blobPath = Path.Combine(repositoryDirectory, "blobs", model.Weights.Sha256.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used when a pinned digest blob exists.")));

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            TestComponent(),
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        // The snapshot link is this run's creation even though its content is not.
        Assert.True(result.CreatedThisRun);
        Assert.Equal(modelPath, result.ModelPath);
        Assert.True(File.Exists(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.True(File.Exists(blobPath));
    }

    [Fact]
    public async Task ModelInstall_ReusesVerifiedSnapshotFromOtherRevisionWithoutHttp()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string snapshotsDirectory = Path.Combine(
            Directory.GetParent(
                Directory.GetParent(Path.GetDirectoryName(modelPath)!)!.FullName)!.FullName,
            "snapshots");
        string otherSnapshot = Path.Combine(snapshotsDirectory, new string('c', 40), "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(otherSnapshot)!);
        await File.WriteAllBytesAsync(otherSnapshot, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used when another revision holds the model.")));

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            TestComponent(),
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        // The snapshot link is this run's creation even though its content is not.
        Assert.True(result.CreatedThisRun);
        Assert.Equal(modelPath, result.ModelPath);
        Assert.True(File.Exists(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.True(File.Exists(otherSnapshot));
    }

    [Fact]
    public async Task ModelInstall_IgnoresBlobWithWrongDigestAndDownloads()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string repositoryDirectory = Directory.GetParent(
            Directory.GetParent(Path.GetDirectoryName(modelPath)!)!.FullName)!.FullName;
        string blobPath = Path.Combine(repositoryDirectory, "blobs", model.Weights.Sha256.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, "corrupted-model"u8.ToArray());
        using var client = new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(modelBytes),
        }));

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            TestComponent(),
            model,
            progress: null,
            CancellationToken.None);

        // The stale blob is left untouched; the download is written at the pinned path.
        Assert.Equal(HuggingFaceModelInstallDisposition.Downloaded, result.Disposition);
        Assert.Equal(modelPath, result.ModelPath);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal("corrupted-model"u8.ToArray(), await File.ReadAllBytesAsync(blobPath));
    }

    [Fact]
    public async Task ModelInstall_SecondInstallOfSameRevisionDownloadsOnlyOnce()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) };
        }));
        var installer = new HuggingFaceModelInstaller(client);

        HuggingFaceModelInstallResult first = await installer.InstallAsync(
            temp.Path, TestComponent(), model, progress: null, CancellationToken.None);
        HuggingFaceModelInstallResult second = await installer.InstallAsync(
            temp.Path, TestComponent(), model, progress: null, CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(HuggingFaceModelInstallDisposition.Downloaded, first.Disposition);
        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, second.Disposition);
        Assert.False(second.CreatedThisRun);
        Assert.Equal(modelPath, second.ModelPath);
        Assert.Equal(cacheRoot, second.CacheRoot);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
    }

    [Fact]
    public async Task ModelInstall_NewRevisionOfDownloadedModelIsLinkedWithoutDownloadingAgain()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo firstRevision = CreateModel(modelBytes);
        LocalModelInfo secondRevision = CreateModel(modelBytes, new string('c', 40));
        (string firstPath, _) = ResolveModelPaths(cacheRoot, firstRevision);
        (string secondPath, _) = ResolveModelPaths(cacheRoot, secondRevision);
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) };
        }));
        var installer = new HuggingFaceModelInstaller(client);

        await installer.InstallAsync(
            temp.Path, TestComponent(), firstRevision, progress: null, CancellationToken.None);
        HuggingFaceModelInstallResult reused = await installer.InstallAsync(
            temp.Path, TestComponent(), secondRevision, progress: null, CancellationToken.None);

        // The pinned bytes are identical, so the second revision becomes a hard link to
        // content already on disk: one download, two snapshot paths, one copy of the data.
        Assert.Equal(1, requests);
        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, reused.Disposition);
        Assert.Equal(secondPath, reused.ModelPath);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(secondPath));
        Assert.Null(new FileInfo(secondPath).LinkTarget);
    }

    [Fact]
    public async Task ModelInstall_RollbackRemovesReusedLinkButKeepsTheBlob()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string blobPath = Path.Combine(RepositoryDirectory(modelPath), "blobs", model.Weights.Sha256.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used when the pinned blob is present.")));
        var installer = new HuggingFaceModelInstaller(client);

        HuggingFaceModelInstallResult reused = await installer.InstallAsync(
            temp.Path, TestComponent(), model, progress: null, CancellationToken.None);
        installer.RemoveInstalledModel(temp.Path, reused);

        // The link is this run's creation, so rollback must remove it -- and removing a
        // hard link never removes the content the pre-existing blob still names.
        Assert.True(reused.CreatedThisRun);
        Assert.False(File.Exists(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(blobPath));
    }

    [Fact]
    public async Task ModelInstall_FailedDownloadLeavesNoEmptySnapshotDirectory()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalModelInfo model = CreateModel("verified-model"u8.ToArray());
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            new HuggingFaceModelInstaller(client, (_, _) => Task.CompletedTask).InstallAsync(
                temp.Path, TestComponent(), model, progress: null, CancellationToken.None));

        // This cache is shared with huggingface_hub and llama.cpp: a failed install must
        // not leave a bogus empty revision behind for a cache scan to report.
        Assert.False(Directory.Exists(Path.GetDirectoryName(modelPath)));
    }

    [Fact]
    public async Task ModelInstall_ReportsVerificationProgressSeparatelyFromDownload()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, modelBytes);
        var phases = new List<HuggingFaceModelInstallPhase>();
        var progress = new SynchronousProgress<HuggingFaceModelInstallProgress>(value => phases.Add(value.Phase));
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for an already verified model.")));

        await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path, TestComponent(), model, progress, CancellationToken.None);

        // Hashing tens of gigabytes is otherwise silent, which reads as a frozen setup.
        Assert.NotEmpty(phases);
        Assert.All(phases, phase => Assert.Equal(HuggingFaceModelInstallPhase.Verifying, phase));
    }

    [SymbolicLinkFact]
    public async Task ModelInstall_ReplacesPinnedSnapshotLinkWhoseBlobFailsVerification()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        (string modelPath, _) = ResolveModelPaths(cacheRoot, model);
        string blobPath = Path.Combine(RepositoryDirectory(modelPath), "blobs", new string('e', 64));
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(blobPath, "corrupted-model"u8.ToArray());
        SymbolicLinkSupport.CreateSymbolicLink(
            modelPath,
            Path.GetRelativePath(Path.GetDirectoryName(modelPath)!, blobPath));
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) }));

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path, TestComponent(), model, progress: null, CancellationToken.None);

        // Refusing to unlink huggingface_hub's own pointer would strand setup forever on
        // a bad blob. Only the link is replaced; the blob stays for its owner.
        Assert.Equal(HuggingFaceModelInstallDisposition.Downloaded, result.Disposition);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Null(new FileInfo(modelPath).LinkTarget);
        Assert.Equal("corrupted-model"u8.ToArray(), await File.ReadAllBytesAsync(blobPath));
    }

    [Fact]
    public async Task RuntimeInstall_ReplacesExactUnclaimedCatalogDirectory()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, component, out LocalAiSetupPaths paths, out _));
        Directory.CreateDirectory(paths.InstallDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.InstallDirectory, "orphan.txt"), "orphan");
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult result = await installer.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.True(result.CreatedThisRun);
        Assert.False(File.Exists(Path.Combine(paths.InstallDirectory, "orphan.txt")));
        Assert.True(File.Exists(Path.Combine(paths.InstallDirectory, "llama-server.exe")));
    }

    [Fact]
    public async Task RuntimeInstall_FollowsOnlyApprovedGithubReleaseRedirects()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        var observedHosts = new List<string>();
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            observedHosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(
                    $"https://release-assets.githubusercontent.com{request.RequestUri.AbsolutePath}");
                return redirect;
            }

            byte[] bytes = request.RequestUri.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        await installer.InstallAsync(temp.Path, runtime, progress: null, CancellationToken.None);

        Assert.Equal(
            ["github.com", "release-assets.githubusercontent.com", "github.com", "release-assets.githubusercontent.com"],
            observedHosts);
    }

    [Fact]
    public async Task RuntimeInstall_RejectsRedirectToUntrustedHostBeforeRequest()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        var requestCount = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requestCount++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://example.invalid/runtime.zip");
            return redirect;
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        LocalAiArtifactInstallException exception = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(
            () => installer.InstallAsync(temp.Path, runtime, progress: null, CancellationToken.None));

        Assert.Contains("untrusted host", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ArtifactInstall_RejectsPinnedZipUnsafeEntryNameWithoutWritingOutsideRoot()
    {
        using var temp = new TempDirectory();
        byte[] archiveBytes = CreateZip(
            ("safe.txt", "safe"u8.ToArray()),
            ("../../../outside.txt", "outside"u8.ToArray()));
        var archive = new LocalAiPinnedArchive(
            "runtime.zip",
            new Uri("https://github.com/owner/repo/releases/download/v1/runtime.zip"),
            archiveBytes.Length,
            Sha256(archiveBytes));
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes),
            }));
        var extractionProgress = new List<LocalAiArtifactInstallProgress>();
        var installer = new LocalAiArtifactInstaller(client);
        installer.ProgressChanged += (_, value) => extractionProgress.Add(value);
        string outsidePath = Path.Combine(temp.Path, "outside.txt");

        LocalAiArtifactInstallException exception =
            await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
                installer.InstallAsync(
                    temp.Path,
                    TestComponent(),
                    [archive],
                    progress: null,
                    CancellationToken.None));

        Assert.Contains("unsafe path segment", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            extractionProgress,
            value => value.Phase == LocalAiArtifactInstallPhase.Extracting && value.Completed == 1);
        Assert.False(File.Exists(outsidePath));
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            TestComponent(),
            out LocalAiSetupPaths paths,
            out string pathError), pathError);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "safe.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));
    }

    [Theory]
    [InlineData((int)FileAttributes.ReparsePoint, "reparse point")]
    [InlineData(unchecked((int)0xA1FF0000), "symbolic link")]
    public async Task ArtifactInstall_RejectsLinkEntryAndCleansEarlierExtraction(
        int externalAttributes,
        string expectedError)
    {
        using var temp = new TempDirectory();
        byte[] archiveBytes = CreateZip(
            ("safe.txt", "safe"u8.ToArray(), 0),
            ("linked.txt", "target.txt"u8.ToArray(), externalAttributes));
        var archive = new LocalAiPinnedArchive(
            "runtime.zip",
            new Uri("https://github.com/owner/repo/releases/download/v1/runtime.zip"),
            archiveBytes.Length,
            Sha256(archiveBytes));
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes),
            }));
        var extractionProgress = new List<LocalAiArtifactInstallProgress>();
        var installer = new LocalAiArtifactInstaller(client);
        installer.ProgressChanged += (_, value) => extractionProgress.Add(value);

        LocalAiArtifactInstallException exception =
            await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
                installer.InstallAsync(
                    temp.Path,
                    TestComponent(),
                    [archive],
                    progress: null,
                    CancellationToken.None));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            extractionProgress,
            value => value.Phase == LocalAiArtifactInstallPhase.Extracting && value.Completed == 1);
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            TestComponent(),
            out LocalAiSetupPaths paths,
            out string pathError), pathError);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "safe.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    public void ArchiveDestination_RejectsTraversalWithEitherSeparator(string entryName)
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            entryName,
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsCanonicalizedPrefixCollision()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            "../staging-sibling/outside.txt",
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsRootedPath()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");
        string rootedEntry = Path.Combine(
            Path.GetPathRoot(temp.Path)!,
            $"openclaw-rooted-{Guid.NewGuid():N}.txt");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            rootedEntry,
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsDescendantJunction()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(stagingDirectory);
        string junction = Path.Combine(stagingDirectory, "linked");
        CreateJunction(junction, outside.Path);
        try
        {
            bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                stagingDirectory,
                "linked/outside.txt",
                out string destinationPath,
                out string error);

            Assert.False(resolved);
            Assert.Empty(destinationPath);
            Assert.Contains("reparse point", error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        }
    }

    [Fact]
    public void ArchiveDestination_ResolvesValidNestedEntry()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            "bin/tools/llama-server.exe",
            out string destinationPath,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(stagingDirectory, "bin", "tools", "llama-server.exe")),
            destinationPath);
        Assert.Empty(error);
    }

    [Fact]
    public async Task Reconciler_ReusesOnlyMatchingManifestWithoutMutation()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, cacheRoot, plan, gpuId);
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        byte[] original = await File.ReadAllBytesAsync(paths.ManifestPath);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None);

        Assert.True(result.Reused);
        Assert.False(result.RuntimeInstall!.CreatedThisRun);
        Assert.False(result.ModelInstall!.CreatedThisRun);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.ManifestPath));
    }

    [Fact]
    public async Task Reconciler_MigratesLegacyCudaPrefixedUuidSelector()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalInferencePlan plan = CatalogPlan();
        const string gpuUuid = "GPU-cc66bca6-b5ff-dd70-995c-d81a07add980";
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(CreateManifest(temp.Path, cacheRoot, plan, $"cuda:{gpuUuid}"));
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuUuid,
            CancellationToken.None);

        LocalAiResolvedInstall migrated = Assert.IsType<LocalAiResolvedInstall>(
            await store.LoadAsync());
        Assert.True(result.Reused);
        Assert.Equal(gpuUuid, result.ResolvedInstall?.Manifest.SelectedGpuId);
        Assert.Equal(gpuUuid, migrated.Manifest.SelectedGpuId);
        Assert.Equal(
            gpuUuid,
            LlamaServerRouterConfiguration.Build(paths, migrated)
                .Environment["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public async Task Reconciler_UpgradesLegacyManifestAndKeepsTheDownloadedWeights()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        var paths = new LocalAiPaths(temp.Path);
        var source = Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
        string legacyModelPath = WriteLegacySchemaThreeManifest(
            paths,
            CreateManifest(temp.Path, cacheRoot, plan, gpuId),
            source,
            "already downloaded weights"u8.ToArray());
        (string hubModelPath, _) = ResolveModelPaths(cacheRoot, plan.Model);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None);

        // The weights are tens of gigabytes and already on disk: the upgrade must relocate
        // them into the hub cache, never force the identical bytes to be downloaded again.
        Assert.True(result.Reused);
        Assert.Equal(hubModelPath, result.ResolvedInstall!.ModelPath);
        Assert.Equal(cacheRoot, result.ResolvedInstall.Manifest.ModelCacheRoot);
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            result.ResolvedInstall.Manifest.SchemaVersion);
        Assert.Equal("already downloaded weights"u8.ToArray(), await File.ReadAllBytesAsync(hubModelPath));
        Assert.False(File.Exists(legacyModelPath));
    }

    [Fact]
    public async Task Reconciler_LeavesLegacyManifestAloneWhenItsWeightsAreGone()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalInferencePlan plan = CatalogPlan();
        var paths = new LocalAiPaths(temp.Path);
        var source = Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
        string legacyModelPath = WriteLegacySchemaThreeManifest(
            paths,
            CreateManifest(temp.Path, cacheRoot, plan, "GPU-0"),
            source,
            weights: null);
        string manifestBefore = await File.ReadAllTextAsync(paths.ManifestPath);

        // Nothing can be salvaged, so the manifest is left untouched for the normal
        // fail-closed validation to reject rather than half-migrated.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiInstallReconciler(new ValidRuntimeInspector(), new AcceptingModelVerifier())
                .ReconcileAsync(temp.Path, plan, "GPU-0", CancellationToken.None));

        Assert.False(File.Exists(legacyModelPath));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(paths.ManifestPath));
    }

    [Fact]
    public async Task Reconciler_RejectsDifferentGpuWithoutDeletingManifest()
    {
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalInferencePlan plan = CatalogPlan();
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(CreateManifest(temp.Path, cacheRoot, plan, "GPU-0"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiInstallReconciler(new ValidRuntimeInspector(), new AcceptingModelVerifier())
                .ReconcileAsync(temp.Path, plan, "GPU-1", CancellationToken.None));

        Assert.True(File.Exists(paths.ManifestPath));
    }

    [Fact]
    public async Task FreshProcessUninstall_RemovesCanonicalLocalAiRoot()
    {
        using var temp = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "engines", "runtime"));
        await File.WriteAllTextAsync(Path.Combine(root, "state.json"), "corrupt but app-owned");
        await File.WriteAllTextAsync(Path.Combine(root, "engines", "runtime", "file.bin"), "data");
        SetupContext context = CreateContext(temp.Path, confirmDestructive: true);

        PipelineResult result = await new SetupPipeline([new PersistLocalAiManifestStep()])
            .UninstallAsync(context);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task FreshProcessUninstall_RejectsDescendantJunctionBeforeDeletingAnything()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(root);
        string retained = Path.Combine(root, "retained.txt");
        string outsideFile = Path.Combine(outside.Path, "outside.txt");
        await File.WriteAllTextAsync(retained, "retain");
        await File.WriteAllTextAsync(outsideFile, "outside");
        string junction = Path.Combine(root, "linked");
        CreateJunction(junction, outside.Path);
        try
        {
            PipelineResult result = await new SetupPipeline([new PersistLocalAiManifestStep()])
                .UninstallAsync(CreateContext(temp.Path, confirmDestructive: true));

            Assert.Equal(PipelineOutcome.Failed, result.Outcome);
            Assert.True(File.Exists(retained));
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task NormalRollback_DoesNotRemoveExistingLocalAiRoot()
    {
        using var temp = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(root);
        string retained = Path.Combine(root, "retained.txt");
        await File.WriteAllTextAsync(retained, "retain");
        SetupContext context = CreateContext(temp.Path, confirmDestructive: false);

        await new PersistLocalAiManifestStep().RollbackAsync(context, CancellationToken.None);

        Assert.True(File.Exists(retained));
    }

    private static SetupContext CreateContext(string localDataDirectory, bool confirmDestructive)
    {
        var config = new SetupConfig { ConfirmDestructive = confirmDestructive };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            dataDir: Path.Combine(localDataDirectory, "roaming"),
            localDataDir: localDataDirectory);
    }

    private static LocalInferencePlan CatalogPlan()
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.X64)!;
        return new LocalInferencePlan(
            runtime,
            LocalModelCatalog.Default,
            LocalModelCatalog.GetProfiles(LocalModelCatalog.Default)[0],
            LocalInferenceModelSelectionOrigin.Default);
    }

    private static LocalAiInstallManifest CreateManifest(
        string localDataDirectory,
        string cacheRoot,
        LocalInferencePlan plan,
        string gpuId)
    {
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(
            localDataDirectory,
            component,
            out LocalAiSetupPaths setupPaths,
            out string error), error);
        var paths = new LocalAiPaths(localDataDirectory);
        var source = Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            source.RepositoryId,
            source.RevisionSha,
            plan.Model.Weights.RelativePath,
            out string modelPath,
            out _,
            out error), error);
        string executable = Path.Combine(setupPaths.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName);
        return new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = "x64",
            RuntimeId = plan.Runtime.Id,
            ModelCatalogId = plan.Model.Id,
            SelectedGpuId = gpuId,
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory, executable),
            RuntimeAssets = plan.Runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
            ModelPath = modelPath,
            ModelCacheRoot = cacheRoot,
            ModelId = $"{source.RepositoryId}@{source.RevisionSha}",
            ModelAlias = plan.Model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(plan.Model.Weights.RelativePath),
                SourceUrl = plan.Model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = plan.Model.Weights.SizeBytes,
                Sha256 = plan.Model.Weights.Sha256.Value,
            },
            Endpoint = "http://127.0.0.1:18803/v1",
            ContextLength = plan.Profile.ContextTokens,
            KeyCachePrecision = plan.Profile.KeyCachePrecision,
            ValueCachePrecision = plan.Profile.ValueCachePrecision,
            DraftKeyCachePrecision = plan.Profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = plan.Profile.DraftValueCachePrecision,
        };
    }

    private static LocalModelInfo CreateModel(byte[] bytes, string? revisionSha = null)
    {
        var source = new HuggingFaceRevisionSource("owner/repo", revisionSha ?? new string('a', 40));
        var artifact = new PinnedArtifact(
            "test-model",
            ArtifactRole.ModelWeights,
            source,
            "model.gguf",
            bytes.Length,
            new Sha256Digest(Sha256(bytes)));
        return new LocalModelInfo(
            "test-model",
            "Test model",
            "Test",
            "Q4",
            artifact,
            new LocalModelRunRecipe(
                128,
                128,
                1,
                1,
                1,
                128,
                true,
                true,
                SpeculativeDecodingMode.DraftMtp,
                1,
                new ModelSamplingPreset(0.6, 20, 0.95, 0, 1, 0)),
            IsDefault: true,
            IsExplicitAlternative: false,
            SupportsVision: false);
    }

    private static LlamaRuntimeVariant CreateRuntime(byte[] binaryZip, byte[] dependencyZip)
    {
        var source = new GitHubReleaseSource("owner/repo", "v1", new string('b', 40));
        return new LlamaRuntimeVariant(
            "test-runtime",
            Architecture.X64,
            new Version(13, 0),
            [
                new PinnedArtifact(
                    "test-runtime-bin",
                    ArtifactRole.RuntimeBinary,
                    source,
                    "runtime.zip",
                    binaryZip.Length,
                    new Sha256Digest(Sha256(binaryZip))),
                new PinnedArtifact(
                    "test-runtime-dep",
                    ArtifactRole.RuntimeDependency,
                    source,
                    "dependency.zip",
                    dependencyZip.Length,
                    new Sha256Digest(Sha256(dependencyZip))),
            ]);
    }

    /// <summary>
    /// Rewrites a current manifest into the schema-3 shape that shipped before models
    /// moved into the shared hub cache: no recorded cache root, and a model path
    /// relative to the Local AI root. Optionally materializes the legacy weights file.
    /// Returns the absolute legacy model path.
    /// </summary>
    private static string WriteLegacySchemaThreeManifest(
        LocalAiPaths paths,
        LocalAiInstallManifest manifest,
        HuggingFaceRevisionSource source,
        byte[]? weights)
    {
        string relativeModelPath = Path.Combine(
            "models",
            source.RepositoryId.Split('/')[0],
            source.RepositoryId.Split('/')[1],
            source.RevisionSha,
            manifest.ModelAsset.FileName);
        string legacyModelPath = Path.Combine(paths.RootDirectory, relativeModelPath);
        if (weights is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
            File.WriteAllBytes(legacyModelPath, weights);
        }

        var json = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["engine"] = manifest.Engine,
            ["engineVersion"] = manifest.EngineVersion,
            ["architecture"] = manifest.Architecture,
            ["runtimeId"] = manifest.RuntimeId,
            ["modelCatalogId"] = manifest.ModelCatalogId,
            ["selectedGpuId"] = manifest.SelectedGpuId,
            ["executablePath"] = manifest.ExecutablePath,
            ["runtimeAssets"] = new JsonArray(
                manifest.RuntimeAssets.Select(asset => (JsonNode)new JsonObject
                {
                    ["fileName"] = asset.FileName,
                    ["sourceUrl"] = asset.SourceUrl,
                    ["sizeBytes"] = asset.SizeBytes,
                    ["sha256"] = asset.Sha256,
                }).ToArray()),
            ["modelPath"] = relativeModelPath,
            ["modelId"] = manifest.ModelId,
            ["modelAlias"] = manifest.ModelAlias,
            ["modelAsset"] = new JsonObject
            {
                ["fileName"] = manifest.ModelAsset.FileName,
                ["sourceUrl"] = manifest.ModelAsset.SourceUrl,
                ["sizeBytes"] = manifest.ModelAsset.SizeBytes,
                ["sha256"] = manifest.ModelAsset.Sha256,
            },
            ["requestedPort"] = manifest.RequestedPort,
            ["endpoint"] = manifest.Endpoint,
            ["contextLength"] = manifest.ContextLength,
            ["keyCachePrecision"] = manifest.KeyCachePrecision.ToString().ToLowerInvariant(),
            ["valueCachePrecision"] = manifest.ValueCachePrecision.ToString().ToLowerInvariant(),
            ["draftKeyCachePrecision"] = manifest.DraftKeyCachePrecision.ToString().ToLowerInvariant(),
            ["draftValueCachePrecision"] = manifest.DraftValueCachePrecision.ToString().ToLowerInvariant(),
            ["installedAtUtc"] = manifest.InstalledAtUtc,
        };
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(paths.ManifestPath, json.ToJsonString());
        return legacyModelPath;
    }

    private static string RepositoryDirectory(string modelPath) =>
        Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(modelPath)!)!.FullName)!.FullName;

    private static (string ModelPath, string PartialPath) ResolveModelPaths(
        string cacheRoot,
        LocalModelInfo model)
    {
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out string modelPath,
            out string partialPath,
            out string error), error);
        return (modelPath, partialPath);
    }

    private static LocalAiComponentIdentity TestComponent() =>
        new("llama-server", "v1", "win-x64");

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateZip(params (string Name, byte[] Content, int ExternalAttributes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content, int externalAttributes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                entry.ExternalAttributes = externalAttributes;
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start mklink.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class ThrowAfterPrefixStream(byte[] bytes, int prefixLength) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefixLength)
                throw new IOException("Simulated interrupted response body.");
            int length = Math.Min(Math.Min(count, prefixLength - _position), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= prefixLength)
                return ValueTask.FromException<int>(new IOException("Simulated interrupted response body."));
            int length = Math.Min(Math.Min(buffer.Length, prefixLength - _position), bytes.Length - _position);
            bytes.AsMemory(_position, length).CopyTo(buffer);
            _position += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class ValidRuntimeInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LlamaRuntimeInspection(true, "valid", null));
    }

    private sealed class AcceptingModelVerifier : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyAsync(
            string cacheRoot,
            string path,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenClawLocalAiRecoveryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
