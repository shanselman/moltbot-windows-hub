using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class HuggingFaceHubCacheTests
{
    [Fact]
    public void ResolveCacheRoot_PrefersHfHubCacheOverEverything()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HF_HUB_CACHE" => @"C:\explicit\hub-cache",
            "HUGGINGFACE_HUB_CACHE" => @"C:\legacy\hub-cache",
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(@"C:\explicit\hub-cache", root);
    }

    [Fact]
    public void ResolveCacheRoot_FallsBackToLegacyHuggingFaceHubCache()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HUGGINGFACE_HUB_CACHE" => @"C:\legacy\hub-cache",
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(@"C:\legacy\hub-cache", root);
    }

    [Fact]
    public void ResolveCacheRoot_FallsBackToHfHomeSlashHub()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(name => name switch
        {
            "HF_HOME" => @"C:\hf-home",
            _ => null,
        });

        Assert.Equal(Path.Combine(@"C:\hf-home", "hub"), root);
    }

    [Fact]
    public void ResolveCacheRoot_DefaultsUnderUserProfile()
    {
        string root = HuggingFaceHubCache.ResolveCacheRoot(_ => null);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "huggingface",
            "hub");
        Assert.Equal(expected, root);
    }

    [Fact]
    public void TryGetSnapshotPaths_MatchesStandardHubCacheLayout()
    {
        using var temp = new TempDirectory();

        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            temp.Path,
            "unsloth/Qwen3.8-27B-GGUF",
            new string('a', 40),
            "model.gguf",
            out string modelPath,
            out string partialPath,
            out string error);

        Assert.True(resolved, error);
        string expectedDirectory = Path.Combine(
            temp.Path,
            "models--unsloth--Qwen3.8-27B-GGUF",
            "snapshots",
            new string('a', 40));
        Assert.Equal(Path.Combine(expectedDirectory, "model.gguf"), modelPath);
        Assert.Equal(Path.Combine(expectedDirectory, "model.gguf.partial"), partialPath);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    public void TryGetSnapshotPaths_RejectsMalformedRepositoryId(string repositoryId)
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            repositoryId,
            new string('a', 40),
            "model.gguf",
            out string modelPath,
            out string partialPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(modelPath);
        Assert.Empty(partialPath);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetSnapshotPaths_RejectsNonGgufFileName()
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 40),
            "model.bin",
            out _,
            out _,
            out string error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryGetSnapshotPaths_RejectsShortRevision()
    {
        bool resolved = HuggingFaceHubCache.TryGetSnapshotPaths(
            @"C:\cache",
            "owner/repo",
            new string('a', 39),
            "model.gguf",
            out _,
            out _,
            out string error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryValidateManagedPath_AcceptsPathContainedWithinCacheRoot()
    {
        using var temp = new TempDirectory();
        string candidate = Path.Combine(temp.Path, "models--owner--repo", "snapshots", new string('a', 40), "model.gguf");

        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            temp.Path,
            candidate,
            out string validatedPath,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal(candidate, validatedPath);
    }

    [Fact]
    public void TryValidateManagedPath_RejectsPathOutsideCacheRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string candidate = Path.Combine(outside.Path, "model.gguf");

        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            temp.Path,
            candidate,
            out string validatedPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(validatedPath);
        Assert.Contains("not contained within", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateManagedPath_RejectsRelativePath()
    {
        bool resolved = HuggingFaceHubCache.TryValidateManagedPath(
            @"C:\cache",
            Path.Combine("models--owner--repo", "snapshots", new string('a', 40), "model.gguf"),
            out string validatedPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(validatedPath);
        Assert.Contains("fully qualified", error, StringComparison.Ordinal);
    }

    [SymbolicLinkFact]
    public void TryValidateSnapshotReadPath_AcceptsStandardRepositoryBlobSymlink()
    {
        using var temp = new TempDirectory();
        string repository = Path.Combine(temp.Path, "models--owner--repo");
        string blobs = Path.Combine(repository, "blobs");
        string snapshot = Path.Combine(repository, "snapshots", new string('a', 40));
        string blob = Path.Combine(blobs, new string('b', 64));
        string pointer = Path.Combine(snapshot, "model.gguf");
        Directory.CreateDirectory(blobs);
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(blob, "verified model");
        SymbolicLinkSupport.CreateSymbolicLink(pointer, Path.GetRelativePath(snapshot, blob));

        bool readable = HuggingFaceHubCache.TryValidateSnapshotReadPath(
            temp.Path,
            pointer,
            out string validatedPath,
            out string readError);
        bool writable = HuggingFaceHubCache.TryValidateManagedPath(
            temp.Path,
            pointer,
            out _,
            out string writeError);

        Assert.True(readable, readError);
        Assert.Equal(pointer, validatedPath);
        Assert.False(writable);
        Assert.Contains("reparse point", writeError, StringComparison.OrdinalIgnoreCase);
    }

    [SymbolicLinkFact]
    public void TryValidateSnapshotReadPath_RejectsSymlinkOutsideRepositoryBlobs()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string repository = Path.Combine(temp.Path, "models--owner--repo");
        string blobs = Path.Combine(repository, "blobs");
        string snapshot = Path.Combine(repository, "snapshots", new string('a', 40));
        string outsideModel = Path.Combine(outside.Path, "model.gguf");
        string pointer = Path.Combine(snapshot, "model.gguf");
        Directory.CreateDirectory(blobs);
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(outsideModel, "untrusted model");
        SymbolicLinkSupport.CreateSymbolicLink(pointer, outsideModel);

        bool readable = HuggingFaceHubCache.TryValidateSnapshotReadPath(
            temp.Path,
            pointer,
            out string validatedPath,
            out string error);

        Assert.False(readable);
        Assert.Empty(validatedPath);
        Assert.Contains("outside the hub cache root", error, StringComparison.Ordinal);
    }

    [SymbolicLinkFact]
    public void TryValidateSnapshotReadPath_RejectsSymlinkIntoSiblingRepositoryBlobs()
    {
        using var temp = new TempDirectory();
        string repository = Path.Combine(temp.Path, "models--owner--repo");
        string repositoryBlobs = Path.Combine(repository, "blobs");
        string snapshot = Path.Combine(repository, "snapshots", new string('a', 40));
        string siblingBlobs = Path.Combine(temp.Path, "models--owner--other", "blobs");
        string siblingBlob = Path.Combine(siblingBlobs, new string('b', 64));
        string pointer = Path.Combine(snapshot, "model.gguf");
        Directory.CreateDirectory(repositoryBlobs);
        Directory.CreateDirectory(snapshot);
        Directory.CreateDirectory(siblingBlobs);
        File.WriteAllText(siblingBlob, "wrong repository");
        SymbolicLinkSupport.CreateSymbolicLink(pointer, siblingBlob);

        bool readable = HuggingFaceHubCache.TryValidateSnapshotReadPath(
            temp.Path,
            pointer,
            out string validatedPath,
            out string error);

        Assert.False(readable);
        Assert.Empty(validatedPath);
        Assert.Contains("its repository blobs directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetReuseCandidates_FindsBlobNamedByPinnedDigest()
    {
        using var temp = new TempDirectory();
        string blob = Path.Combine(
            temp.Path,
            "models--owner--repo",
            "blobs",
            new string('b', 64));
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllText(blob, "verified model");

        bool resolved = HuggingFaceHubCache.TryGetReuseCandidates(
            temp.Path,
            "owner/repo",
            "model.gguf",
            new Sha256Digest(new string('b', 64)),
            out IReadOnlyList<string> candidates,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal([blob], candidates);
    }

    [Fact]
    public void TryGetReuseCandidates_FindsSameNamedSnapshotInAnyRevision()
    {
        using var temp = new TempDirectory();
        string pinnedSnapshot = Path.Combine(
            temp.Path,
            "models--owner--repo",
            "snapshots",
            new string('a', 40),
            "model.gguf");
        string otherSnapshot = Path.Combine(
            temp.Path,
            "models--owner--repo",
            "snapshots",
            new string('c', 40),
            "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(pinnedSnapshot)!);
        Directory.CreateDirectory(Path.GetDirectoryName(otherSnapshot)!);
        File.WriteAllText(otherSnapshot, "verified model");

        bool resolved = HuggingFaceHubCache.TryGetReuseCandidates(
            temp.Path,
            "owner/repo",
            "model.gguf",
            new Sha256Digest(new string('b', 64)),
            out IReadOnlyList<string> candidates,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal([otherSnapshot], candidates);
    }

    [Fact]
    public void TryGetReuseCandidates_IgnoresUnrelatedFilesAndMissingEntries()
    {
        using var temp = new TempDirectory();
        string repository = Path.Combine(temp.Path, "models--owner--repo");
        Directory.CreateDirectory(Path.Combine(repository, "blobs"));
        Directory.CreateDirectory(Path.Combine(repository, "snapshots", new string('c', 40)));
        File.WriteAllText(
            Path.Combine(repository, "blobs", new string('d', 64)),
            "unrelated blob");
        File.WriteAllText(
            Path.Combine(repository, "snapshots", new string('c', 40), "other.gguf"),
            "different file name");

        bool resolved = HuggingFaceHubCache.TryGetReuseCandidates(
            temp.Path,
            "owner/repo",
            "model.gguf",
            new Sha256Digest(new string('b', 64)),
            out IReadOnlyList<string> candidates,
            out string error);

        Assert.True(resolved, error);
        Assert.Empty(candidates);
    }

    [Fact]
    public void TryGetReuseCandidates_RejectsMalformedRepositoryId()
    {
        bool resolved = HuggingFaceHubCache.TryGetReuseCandidates(
            @"C:\cache",
            "owner",
            "model.gguf",
            new Sha256Digest(new string('b', 64)),
            out _,
            out string error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryCreateHardLink_LinksContentWithoutCopying()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "target.gguf");
        string link = Path.Combine(temp.Path, "link.gguf");
        File.WriteAllText(target, "verified model");

        bool created = HuggingFaceHubCache.TryCreateHardLink(link, target);

        Assert.True(created);
        Assert.Equal("verified model", File.ReadAllText(link));
        File.Delete(target);
        Assert.Equal("verified model", File.ReadAllText(link));
    }
}
