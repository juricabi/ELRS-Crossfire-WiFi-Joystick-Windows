@echo off
REM Publish as self-contained single-file application
echo Building self-contained single-file package...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin\Release\net6.0\win-x64\publish

echo.
echo Done! Self-contained package created at:
echo bin\Release\net6.0\win-x64\publish\
echo.
echo Contents:
echo - ELRSWifiJoystick.exe (main executable)
echo - vJoyInterface.dll (native driver)
echo - vJoyInterfaceWrap.dll (wrapper)
echo - All .NET runtime files (no installation needed)
echo.
echo Creating distribution ZIP...
powershell -Command "Compress-Archive -Path 'bin\Release\net6.0\win-x64\publish\*' -DestinationPath 'ELRSWifiJoystick_v1.0.zip' -Force"

echo.
echo Distribution package created:
echo - ELRSWifiJoystick_v1.0.zip (~11MB compressed)
echo - Contains everything needed (no .NET installation required)
echo - Users only need to install vJoy driver from vjoystick.sourceforge.net
echo.
pause

