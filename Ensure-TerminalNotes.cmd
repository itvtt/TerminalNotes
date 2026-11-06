@echo off
setlocal
set "defaultPolicyOption="
rem Windows defaults to Restricted when every policy scope is Undefined.
rem A session-only RemoteSigned default honors every explicitly configured policy.
for /f "delims=" %%P in ('%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "$configured = $false; foreach ($policy in (Get-ExecutionPolicy -List)) { if ($policy.ExecutionPolicy -ne 'Undefined') { $configured = $true } }; if (-not $configured) { 'RemoteSigned' }"') do set "defaultPolicyOption=-ExecutionPolicy %%P"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile %defaultPolicyOption% -File "%~dp0Invoke-TerminalNotes.ps1" %*
exit /b %errorlevel%
