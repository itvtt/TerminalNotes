param([string]$Workspace)
& (Join-Path $PSScriptRoot 'Invoke-TerminalNotes.ps1') -Launch -WaitForExit -Workspace $Workspace
exit $LASTEXITCODE
