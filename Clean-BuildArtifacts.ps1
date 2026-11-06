#requires -Version 5.1
<#
.SYNOPSIS
Previews project bin/obj cleanup; pass -Apply after publishing to perform it.
.DESCRIPTION
Only this script's project, the named component projects (including the installer),
and direct test project directories are eligible. Published dist files are hashed before and
after deletion. No application data, arbitrary project roots, or reports are used.
.EXAMPLE
.\Clean-BuildArtifacts.ps1
.EXAMPLE
.\Clean-BuildArtifacts.ps1 -Apply -PassThru
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar

function Assert-SafePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the project: $Path"
    }
    $cursor = $fullPath
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
    $resolved = Resolve-Path -LiteralPath $fullPath -ErrorAction Stop
    if ($resolved.Provider.Name -ne 'FileSystem' -or
        -not $resolved.ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar).Equals(
            $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path did not resolve to its expected location: $Path"
    }
}

function Get-SafeTreeFiles {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-SafePath -Path $Path
    if (-not (Get-Item -LiteralPath $Path -Force).PSIsContainer) {
        throw "Expected a directory: $Path"
    }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-SafePath -Path $directory
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            Assert-SafePath -Path $entry.FullName
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
            else {
                $entry
            }
        }
    }
}

function Get-OutputTargets {
    $projectDirectories = New-Object 'System.Collections.Generic.List[string]'
    $projectDirectories.Add($workspaceRoot)
    foreach ($name in @('TerminalEngine', 'TerminalTransport', 'TerminalDesktop', 'installer')) {
        $directory = Join-Path $workspaceRoot $name
        if (Test-Path -LiteralPath $directory) {
            Assert-SafePath -Path $directory
            if (@(Get-ChildItem -LiteralPath $directory -Filter '*.csproj' -File -Force).Count -gt 0) {
                $projectDirectories.Add($directory)
            }
        }
    }
    $testsPath = Join-Path $workspaceRoot 'tests'
    if (Test-Path -LiteralPath $testsPath) {
        Assert-SafePath -Path $testsPath
        foreach ($directory in Get-ChildItem -LiteralPath $testsPath -Directory -Force) {
            if ($directory.Name -in @('artifacts', 'dist', 'bin', 'obj', 'fixtures', 'history', 'logs', 'settings', 'docs', 'Assets')) {
                continue
            }
            Assert-SafePath -Path $directory.FullName
            if (@(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.csproj' -File -Force).Count -gt 0) {
                $projectDirectories.Add($directory.FullName)
            }
        }
    }
    foreach ($directory in $projectDirectories) {
        foreach ($projectFile in Get-ChildItem -LiteralPath $directory -Filter '*.csproj' -File -Force) {
            Assert-SafePath -Path $projectFile.FullName
        }
        foreach ($name in @('bin', 'obj')) {
            $target = Join-Path $directory $name
            if (Test-Path -LiteralPath $target) {
                $files = @(Get-SafeTreeFiles -Path $target)
                [long]$bytes = 0
                foreach ($file in $files) { $bytes += $file.Length }
                [pscustomobject]@{ Path = $target; Files = $files.Count; Bytes = $bytes }
            }
        }
    }
}

function Get-PublishManifest {
    $distPath = Join-Path $workspaceRoot 'dist'
    $manifest = @{}
    foreach ($file in @(Get-SafeTreeFiles -Path $distPath)) {
        $relativePath = $file.FullName.Substring($workspacePrefix.Length)
        $manifest[$relativePath] = '{0}:{1}' -f $file.Length, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    foreach ($name in @('TerminalNotes.exe', 'TerminalNotes.dll', 'TerminalNotes.deps.json', 'TerminalNotes.runtimeconfig.json')) {
        $publishedFile = Join-Path (Join-Path $distPath 'win-x64') $name
        Assert-SafePath -Path $publishedFile
        if ((Get-Item -LiteralPath $publishedFile -Force).PSIsContainer) {
            throw "Required published file is a directory: $publishedFile"
        }
    }
    return ,$manifest
}

Assert-SafePath -Path $workspaceRoot
foreach ($marker in @('TerminalNotes.csproj', 'launcher.app.json')) {
    $markerPath = Join-Path $workspaceRoot $marker
    Assert-SafePath -Path $markerPath
    if ((Get-Item -LiteralPath $markerPath -Force).PSIsContainer) {
        throw "Project marker is not a file: $markerPath"
    }
}

# Finish discovery and validation of every target before any deletion.
$targets = @(Get-OutputTargets)
[long]$fileCount = 0
[long]$byteCount = 0
foreach ($target in $targets) {
    $fileCount += $target.Files
    $byteCount += $target.Bytes
    Write-Host ('  {0}: {1} files, {2} bytes' -f $target.Path.Substring($workspacePrefix.Length), $target.Files, $target.Bytes)
}
$mode = if ($Apply) { 'Apply' } else { 'Preview' }
Write-Host ('{0}: {1} build directories, {2} files, {3} bytes.' -f $mode, $targets.Count, $fileCount, $byteCount)

if ($Apply) {
    $beforePublish = Get-PublishManifest
    # Repeat the complete preflight after hashing, before starting the mutation.
    $verifiedTargets = @(Get-OutputTargets)
    $expected = @($targets | ForEach-Object { '{0}|{1}|{2}' -f $_.Path, $_.Files, $_.Bytes })
    $verified = @($verifiedTargets | ForEach-Object { '{0}|{1}|{2}' -f $_.Path, $_.Files, $_.Bytes })
    if (($expected -join "`n") -cne ($verified -join "`n")) {
        throw 'Build outputs changed during preflight. Stop builds and run cleanup again.'
    }
    foreach ($target in $verifiedTargets) {
        # Validate again immediately before removal; never traverse a reparse point.
        $null = @(Get-SafeTreeFiles -Path $target.Path)
        Remove-Item -LiteralPath $target.Path -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $target.Path) {
            throw "Build output still exists after cleanup: $($target.Path)"
        }
    }
    $afterPublish = Get-PublishManifest
    if ($beforePublish.Count -ne $afterPublish.Count) {
        throw 'Published dist file count changed during cleanup.'
    }
    foreach ($path in $beforePublish.Keys) {
        if (-not $afterPublish.ContainsKey($path) -or $beforePublish[$path] -cne $afterPublish[$path]) {
            throw "Published dist file changed during cleanup: $path"
        }
    }
    Write-Host ('Cleanup complete; {0} published dist files verified unchanged.' -f $afterPublish.Count)
}
else {
    Write-Host 'Preview only. Publish first, then run with -Apply to delete these build directories.'
}

if ($PassThru) {
    [pscustomobject]@{
        Mode = $mode
        Directories = $targets.Count
        Files = $fileCount
        Bytes = $byteCount
        Targets = $targets
        PublishedFilesVerified = $(if ($Apply) { $afterPublish.Count } else { $null })
    }
}
