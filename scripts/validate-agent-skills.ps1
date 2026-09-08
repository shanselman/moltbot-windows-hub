<#
.SYNOPSIS
    Validates repository-owned agent skill documentation.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$skillsRoot = Join-Path $repoRootPath ".agents\skills"
$errors = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $skillsRoot -PathType Container)) {
    throw "Agent skills directory is missing: $skillsRoot"
}

$skillsRootPrefix = $skillsRoot.TrimEnd("\") + "\"
$skillDirectories = @(Get-ChildItem -LiteralPath $skillsRoot -Directory)
if ($skillDirectories.Count -eq 0) {
    throw "Agent skills directory contains no skills."
}

foreach ($skillDirectory in $skillDirectories) {
    $skillFile = Join-Path $skillDirectory.FullName "SKILL.md"
    if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) {
        $errors.Add(".agents/skills/$($skillDirectory.Name): SKILL.md is required")
        continue
    }

    $content = [System.IO.File]::ReadAllText($skillFile)
    $frontMatter = [regex]::Match(
        $content,
        '\A---\r?\nname:\s*(?<name>[^\r\n]+)\r?\ndescription:\s*(?<description>[^\r\n]+)\r?\n---(?:\r?\n|\z)')
    if (-not $frontMatter.Success) {
        $errors.Add(".agents/skills/$($skillDirectory.Name)/SKILL.md: expected name and description YAML front matter")
        continue
    }

    $name = $frontMatter.Groups["name"].Value.Trim().Trim('"', "'")
    if ($name -cne $skillDirectory.Name) {
        $errors.Add(".agents/skills/$($skillDirectory.Name)/SKILL.md: name '$name' must match the directory name")
    }
    $description = $frontMatter.Groups["description"].Value.Trim().Trim('"', "'")
    if ([string]::IsNullOrWhiteSpace($description)) {
        $errors.Add(".agents/skills/$($skillDirectory.Name)/SKILL.md: description must not be empty")
    }
}

$linkPattern = [regex]'!?\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^)]*["''])?\)'
$markdownFiles = @(Get-ChildItem -LiteralPath $skillsRoot -Filter "*.md" -Recurse -File)
foreach ($markdownFile in $markdownFiles) {
    $relativePath = $markdownFile.FullName.Substring($skillsRootPrefix.Length).Replace("\", "/")
    $content = [System.IO.File]::ReadAllText($markdownFile.FullName)
    if ($content.Contains([string][char]0x2014)) {
        $errors.Add(".agents/skills/${relativePath}: agent-facing content must not use em dashes")
    }

    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups["target"].Value.Trim().Trim("<", ">")
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith("#") -or
            $target.StartsWith("/") -or
            $target -match "^[a-z][a-z0-9+.-]*:" -or
            $target.Contains("{") -or
            $target.Contains("}")) {
            continue
        }

        $targetWithoutFragment = $target.Split("#", 2)[0].Split("?", 2)[0]
        if ([string]::IsNullOrWhiteSpace($targetWithoutFragment)) {
            continue
        }
        $resolvedPath = [System.IO.Path]::GetFullPath(
            (Join-Path $markdownFile.DirectoryName (
                [System.Uri]::UnescapeDataString($targetWithoutFragment) -replace "/", "\")))
        if (-not $resolvedPath.StartsWith(
                $skillsRootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            $errors.Add(".agents/skills/${relativePath}: local link leaves the skills directory: $target")
        } elseif (-not (Test-Path -LiteralPath $resolvedPath)) {
            $errors.Add(".agents/skills/${relativePath}: local link target does not exist: $target")
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "`nAgent skill validation failed:" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host "  - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Agent skill validation passed: $($skillDirectories.Count) skills and $($markdownFiles.Count) Markdown files checked." -ForegroundColor Green
exit 0
