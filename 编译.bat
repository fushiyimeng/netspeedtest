@echo off
rem Rebuild the speed-test exe.
rem ASCII only + CRLF on purpose: cmd parses batch files in the OEM code page,
rem so mixing in non-ASCII would break parsing. Chinese notes live in the .txt beside it.
setlocal enabledelayedexpansion
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [X] dotnet not found. Install the .NET 10 SDK first.
  pause
  exit /b 1
)

rem Find the source folder without spelling out its name (it is non-ASCII).
rem Note: a wildcard in the FOLDER part of "for %%F in (dir\*.x)" does not expand;
rem "for /d" over the parent does.
set "SRCDIR="
for /d %%D in ("%~dp0*") do if exist "%%D\*.csproj" set "SRCDIR=%%D"
if not defined SRCDIR (
  echo [X] No subfolder with a .csproj next to this .bat.
  pause
  exit /b 1
)

echo Building from "!SRCDIR!" ...
echo.
pushd "!SRCDIR!"
dotnet publish -c Release --nologo
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo.
  echo [X] Build failed.
  pause
  exit /b 1
)

echo.
echo [OK] Done. The exe was copied next to this .bat.
echo.
pause
