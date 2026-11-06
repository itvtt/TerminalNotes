# Builds the Terminal Notes release into out\:
#   TerminalNotes-Setup.exe   first install (per user, no admin; Start menu/desktop shortcuts, Windows "Apps" entry;
#                             runs on the .NET Framework built into Windows and brings the .NET 10 runtime for PCs without it)
#   TerminalNotes-win-x64.zip what the installed app downloads from the GitHub Release to update itself
#   win-x64\                  the published app folder (the zip's contents)
# Needs only the .NET 10 SDK. Third-party assemblies are in lib\, so nothing is downloaded during the build.
# Used by .github/workflows/release.yml; also runs by hand:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\Publish-Release.ps1 [-Version 1.0.130]
# Users need nothing preinstalled except the WebView2 Runtime (built into Windows 11): the Setup brings .NET 10 if missing.
param(
    [string]$Version = '',
    [string]$OutputDirectory = 'out'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$sdk = ''
try { $sdk = (& dotnet --version 2>$null | Select-Object -First 1) } catch { }
if (-not $sdk -or [int]($sdk.Split('.')[0]) -lt 10) { throw ".NET 10 SDK is required on the build machine (found: '$sdk')." }

if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'TerminalNotes.csproj') -Encoding UTF8
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if (-not (Test-Path (Join-Path $root '.env'))) {
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Warning '.env not found: no problem reports; updates check this repository without a token (UPDATE_TOKEN).' }
    else { Write-Warning '.env not found: the build will not report problems or check for updates.' }
}
else {
    # On the Actions runner an empty UPDATE_REPOSITORY falls back to the runner's repository (TerminalNotes.csproj).
    $updateRepository = @(Get-Content (Join-Path $root '.env') -Encoding UTF8 | Where-Object { $_ -match '^UPDATE_REPOSITORY=\s*\S' })
    if ($updateRepository.Count -eq 0 -and $env:GITHUB_ACTIONS -ne 'true') {
        Write-Warning 'UPDATE_REPOSITORY is empty in .env: this build will not check for updates.'
    }
}

$out = [IO.Path]::Combine($root, $OutputDirectory)
$publish = Join-Path $out 'win-x64'
$zip = Join-Path $out 'TerminalNotes-win-x64.zip'
$setup = Join-Path $out 'TerminalNotes-Setup.exe'
$setupStage = Join-Path $out 'setup-build'
foreach ($old in $publish, $setupStage) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
foreach ($old in $zip, $setup) { if (Test-Path $old) { Remove-Item $old -Force } }

Write-Host "Publishing Terminal Notes $Version (SDK $sdk)"
& dotnet publish (Join-Path $root 'TerminalNotes.csproj') -c Release -r win-x64 --self-contained false -nologo `
    -o $publish "-p:Version=$Version" "-p:ReleaseZip=$zip"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
foreach ($required in 'TerminalNotes.exe', 'TerminalNotes.dll', 'WebView2Loader.dll') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "$required is missing from the published folder." }
}
if (-not (Test-Path $zip)) { throw 'The release zip was not created.' }

# .NET 10 runtime for PCs without it, copied from this build machine's own .NET (the SDK installs it; nothing is
# downloaded). The Setup unpacks it next to the app only when the PC has no .NET 10 Desktop Runtime.
$runtimeStage = Join-Path $out 'runtime-build'
$uninstallStage = Join-Path $out 'uninstall-build'
foreach ($old in $runtimeStage, $uninstallStage) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
$desktopRuntimes = @(& dotnet --list-runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.WindowsDesktop\.App (10\.\d+\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Text = $Matches[1]; Path = $Matches[2] } }
} | Sort-Object Version -Descending)
if ($desktopRuntimes.Count -eq 0) { throw 'The .NET 10 Desktop Runtime (installed with the .NET 10 SDK on Windows) was not found on the build machine.' }
$runtimeVersion = $desktopRuntimes[0].Text
$dotnetRoot = Split-Path -Parent (Split-Path -Parent $desktopRuntimes[0].Path)
$fxr = @(Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'host\fxr') -Directory | Where-Object { $_.Name -match '^(\d+)\.\d+\.\d+$' -and [int]$Matches[1] -ge 10 } |
    Sort-Object { [version]$_.Name } -Descending)
if ($fxr.Count -eq 0) { throw "No .NET 10 host resolver (host\fxr) on the build machine under $dotnetRoot." }
foreach ($part in "host\fxr\$($fxr[0].Name)", "shared\Microsoft.NETCore.App\$runtimeVersion", "shared\Microsoft.WindowsDesktop.App\$runtimeVersion") {
    $source = Join-Path $dotnetRoot $part
    if (-not (Test-Path -LiteralPath $source)) { throw "The .NET runtime part is missing on the build machine: $source" }
    $destination = Join-Path $runtimeStage $part
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Recurse
}
foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $path = Join-Path $dotnetRoot $notice
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $runtimeStage }
}
Write-Host "Bundled .NET runtime $runtimeVersion (host $($fxr[0].Name)) for PCs without .NET 10"

# Installer (.NET Framework 4.8, built into Windows): the small uninstall.exe first, then the Setup with the
# release zip, the runtime and that uninstaller embedded.
$setupProject = Join-Path $root 'installer\TerminalNotes.Setup.csproj'
Write-Host 'Building uninstall.exe'
& dotnet publish $setupProject -c Release -nologo -o $uninstallStage "-p:Version=$Version" -p:BuildUninstaller=true
if ($LASTEXITCODE -ne 0) { throw "Uninstaller build failed (exit $LASTEXITCODE)." }
$installerObj = Join-Path $root 'installer\obj'
if (Test-Path $installerObj) { Remove-Item $installerObj -Recurse -Force }
Write-Host 'Building TerminalNotes-Setup.exe'
& dotnet publish $setupProject -c Release -nologo -o $setupStage "-p:Version=$Version" "-p:PayloadZip=$zip" `
    "-p:RuntimeDirectory=$runtimeStage" "-p:UninstallerExe=$(Join-Path $uninstallStage 'TerminalNotes-Setup.exe')"
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit $LASTEXITCODE)." }
Move-Item -LiteralPath (Join-Path $setupStage 'TerminalNotes-Setup.exe') -Destination $setup -Force

# Final cleanup: keep only the release files (win-x64, zip, setup) and drop build intermediates.
foreach ($stage in $setupStage, $uninstallStage, $runtimeStage) { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force } }
foreach ($intermediate in 'bin', 'obj') {
    $path = Join-Path (Join-Path $root 'installer') $intermediate
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

# The relay server moved to its own project (itvtt/ApiServer, server\relay.py): its program ApiServer.exe deploys it.

Write-Host ("Release zip: {0} ({1:N1} MB, {2} files)" -f $zip, ((Get-Item $zip).Length / 1MB), @(Get-ChildItem $publish -Recurse -File).Count)
Write-Host ("Installer:   {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zip" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "setup=$setup" -Encoding ASCII
    Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$Version" -Encoding ASCII
}
