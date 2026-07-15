@echo off
echo Building ExpressLRS WiFi Joystick for Windows...
echo.

if not exist "lib\vJoyInterfaceWrap.dll" (
    echo ERROR: vJoyInterfaceWrap.dll not found in lib\ folder
    echo Please copy it from your vJoy installation:
    echo    C:\Program Files\vJoy\x64\vJoyInterfaceWrap.dll
    echo.
    pause
    exit /b 1
)

if not exist "C:\Program Files\vJoy\x64\vJoyInterface.dll" (
    echo ERROR: vJoyInterface.dll not found in vJoy installation
    echo Please ensure vJoy is properly installed from:
    echo    http://vjoystick.sourceforge.net/
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

echo Copying vJoy native DLLs to output directory...
copy "C:\Program Files\vJoy\x64\vJoyInterface.dll" "bin\Release\net8.0-windows\" >nul
copy "C:\Program Files\vJoy\x64\vJoyInterface.dll" "bin\Debug\net8.0-windows\" >nul

echo.
echo Build successful!
echo Run with: dotnet run
echo Or use: bin\Release\net8.0-windows\ELRSWifiJoystick.exe
echo.
pause

