@echo off
echo Building ELRS / TBS Crossfire WiFi Joystick for Windows...
echo.

if not exist "lib\vJoyInterfaceWrap.dll" (
    echo ERROR: vJoyInterfaceWrap.dll not found in lib\ folder
    echo Please copy it from your vJoy installation:
    echo    C:\Program Files\vJoy\x64\vJoyInterfaceWrap.dll
    echo.
    pause
    exit /b 1
)

if not exist "lib\vJoyInterface.dll" (
    echo ERROR: vJoyInterface.dll not found in lib\ folder
    echo Please copy it from your vJoy installation:
    echo    C:\Program Files\vJoy\x64\vJoyInterface.dll
    echo.
    pause
    exit /b 1
)

dotnet build -c Release
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build failed!
    pause
    exit /b 1
)

echo.
echo Build successful!
echo Run: bin\Release\net6.0-windows\win-x64\ELRSWifiJoystick.exe
echo.
pause
