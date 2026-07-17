@echo off
REM Publish the lightweight CLI version (trimmed, ~9 MB) and zip it.
echo Building CLI (trimmed single-file)...
dotnet publish ELRSWifiJoystick.Cli -c Release

echo.
echo Creating distribution ZIP...
powershell -Command "Compress-Archive -Path 'ELRSWifiJoystick.Cli\bin\Release\net6.0-windows\win-x64\publish\ELRSWifiJoystickCli.exe' -DestinationPath 'ELRSWifiJoystickCli_v3.0.zip' -Force"

echo.
echo Distribution package created:
echo - ELRSWifiJoystickCli_v3.0.zip (~9 MB, no .NET installation required)
echo - Run: ELRSWifiJoystickCli.exe --help
echo.
pause
