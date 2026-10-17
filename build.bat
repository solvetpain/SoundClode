@echo off
setlocal
cd /d "%~dp0"

echo ===================================================
echo   Building SoundClode Native Client (.NET 6 + WPF)
echo ===================================================
echo.

dotnet publish "%~dp0SoundCloudNative\SoundCloudNative.csproj" -c Release -o "%~dp0publish"

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Build failed. Please ensure .NET 6.0 SDK is installed.
    echo Download: https://dotnet.microsoft.com/download/dotnet/6.0
    echo.
    pause
    exit /b %errorlevel%
)

echo.
echo Syncing web files into publish directory...
copy /y "%~dp0index.html" "%~dp0publish\index.html" >nul
copy /y "%~dp0tracks.js" "%~dp0publish\tracks.js" >nul

echo.
echo ===================================================
echo   [SUCCESS] Build completed!
echo   Launching SoundClode...
echo ===================================================
echo.

start "" "%~dp0publish\SoundCloudNative.exe"
echo Press any key to exit...
pause >nul
exit /b 0
