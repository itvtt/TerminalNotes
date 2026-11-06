# Isolated executable fixtures only: no network, SDK installation, or real app launch.
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixtureRoot = Join-Path $env:TEMP ('TerminalNotes bootstrap 한글 & space ' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$source = @'
using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
class Stub {
  static void Log(string line) { for(int attempt=0;;attempt++) { try { File.AppendAllText(Environment.GetEnvironmentVariable("TN_TEST_LOG"), line + "\n"); return; } catch(IOException) { if(attempt==99) throw; System.Threading.Thread.Sleep(10); } } }
  static int Main(string[] args) {
    string ownPath = Assembly.GetExecutingAssembly().Location;
    string root = Path.GetDirectoryName(ownPath);
    string mode = Environment.GetEnvironmentVariable("TN_TEST_MODE");
    if (Path.GetFileName(ownPath).Equals("TerminalNotes.exe", StringComparison.OrdinalIgnoreCase)) {
      Log("launch"); Log("cwd=" + Environment.CurrentDirectory);
      foreach (string arg in args) Log("app-arg=" + arg);
      Log("app-root=" + Environment.GetEnvironmentVariable("DOTNET_ROOT"));
      Log("app-root-x64=" + Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
      Log("app-path=" + Environment.GetEnvironmentVariable("PATH").Split(';')[0]);
      var child = Process.Start(new ProcessStartInfo("dotnet.exe", "--child-check") { UseShellExecute=false, CreateNoWindow=true });
      child.WaitForExit(); return child.ExitCode;
    }
    if (args.Length > 0 && args[0] == "--child-check") { Log("child-root=" + Environment.GetEnvironmentVariable("DOTNET_ROOT")); return 0; }
    bool installed = File.Exists(Path.Combine(root, "private-ready"));
    if (args.Length > 0 && args[0] == "--list-sdks") {
      Log("sdk");
      if (installed || (mode != "runtime-only" && mode != "no-sdk")) Console.WriteLine(!installed && mode.StartsWith("sdk11") ? "11.0.100 [fixture]" : !installed && mode == "sdk9" ? "9.0.100 [fixture]" : "10.0.100 [fixture]");
      return 0;
    }
    if (args.Length > 0 && args[0] == "--list-runtimes") {
      Log("runtime");
      string version = !installed && mode == "sdk11-missing10" ? "11.0.0" : !installed && mode == "preview-runtime" ? "10.0.0-preview.1" : "10.0.1";
      Console.WriteLine("Microsoft.NETCore.App " + version + " [fixture]");
      if (installed || mode != "missing-desktop") Console.WriteLine("Microsoft.WindowsDesktop.App " + version + " [fixture]");
      return 0;
    }
    Log("publish"); Log("publish-root=" + Environment.GetEnvironmentVariable("DOTNET_ROOT"));
    foreach (string arg in args) Log("publish-arg=" + arg);
    Console.WriteLine("PUBLISH-OUTPUT-PRESERVED");
    if (mode == "fail" || mode == "negative-fail") { Console.Error.WriteLine("PUBLISH-ERROR-PRESERVED"); Console.Error.WriteLine("PUBLISH-SECOND-ERROR-PRESERVED"); return mode == "negative-fail" ? -37 : 37; }
    string output = Path.GetFullPath(args[Array.IndexOf(args, "-o") + 1]); Directory.CreateDirectory(output);
    File.Copy(ownPath, Path.Combine(output, "TerminalNotes.exe"), true);
    foreach (string file in new [] {"TerminalNotes.dll", "TerminalNotes.deps.json", "TerminalNotes.runtimeconfig.json", "TerminalNotes.TerminalEngine.dll", "Markdig.dll", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll"}) {
      if (mode != "partial" || file != "TerminalNotes.dll") File.WriteAllText(Path.Combine(output, file), "fixture");
    }
    return 0;
  }
}
'@
$sourcePath = Join-Path $fixtureRoot 'Stub.cs'
[IO.File]::WriteAllText($sourcePath, $source)
$stubExe = Join-Path $fixtureRoot 'fixture.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 "/out:$stubExe" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Could not compile x64 test stub.' }
$x86Exe = Join-Path $fixtureRoot 'x86.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x86 "/out:$x86Exe" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Could not compile x86 test stub.' }
$files = @('TerminalNotes.exe','TerminalNotes.dll','TerminalNotes.deps.json','TerminalNotes.runtimeconfig.json','TerminalNotes.TerminalEngine.dll','Markdig.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')
$installerScript = @'
param($Channel, $Quality, $Architecture, $InstallDir, [switch]$NoPath)
[IO.File]::AppendAllText($env:TN_TEST_LOG, "install=$Channel,$Quality,$Architecture,$NoPath`n")
Write-Output 'INSTALL-OUTPUT-PRESERVED'
if ($env:TN_TEST_MODE -eq 'install-fail') { [Console]::Error.WriteLine('INSTALL-ERROR-PRESERVED'); exit 29 }
if ($env:TN_TEST_MODE -eq 'false-success') { exit 0 }
[void][IO.Directory]::CreateDirectory($InstallDir)
Copy-Item -LiteralPath $env:TN_TEST_STUB -Destination (Join-Path $InstallDir 'dotnet.exe')
[IO.File]::WriteAllText((Join-Path $InstallDir 'private-ready'), '')
exit 0
'@
$installerFixture = Join-Path $fixtureRoot 'installer.ps1'
[IO.File]::WriteAllText($installerFixture, $installerScript)
$mockDownload = @'
function Invoke-WebRequest {
    param($Uri, [switch]$UseBasicParsing, $OutFile, $ErrorAction)
    [IO.File]::AppendAllText($env:TN_TEST_LOG, "download=$Uri`n")
    if ($env:TN_TEST_MODE -eq 'download-fail') { throw 'DOWNLOAD-ERROR-PRESERVED' }
    Copy-Item -LiteralPath $env:TN_TEST_INSTALLER -Destination $OutFile
}

'@
$cleanup = @'
param([switch]$Apply)
if (-not $Apply) { throw 'Apply switch missing' }
[IO.File]::AppendAllText($env:TN_TEST_LOG, "cleanup`n")
if ($env:TN_TEST_MODE -eq 'cleanup-fail') { throw 'CLEANUP-ERROR-PRESERVED' }
'@
function Assert-Case {
    param($Result, [bool]$Condition, [string]$Message)
    if (-not $Condition) { $Result | Format-List | Out-String | Write-Host; throw "$($Result.Name): $Message" }
}
function Read-EventText {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = New-Object IO.StreamReader($stream)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
function Invoke-Case {
    param([string]$Name, [string]$Launcher = 'Start-TerminalNotes.cmd', [string]$Mode = 'success', [switch]$Ready, [switch]$NoHost, [switch]$X86, [string]$Missing, [string]$Workspace, [string]$ReuseLocal)
    $project = Join-Path $fixtureRoot $Name
    [void][IO.Directory]::CreateDirectory($project)
    foreach ($file in @('Start-TerminalNotes.cmd','Start-TerminalNotes-Updated.cmd','Start-TerminalNotes.ps1','Ensure-TerminalNotes.cmd','Invoke-TerminalNotes.ps1')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $project
    }
    # Inject the download mock into a fixture copy, leaving production scripts untouched.
    $scriptPath = Join-Path $project 'Invoke-TerminalNotes.ps1'
    $script = [IO.File]::ReadAllText($scriptPath)
    $paramEnd = $script.IndexOf("`n") + 1
    [IO.File]::WriteAllText($scriptPath, $script.Substring(0, $paramEnd) + $mockDownload + $script.Substring($paramEnd), [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $project 'TerminalNotes.csproj'), '<Project />')
    [IO.File]::WriteAllText((Join-Path $project 'Clean-BuildArtifacts.ps1'), $cleanup)
    $tools = Join-Path $project 'tools'
    [void][IO.Directory]::CreateDirectory($tools)
    if (-not $NoHost) { Copy-Item -LiteralPath $(if ($X86) { $x86Exe } else { $stubExe }) -Destination (Join-Path $tools 'dotnet.exe') }
    $output = Join-Path $project 'dist\win-x64'
    if ($Ready) {
        [void][IO.Directory]::CreateDirectory($output)
        foreach ($file in $files) {
            if ($file -eq $Missing) { continue }
            if ($file -eq 'TerminalNotes.exe') { Copy-Item -LiteralPath $stubExe -Destination (Join-Path $output $file) }
            else { [IO.File]::WriteAllText((Join-Path $output $file), 'fixture') }
        }
    }
    $local = if ($ReuseLocal) { $ReuseLocal } else { Join-Path $project 'local' }
    $log = Join-Path $project 'events.txt'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.WorkingDirectory = $env:TEMP
    $start.EnvironmentVariables['PATH'] = "$tools;$env:SystemRoot\System32"
    foreach ($envName in @('ProgramFiles','ProgramW6432','USERPROFILE','DOTNET_ROOT','DOTNET_ROOT_X64')) { $start.EnvironmentVariables[$envName] = (Join-Path $project 'empty') }
    $start.EnvironmentVariables['LOCALAPPDATA'] = $local
    $start.EnvironmentVariables['TN_TEST_MODE'] = $Mode
    $start.EnvironmentVariables['TN_TEST_LOG'] = $log
    $start.EnvironmentVariables['TN_TEST_STUB'] = $stubExe
    $start.EnvironmentVariables['TN_TEST_INSTALLER'] = $installerFixture
    if ($Launcher.EndsWith('.ps1')) {
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoProfile -File "' + (Join-Path $project $Launcher) + '"'
        if ($Workspace) { $start.Arguments += ' -Workspace "' + $Workspace + '"' }
    }
    else {
        $start.FileName = "$env:SystemRoot\System32\cmd.exe"
        $start.Arguments = '/d /s /c ""' + (Join-Path $project $Launcher) + '""'
    }
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { throw "Fixture timed out: $Name" }
    # CMD starts a short lived benign executable; wait for its log to flush.
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $until -and -not ((Read-EventText $log).Contains('child-root=')) -and $process.ExitCode -eq 0 -and $Launcher -ne 'Ensure-TerminalNotes.cmd') { Start-Sleep -Milliseconds 30 }
    $result = [pscustomobject]@{ Name=$Name; Project=$project; Local=$local; Tools=$tools; ExitCode=$process.ExitCode; StdOut=$stdout.GetAwaiter().GetResult(); StdErr=$stderr.GetAwaiter().GetResult(); Events=@((Read-EventText $log) -split '\r?\n' | Where-Object { $_ }) }
    $process.Dispose()
    return $result
}

$passed = 0
# Evaluate only the policy preflight expression with mocked policy listings; no policy is changed.
$ensureText = [IO.File]::ReadAllText((Join-Path $sourceRoot 'Ensure-TerminalNotes.cmd'))
$preflight = [regex]::Match($ensureText, '-Command "([^"]+)"').Groups[1].Value
if (-not $preflight) { throw 'Policy preflight expression missing.' }
foreach ($case in @('Undefined','Process','CurrentUser','LocalMachine','MachinePolicy','UserPolicy')) {
    $mockPolicy = 'function Get-ExecutionPolicy { param([switch]$List); foreach ($scope in @(''MachinePolicy'',''UserPolicy'',''Process'',''CurrentUser'',''LocalMachine'')) { [pscustomobject]@{ Scope=$scope; ExecutionPolicy=$(if ($scope -eq ''' + $case + ''') { ''Restricted'' } else { ''Undefined'' }) } } }; '
    $policyResult = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command ($mockPolicy + $preflight)
    if ($LASTEXITCODE -ne 0 -or ($case -eq 'Undefined' -and $policyResult -ne 'RemoteSigned') -or ($case -ne 'Undefined' -and $policyResult)) { throw "Policy preflight failed: $case" }
    $passed++
}
foreach ($launcher in @('Start-TerminalNotes.cmd','Start-TerminalNotes-Updated.cmd','Start-TerminalNotes.ps1')) {
    $result = Invoke-Case -Name ('publish-' + $launcher) -Launcher $launcher
    Assert-Case $result ($result.ExitCode -eq 0) 'successful launcher exit'
    Assert-Case $result ((($result.Events | Where-Object { $_ -in @('publish','cleanup','launch') }) -join ',') -eq 'publish,cleanup,launch') 'publish -> cleanup -> launch'
    Assert-Case $result (-not ($result.Events -match '^download=')) 'installed fast path must not download'
    Assert-Case $result ($result.Events -contains ('cwd=' + $result.Project)) 'application working directory'
    foreach ($prefix in @('app-root=','app-root-x64=','app-path=','child-root=','publish-root=')) { Assert-Case $result ($result.Events -contains ($prefix + $result.Tools)) 'selected host environment inherited' }
    $expected = @('publish-arg=publish','publish-arg=.\TerminalNotes.csproj','publish-arg=-c','publish-arg=Release','publish-arg=-r','publish-arg=win-x64','publish-arg=--self-contained','publish-arg=false','publish-arg=-o','publish-arg=.\dist\win-x64')
    Assert-Case $result ((($result.Events -like 'publish-arg=*') -join '|') -eq ($expected -join '|')) 'exact publish argv'
    $passed++
}
foreach ($mode in @('no-host','runtime-only','no-sdk','sdk9','sdk11-missing10','missing-desktop','preview-runtime','x86')) {
    $result = Invoke-Case -Name $mode -Mode $mode -Ready -NoHost:($mode -eq 'no-host') -X86:($mode -eq 'x86')
    Assert-Case $result ($result.ExitCode -eq 0 -and $result.Events -contains 'install=10.0,GA,x64,True') 'missing dependency installs GA x64 SDK10'
    Assert-Case $result ($result.Events -contains 'download=https://dot.net/v1/dotnet-install.ps1') 'official HTTPS installer'
    Assert-Case $result (-not ($result.Events -contains 'publish') -and $result.Events -contains 'launch') 'ready dist still ensures dependencies and skips publish'
    $private = Join-Path $result.Local 'TerminalNotes\dotnet'
    foreach ($prefix in @('app-root=','app-root-x64=','app-path=','child-root=')) { Assert-Case $result ($result.Events -contains ($prefix + $private)) 'private host is inherited in same run' }
    if ($mode -eq 'no-host') { $savedLocal = $result.Local }
    $passed++
}
$reuse = Invoke-Case -Name 'reuse-private-next-run' -NoHost -Ready -ReuseLocal $savedLocal
Assert-Case $reuse ($reuse.ExitCode -eq 0 -and -not ($reuse.Events -match '^download=')) 'private host reused next run'
$passed++
$installedPublish = Invoke-Case -Name 'install-then-publish' -NoHost
Assert-Case $installedPublish ($installedPublish.ExitCode -eq 0 -and $installedPublish.Events -contains 'publish' -and $installedPublish.Events -contains ('publish-root=' + (Join-Path $installedPublish.Local 'TerminalNotes\dotnet'))) 'new private SDK publishes immediately'
$passed++
foreach ($mode in @('download-fail','install-fail','false-success')) {
    $result = Invoke-Case -Name $mode -Mode $mode -Ready -NoHost
    Assert-Case $result ($result.ExitCode -ne 0 -and -not ($result.Events -contains 'publish') -and -not ($result.Events -contains 'launch')) 'bootstrap failure blocks publish and launch'
    if ($mode -eq 'install-fail') { Assert-Case $result (($result.StdOut + $result.StdErr) -match 'INSTALL-ERROR-PRESERVED') 'installer diagnostics preserved' }
    $passed++
}
foreach ($mode in @('fail','negative-fail','partial')) {
    $result = Invoke-Case -Name $mode -Mode $mode
    Assert-Case $result ($result.ExitCode -ne 0 -and -not ($result.Events -contains 'cleanup') -and -not ($result.Events -contains 'launch')) 'publish failure blocks cleanup and launch'
    if ($mode -ne 'partial') { Assert-Case $result (($result.StdOut + $result.StdErr) -match 'PUBLISH-OUTPUT-PRESERVED' -and ($result.StdOut + $result.StdErr) -match 'PUBLISH-SECOND-ERROR-PRESERVED') 'complete publish diagnostics preserved' }
    $passed++
}
foreach ($file in $files) {
    $result = Invoke-Case -Name ('missing-' + $file) -Ready -Missing $file
    Assert-Case $result ($result.ExitCode -eq 0 -and $result.Events -contains 'publish') 'all ten readiness files required'
    $passed++
}
$workspace = 'C:\작업 영역 & literal $value; [name]'
$result = Invoke-Case -Name 'workspace' -Launcher 'Start-TerminalNotes.ps1' -Ready -Workspace $workspace
Assert-Case $result ($result.ExitCode -eq 0 -and ($result.Events -contains 'app-arg=--workspace') -and ($result.Events -contains ('app-arg=' + $workspace))) 'exact workspace argument'
$passed++
$result = Invoke-Case -Name 'cleanup-fail' -Mode 'cleanup-fail'
Assert-Case $result ($result.ExitCode -eq 0 -and $result.Events -contains 'launch' -and ($result.StdOut + $result.StdErr) -match 'cleanup failed') 'cleanup failure warns and launches verified app'
$passed++
$result = Invoke-Case -Name 'sdk11-with10' -Mode 'sdk11-with10' -Ready
Assert-Case $result ($result.ExitCode -eq 0 -and -not ($result.Events -match '^download=')) 'newer SDK with stable runtime10 is usable'
$passed++
Write-Host "PASS $passed isolated launcher cases. Fixtures retained at $fixtureRoot"
