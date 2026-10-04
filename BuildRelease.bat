@echo off
setlocal DisableDelayedExpansion

if /i "%~1"=="/?" goto help
if not "%~1"=="" if /i not "%~1"=="/nopause" goto badargs
if not "%~2"=="" goto badargs

echo Building DedupDesk Release for Windows x64...
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Publish
set "DEDUP_RELEASE_EXIT=%errorlevel%"
echo.
if not "%DEDUP_RELEASE_EXIT%"=="0" goto failed

echo Build succeeded.
echo EXE: "%~dp0artifacts\DedupDesk-win-x64\DedupDesk.exe"
echo ZIP: "%~dp0artifacts\DedupDesk-win-x64.zip"
goto finish

:failed
echo Build failed. Exit code: %DEDUP_RELEASE_EXIT%
echo See the error output above.

:finish
if /i not "%~1"=="/nopause" pause
exit /b %DEDUP_RELEASE_EXIT%

:badargs
echo Unknown arguments. Use BuildRelease.bat /? for help.
exit /b 2

:help
echo Usage: BuildRelease.bat [/nopause]
echo Builds Release, runs isolated tests, and publishes a self-contained Windows x64 EXE and ZIP.
echo Double-click to build and keep the result window open.
echo Use /nopause for terminal or automation runs.
exit /b 0
