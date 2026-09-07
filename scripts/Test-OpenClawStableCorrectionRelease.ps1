<#
.SYNOPSIS
    Validates that a numeric stable correction tag may be published as an
    OpenClaw Windows Hub release.

.DESCRIPTION
    Windows Hub release tags are an independent version domain. A correction is
    accepted only when it stays on the current Windows latest release line and
    strictly advances that line's numeric correction:

      current v2026.7.1    + candidate v2026.7.1-1 -> accepted
      current v2026.7.1-2  + candidate v2026.7.1-3 -> accepted
      current v2026.7.1-2  + candidate v2026.7.1-2 -> rejected (tag reuse)
      current v2026.7.1-2  + candidate v2026.7.1-1 -> rejected (backwards)
      current v2026.7.1-2  + candidate v2026.8.0-1 -> rejected (other line)

    The validator deliberately has no dependency on any other repository's
    release API. Online runs also refuse a candidate tag that already has a
    published Windows release, and refuse to order against a draft, prerelease,
    or unpublished latest release. Pass -CurrentWindowsTag to evaluate the
    ordering rule offline.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)-[1-9]\d*$')]
    [string]$Tag,

    [string]$GitHubToken = $env:GITHUB_TOKEN,

    # Deterministic/offline ordering input. When supplied, the current Windows
    # latest release tag is not fetched from the GitHub API.
    [string]$CurrentWindowsTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-StableReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match(
        $Value,
        '^v?(?<year>0|[1-9]\d*)\.(?<month>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<revision>[1-9]\d*))?$')
    if (-not $match.Success) {
        throw "Stable release version '$Value' has an unsupported format."
    }

    return @(
        [long]$match.Groups["year"].Value,
        [long]$match.Groups["month"].Value,
        [long]$match.Groups["patch"].Value,
        $(if ($match.Groups["revision"].Success) {
            [long]$match.Groups["revision"].Value
        } else {
            0L
        })
    )
}

function Get-StableReleaseBaseVersion {
    param([Parameter(Mandatory)][object[]]$Parts)

    return "$($Parts[0]).$($Parts[1]).$($Parts[2])"
}

function Assert-SameLineStableCorrection {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Current
    )

    $candidateParts = ConvertTo-StableReleaseVersion $Candidate
    $currentParts = ConvertTo-StableReleaseVersion $Current
    $candidateBase = Get-StableReleaseBaseVersion $candidateParts
    $currentBase = Get-StableReleaseBaseVersion $currentParts

    if ($candidateBase -ne $currentBase) {
        throw "Stable correction '$Candidate' must correct the current Windows latest release line '$currentBase', but targets '$candidateBase'."
    }

    if ($candidateParts[3] -eq $currentParts[3]) {
        throw "Stable correction '$Candidate' is already the current Windows release '$Current'; a published tag must never be moved or reused."
    }

    if ($candidateParts[3] -lt $currentParts[3]) {
        throw "Stable correction '$Candidate' does not advance current Windows release '$Current'."
    }
}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "openclaw-windows-node-release"
    "X-GitHub-Api-Version" = "2022-11-28"
}
if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
    $headers.Authorization = "Bearer $GitHubToken"
}

function Get-ExceptionHttpStatus {
    param([Parameter(Mandatory)][object]$Exception)

    $response = $null
    if ($null -ne $Exception.PSObject.Properties["Response"]) {
        $response = $Exception.Response
    }
    if ($null -eq $response -or
        $null -eq $response.PSObject.Properties["StatusCode"]) {
        return $null
    }

    return [int]$response.StatusCode
}

function Assert-WindowsReleaseTagUnpublished {
    param(
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][hashtable]$RequestHeaders
    )

    try {
        Invoke-RestMethod `
            -Headers $RequestHeaders `
            -Uri "https://api.github.com/repos/openclaw/openclaw-windows-node/releases/tags/$ReleaseTag" |
            Out-Null
    } catch {
        $status = Get-ExceptionHttpStatus -Exception $_.Exception
        if ($status -eq 404) {
            return
        }

        throw "Could not determine whether Windows release '$ReleaseTag' already exists: $($_.Exception.Message)"
    }

    throw "Stable correction '$ReleaseTag' is already a published Windows release; a published tag must never be moved or reused."
}

function Get-CurrentWindowsReleaseTag {
    param([Parameter(Mandatory)][hashtable]$RequestHeaders)

    $release = Invoke-RestMethod `
        -Headers $RequestHeaders `
        -Uri "https://api.github.com/repos/openclaw/openclaw-windows-node/releases/latest"

    $tagName = $null
    if ($null -ne $release -and
        $null -ne $release.PSObject.Properties["tag_name"]) {
        $tagName = $release.tag_name
    }
    if ([string]::IsNullOrWhiteSpace($tagName)) {
        throw "Could not resolve the current openclaw/openclaw-windows-node latest release tag."
    }

    if ($release.PSObject.Properties["draft"] -and $release.draft) {
        throw "Current Windows latest release '$tagName' is a draft; refusing to order a correction against it."
    }
    if ($release.PSObject.Properties["prerelease"] -and $release.prerelease) {
        throw "Current Windows latest release '$tagName' is a prerelease; refusing to order a correction against it."
    }
    if ($null -eq $release.PSObject.Properties["published_at"] -or
        $null -eq $release.published_at) {
        throw "Current Windows latest release '$tagName' is not published; refusing to order a correction against it."
    }

    return $tagName
}

$currentTag = $CurrentWindowsTag
if ([string]::IsNullOrWhiteSpace($currentTag)) {
    Assert-WindowsReleaseTagUnpublished -ReleaseTag $Tag -RequestHeaders $headers
    $currentTag = Get-CurrentWindowsReleaseTag -RequestHeaders $headers
}

Assert-SameLineStableCorrection `
    -Candidate $Tag `
    -Current $currentTag

$parsedTag = ConvertTo-StableReleaseVersion $Tag
$parsedCurrent = ConvertTo-StableReleaseVersion $currentTag
[pscustomobject]@{
    Tag = $Tag
    BaseVersion = Get-StableReleaseBaseVersion $parsedTag
    Correction = $parsedTag[3]
    CurrentWindowsTag = $currentTag
    CurrentCorrection = $parsedCurrent[3]
}
