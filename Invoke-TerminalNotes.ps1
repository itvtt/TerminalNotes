param([switch]$Launch, [switch]$WaitForExit, [string]$Workspace)

$ErrorActionPreference = 'Stop'
$privateRoot = Join-Path $env:LOCALAPPDATA 'TerminalNotes\dotnet'
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Test-X64Host {
    param([string]$Path)
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or $offset -gt ($stream.Length - 6)) { return $false }
        $stream.Position = $offset
        return ($reader.ReadUInt32() -eq 0x4550 -and $reader.ReadUInt16() -eq 0x8664)
    }
    catch { return $false }
    finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
}

function Get-HostOutput {
    param([string]$Path, [string]$Argument)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Path
    $start.Arguments = $Argument
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { return $null }
        return $output
    }
    catch { return $null }
    finally { $process.Dispose() }
}

function Test-DotnetEnvironment {
    param([string]$Path)
    if (-not (Test-X64Host $Path)) { return $false }
    $sdks = Get-HostOutput $Path '--list-sdks'
    if (-not ($sdks -match '(?m)^([1-9][0-9]+)\.\d+\.\d+[^\r\n]*\s\[')) { return $false }
    $runtimes = Get-HostOutput $Path '--list-runtimes'
    return ($runtimes -match '(?m)^Microsoft\.NETCore\.App 10\.\d+\.\d+ \[' -and
            $runtimes -match '(?m)^Microsoft\.WindowsDesktop\.App 10\.\d+\.\d+ \[')
}

function Find-DotnetEnvironment {
    $roots = New-Object 'Collections.Generic.List[string]'
    foreach ($root in @($env:ProgramW6432, $env:ProgramFiles)) {
        if ($root) {
            $roots.Add((Join-Path $root 'dotnet'))
            $roots.Add((Join-Path $root 'dotnet\x64'))
        }
    }
    foreach ($entry in ($env:PATH -split ';')) {
        if ($entry) { $roots.Add($entry.Trim('"')) }
    }
    if ($env:LOCALAPPDATA) { $roots.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet')) }
    if ($env:USERPROFILE) { $roots.Add((Join-Path $env:USERPROFILE '.dotnet')) }
    foreach ($root in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
        if ($root) { $roots.Add($root) }
    }
    $roots.Add($privateRoot)
    $seen = @{}
    foreach ($root in $roots) {
        try { $hostPath = [IO.Path]::GetFullPath((Join-Path $root 'dotnet.exe')) }
        catch { continue }
        if ($seen.ContainsKey($hostPath)) { continue }
        $seen[$hostPath] = $true
        if (Test-DotnetEnvironment $hostPath) { return $hostPath }
    }
    return $null
}

function Install-PrivateDotnet {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $downloadRoot = Join-Path $tempRoot ('TerminalNotes-dotnet-install-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($downloadRoot)
    $installerPath = Join-Path $downloadRoot 'dotnet-install.ps1'
    try {
        Write-Host 'Installing the .NET 10 x64 SDK for TerminalNotes. This may take a few minutes...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing -OutFile $installerPath -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf) -or (Get-Item -LiteralPath $installerPath).Length -eq 0) {
            throw 'The official .NET installer download was empty.'
        }
        # The official installer may call exit: keep it in its own PowerShell process.
        $ErrorActionPreference = 'Continue'
        try { & $powershellPath -NoProfile -File $installerPath -Channel 10.0 -Quality GA -Architecture x64 -InstallDir $privateRoot -NoPath | Out-Host }
        finally { $ErrorActionPreference = 'Stop' }
        if ($LASTEXITCODE -ne 0) { throw "The .NET installer failed (exit code $LASTEXITCODE)." }
        $installedHost = Join-Path $privateRoot 'dotnet.exe'
        if (-not (Test-DotnetEnvironment $installedHost)) {
            throw 'The installation did not provide an x64 SDK 10 or newer and stable .NET 10 Core and Windows Desktop runtimes.'
        }
        return $installedHost
    }
    finally {
        # Only this invocation's GUID directory under the verified temporary root is owned here.
        $resolvedDownload = [IO.Path]::GetFullPath($downloadRoot)
        if ($resolvedDownload.StartsWith($tempRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedDownload) -match '^TerminalNotes-dotnet-install-[0-9a-f]{32}$') {
            try { Remove-Item -LiteralPath $resolvedDownload -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Could not remove temporary .NET installer files: $($_.Exception.Message)" }
        }
    }
}

function Ensure-DotnetEnvironment {
    $hostPath = Find-DotnetEnvironment
    if ($hostPath) { return $hostPath }
    # Concurrent launchers share the private SDK; recheck after acquiring its setup lock.
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($privateRoot.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = New-Object Threading.Mutex($false, ('Local\TerminalNotes.DotnetSetup.' + $key))
    $locked = $false
    try {
        try { $locked = $mutex.WaitOne() }
        catch [Threading.AbandonedMutexException] { $locked = $true }
        $hostPath = Find-DotnetEnvironment
        if ($hostPath) { return $hostPath }
        return (Install-PrivateDotnet)
    }
    finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Test-PublishedApplication {
    foreach ($file in @('TerminalNotes.exe', 'TerminalNotes.dll', 'TerminalNotes.deps.json',
        'TerminalNotes.runtimeconfig.json', 'TerminalNotes.TerminalEngine.dll', 'Markdig.dll',
        'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll',
        'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "dist\win-x64\$file") -PathType Leaf)) { return $false }
    }
    return $true
}

try {
    $dotnetPath = Ensure-DotnetEnvironment
    $dotnetRoot = Split-Path -Parent $dotnetPath
    # Process-only settings are inherited by publish, the app, and its installer child.
    $env:PATH = $dotnetRoot + ';' + $env:PATH
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    Push-Location -LiteralPath $PSScriptRoot
    try {
        if (-not (Test-PublishedApplication)) {
            Write-Host 'Published application files are missing. Publishing TerminalNotes...'
            $ErrorActionPreference = 'Continue'
            try { & $dotnetPath publish '.\TerminalNotes.csproj' -c Release -r win-x64 --self-contained false -o '.\dist\win-x64' }
            finally { $ErrorActionPreference = 'Stop' }
            $publishExitCode = $LASTEXITCODE
            if ($publishExitCode -ne 0) {
                Write-Host "ERROR: TerminalNotes publish failed (exit code $publishExitCode). Application was not started."
                exit $publishExitCode
            }
            if (-not (Test-PublishedApplication)) {
                throw 'Publish finished without all required application files. Application was not started.'
            }
            $ErrorActionPreference = 'Continue'
            try { & $powershellPath -NoProfile -File (Join-Path $PSScriptRoot 'Clean-BuildArtifacts.ps1') -Apply }
            finally { $ErrorActionPreference = 'Stop' }
            if ($LASTEXITCODE -ne 0) {
                Write-Warning 'Build artifact cleanup failed. Published application files are ready; continuing to launch.'
            }
        }
        if ($Launch) {
            $applicationPath = Join-Path $PSScriptRoot 'dist\win-x64\TerminalNotes.exe'
            if ($Workspace) { & $applicationPath --workspace $Workspace }
            elseif ($WaitForExit) { & $applicationPath }
            else { Start-Process -FilePath $applicationPath -WorkingDirectory $PSScriptRoot | Out-Null }
        }
    }
    finally { Pop-Location }
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message) Application was not started."
    exit 1
}
