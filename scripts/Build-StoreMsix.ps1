<#
.SYNOPSIS
    Builds an unsigned, self-contained Microsoft Store MSIX for one architecture.

.DESCRIPTION
    Publishes OpenClaw.Tray.WinUI into a temporary work directory, then copies
    the resulting package to a deterministically named artifact and verifies it
    before returning.

    The package is left unsigned because Partner Center signs Store
    submissions. Local development packages are produced instead by
    build.ps1 -Msix Dev, which uses the certificate from
    scripts\setup-dev-msix-cert.ps1 and a separate side-by-side identity.

    src\OpenClaw.Tray.WinUI\Package.appxmanifest is the single source of truth
    for the release identity. The build fails when the produced package drifts
    from it, when the version does not end in .0, when more than one package is
    produced, or when required content is missing or forbidden content is
    present.

    An msix-metadata.json sidecar records the source commit, whether the tree
    was dirty, the package version, publisher, and the package SHA-256.

.PARAMETER Architecture
    Target architecture: x64 or arm64. Defaults to x64. The Store serves a
    separate package per architecture; upload both to one submission.

.PARAMETER Configuration
    Build configuration. Release is the only accepted value: Store
    certification rejects Debug binaries, so this script refuses to stamp a
    Debug build with the release identity and provenance sidecar.

.PARAMETER OutputDirectory
    Where to place the package and its metadata sidecar. Relative paths resolve
    against the repository root. Defaults to artifacts\msix\<architecture>,
    which is cleaned on each run. A caller-supplied directory is never deleted;
    the build fails if it already exists and is not empty.

.EXAMPLE
    .\scripts\Build-StoreMsix.ps1 -Architecture x64
    .\scripts\Build-StoreMsix.ps1 -Architecture arm64
    .\build.ps1 -Project WinUI -Msix Store
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    # Release-only by design. build.ps1 -Msix Store already forces Release, but this
    # script is a documented entry point on its own: accepting Debug here would let a
    # caller produce a locally verified, provenance-stamped package that Partner
    # Center rejects.
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $IsWindows only exists in PowerShell Core, and strict mode turns a bare read
# into a terminating error under Windows PowerShell 5.1.
$isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
$runningOnWindows = if ($isWindowsVariable) {
    [bool]$isWindowsVariable.Value
}
else {
    [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}
if (-not $runningOnWindows) {
    throw 'MSIX packaging requires Windows.'
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectDirectory = Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI'
$projectPath = Join-Path $projectDirectory 'OpenClaw.Tray.WinUI.csproj'
$sourceManifestPath = Join-Path $projectDirectory 'Package.appxmanifest'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Remove-DirectoryIfPresent {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([IO.Directory]::Exists($Path)) {
        [IO.Directory]::Delete($Path, $true)
    }
}

function Test-PackageVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $segments = @($Version.Split('.'))
    if ($segments.Count -ne 4) {
        throw "MSIX package version must contain four numeric components: $Version"
    }

    foreach ($segment in $segments) {
        [uint16]$value = 0
        if (-not [uint16]::TryParse($segment, [ref]$value)) {
            throw "Invalid MSIX package version component: $segment"
        }
    }

    # Partner Center and the Store reserve the revision component.
    if ($segments[3] -ne '0') {
        throw "MSIX package version must end in .0 for release packages: $Version"
    }
}

# The tracked manifest is the single source of truth for the release identity.
# A packaged build that drifts from it is a packaging bug, not a new identity.
[xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw
$expectedIdentityName = [string]$sourceManifest.Package.Identity.Name
$expectedPublisher = [string]$sourceManifest.Package.Identity.Publisher
if (
    [string]::IsNullOrWhiteSpace($expectedIdentityName) -or
    [string]::IsNullOrWhiteSpace($expectedPublisher)
) {
    throw "Could not read the release identity from $sourceManifestPath."
}

if ($OutputDirectory) {
    # Never recursively delete a caller-supplied path; it may hold unrelated files.
    $OutputDirectory = [IO.Path]::GetFullPath(
        [IO.Path]::Combine($repositoryRoot, $OutputDirectory))
    if ([IO.Directory]::Exists($OutputDirectory) -and
        @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        throw (
            "The output directory already exists and is not empty: $OutputDirectory. " +
            'Choose another -OutputDirectory or remove it first.'
        )
    }
}
else {
    # The default location is script-owned, so a stale package is cleared here
    # rather than being mistaken for the current build.
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\msix\$Architecture"
    Remove-DirectoryIfPresent -Path $OutputDirectory
}
New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$temporaryRoot = if ($env:RUNNER_TEMP) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}
$workRoot = Join-Path `
    $temporaryRoot `
    "openclaw-companion-msix-$Architecture-$([guid]::NewGuid().ToString('N'))"
$msixBuildDirectory = Join-Path $workRoot 'appx'
New-Item -Path $msixBuildDirectory -ItemType Directory -Force | Out-Null

$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }

