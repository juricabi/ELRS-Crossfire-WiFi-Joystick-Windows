@echo off
REM Publish as a self-contained single-file application and zip it for distribution.
echo Building self-contained single-file package...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\Release\net6.0-windows\win-x64\publish

echo.
echo Done! Self-contained package created at:
echo bin\Release\net6.0-windows\win-x64\publish\
echo.
echo Contents:
echo - ELRSWifiJoystick.exe (single file; .NET runtime and vJoy wrapper embedded)
echo.
echo Creating distribution ZIP...
powershell -Command "Compress-Archive -Path 'bin\Release\net6.0-windows\win-x64\publish\*' -DestinationPath 'ELRSWifiJoystick_v2.0.1.zip' -Force"

echo.
echo Distribution package created:
echo - ELRSWifiJoystick_v2.0.1.zip (~60MB, no .NET installation required)
echo - Users only need to install the vJoy driver from vjoystick.sourceforge.net
echo.
pause
