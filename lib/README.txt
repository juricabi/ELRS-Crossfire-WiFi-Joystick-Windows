vJoy Library Setup Instructions
==============================

This folder should contain vJoyInterfaceWrap.dll and vJoyInterface.dll, required to build.

To obtain this file:
1. Install vJoy from: http://vjoystick.sourceforge.net/
2. Copy vJoyInterfaceWrap.dll and vJoyInterface.dll from: C:\Program Files\vJoy\x64
3. Paste them into this lib folder

The bundled vJoyInterface.dll is only a fallback. At runtime the app loads the
vJoyInterface.dll from the user's vJoy install (it must match their driver - a
mismatched DLL acquires the device but never moves it), so any vJoy version works here.

The application will not build without these files.

