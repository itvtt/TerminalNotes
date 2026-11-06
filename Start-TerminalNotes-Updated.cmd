@echo off
setlocal
call "%~dp0Ensure-TerminalNotes.cmd" -Launch
set "launcherExitCode=%errorlevel%"
if "%launcherExitCode%"=="0" exit /b 0
pause
exit /b %launcherExitCode%
