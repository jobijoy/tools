@echo off
:: ============================================
:: IdolClick Installer Build Script
:: ============================================
:: Requires: Inno Setup 6 (https://jrsoftware.org/isdl.php)
:: 
:: Usage: build-installer.bat [options]
::   --skip-publish    Skip dotnet publish step
::   --help            Show this help
:: ============================================

setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "SRC_DIR=%ROOT_DIR%\src\IdolClick.App"
set "PUBLISH_DIR=%ROOT_DIR%\publish\win-x64"
set "OUTPUT_DIR=%SCRIPT_DIR%output"
set "ISS_FILE=%SCRIPT_DIR%IdolClick.iss"
set "SKIP_PUBLISH=0"

:: Parse arguments
:parse_args
if "%~1"=="" goto :check_inno
if /i "%~1"=="--skip-publish" set "SKIP_PUBLISH=1" & shift & goto :parse_args
if /i "%~1"=="--help" goto :show_help
shift
goto :parse_args

:show_help
echo.
echo IdolClick Installer Build Script
echo =================================
echo.
echo Usage: build-installer.bat [options]
echo.
echo Options:
echo   --skip-publish    Skip dotnet publish step (use existing build)
echo   --help            Show this help
echo.
exit /b 0

:check_inno
echo.
echo ========================================
echo   IdolClick Installer Build
echo ========================================
echo.

:: Find Inno Setup
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
) else if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
    set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
)

if "!ISCC!"=="" (
    echo [ERROR] Inno Setup 6 not found!
    echo.
    echo Please install from: https://jrsoftware.org/isdl.php
    echo.
    exit /b 1
)

echo [1/3] Found Inno Setup: !ISCC!

:: Step 1: Publish
if %SKIP_PUBLISH%==0 (
    echo.
    echo [2/3] Publishing IdolClick...
    pushd "%SRC_DIR%"
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%PUBLISH_DIR%"
    if errorlevel 1 (
        echo [ERROR] dotnet publish failed!
        popd
        exit /b 1
    )
    popd
    echo       Published to: %PUBLISH_DIR%
) else (
    echo.
    echo [2/3] Skipping publish (using existing build)
)

:: Verify publish output
if not exist "%PUBLISH_DIR%\IdolClick.exe" (
    echo [ERROR] IdolClick.exe not found in publish directory!
    exit /b 1
)

:: Step 2: Create output directory
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: Step 3: Build installer
echo.
echo [3/3] Building installer...
pushd "%SCRIPT_DIR%"
"!ISCC!" "%ISS_FILE%"
if errorlevel 1 (
    echo [ERROR] Installer build failed!
    popd
    exit /b 1
)
popd

:: Find output
echo.
echo ========================================
echo   Build Complete!
echo ========================================
echo.

for %%f in ("%OUTPUT_DIR%\IdolClickSetup-*.exe") do (
    echo Installer: %%f
    for %%s in (%%f) do echo Size: %%~zs bytes
)

echo.
echo Opening output folder...
start "" "%OUTPUT_DIR%"

exit /b 0