try {
    $appxOutput = $msixBuildDirectory.TrimEnd('\') + '\'
    Write-Host "Building unsigned win-$Architecture MSIX with MSBuild."
    Invoke-CheckedCommand `
        -FailureMessage "MSIX build failed for $Architecture." `
        -Command {
            & dotnet publish $projectPath `
                --configuration $Configuration `
                --runtime "win-$Architecture" `
                --self-contained `
                "-p:Platform=$platform" `
                -p:PackageMsix=true `
                -p:GenerateAppxPackageOnBuild=true `
                -p:AppxBundle=Never `
                -p:UapAppxPackageBuildMode=SideloadOnly `
                -p:AppxPackageSigningEnabled=false `
                "-p:AppxPackageDir=$appxOutput" `
                --nologo
        }

    $builtPackages = @(
        Get-ChildItem `
            -LiteralPath $msixBuildDirectory `
            -Filter '*.msix' `
            -File `
            -Recurse
    )
    if ($builtPackages.Count -ne 1) {
        throw (
            "Expected one MSIX under '$msixBuildDirectory'; " +
            "found $($builtPackages.Count)."
        )
    }

    $msixName = "OpenClawCompanion-$Architecture.msix"
    $msixPath = Join-Path $OutputDirectory $msixName
    Copy-Item -LiteralPath $builtPackages[0].FullName -Destination $msixPath -Force

    $requiredEntries = @(
        'OpenClaw.Tray.WinUI.exe',
        'AppxManifest.xml',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'System.Private.CoreLib.dll',
        'Microsoft.ui.xaml.dll',
        'OpenClaw.SetupEngine.dll',
        'OpenClaw.SetupEngine.UI.dll',
        "tools/mxc/$Architecture/wxc-exec.exe"
    )
    # The MSIX resolves the CRT through the VCLibs framework dependency, and
    # Partner Center rejects a package that is already signed.
    $forbiddenEntries = @(
        'AppxSignature.p7x',
        'vcruntime140.dll',
        'vcruntime140_1.dll',
        'msvcp140.dll',
        'msvcp140_1.dll'
    )

    $packageEntries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($msixPath)
    try {
        foreach ($entry in $packageArchive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }

            $null = $packageEntries.Add([Uri]::UnescapeDataString($entry.FullName))
        }

        $manifestEntry = $packageArchive.Entries |
            Where-Object { $_.FullName -eq 'AppxManifest.xml' } |
            Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw 'The MSIX does not contain AppxManifest.xml.'
        }

        $manifestReader = New-Object System.IO.StreamReader($manifestEntry.Open())
        try {
            [xml]$packagedManifest = $manifestReader.ReadToEnd()
        }
        finally {
            $manifestReader.Dispose()
        }
    }
    finally {
        $packageArchive.Dispose()
    }

    foreach ($requiredEntry in $requiredEntries) {
        if (-not $packageEntries.Contains($requiredEntry)) {
            throw "The MSIX is missing required content: $requiredEntry"
        }
    }
    foreach ($forbiddenEntry in $forbiddenEntries) {
        if ($packageEntries.Contains($forbiddenEntry)) {
            throw "The MSIX contains forbidden content: $forbiddenEntry"
        }
    }

    $packagedIdentity = $packagedManifest.Package.Identity
    $packageVersion = [string]$packagedIdentity.Version
    Test-PackageVersion -Version $packageVersion

    if ([string]$packagedIdentity.Name -ne $expectedIdentityName) {
        throw (
            "The MSIX identity is '$($packagedIdentity.Name)' but " +
            "$sourceManifestPath declares '$expectedIdentityName'."
        )
    }
    if ([string]$packagedIdentity.Publisher -ne $expectedPublisher) {
        throw (
            "The MSIX publisher is '$($packagedIdentity.Publisher)' but " +
            "$sourceManifestPath declares '$expectedPublisher'."
        )
    }
    if ([string]$packagedIdentity.ProcessorArchitecture -ne $Architecture) {
        throw (
            "The MSIX targets '$($packagedIdentity.ProcessorArchitecture)' " +
            "but $Architecture was requested."
        )
    }

    $sourceCommit = (& git -C $repositoryRoot rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Unable to resolve the current source commit.'
    }
    $sourceTreeDirty = [bool](& git -C $repositoryRoot status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect the current source tree.'
    }

    $msixHash = (
        Get-FileHash -LiteralPath $msixPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw-windows-node'
        sourceCommit = $sourceCommit.ToLowerInvariant()
        sourceTreeDirty = $sourceTreeDirty
        architecture = $Architecture
        configuration = $Configuration
        archive = $msixName
        sha256 = $msixHash
        signed = $false
        identityName = $expectedIdentityName
        packageVersion = $packageVersion
        publisher = $expectedPublisher
    } | ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $OutputDirectory 'msix-metadata.json') `
            -Encoding utf8

    Write-Host "Created unsigned MSIX: $msixPath"
    Write-Host "  Identity: $expectedIdentityName $packageVersion $Architecture"
}
finally {
    Remove-DirectoryIfPresent -Path $workRoot
}
