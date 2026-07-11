# ExpressLRS / TBS Crossfire WiFi Joystick for Windows

<div align="center">

**A Windows implementation of the ExpressLRS & TBS Crossfire WiFi Joystick application**

[![.NET](https://img.shields.io/badge/.NET-6.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

</div>

This application receives UDP packets from your **ExpressLRS (ELRS)** *or* **TBS Crossfire / Tracer** TX module and creates a virtual joystick using vJoy, allowing you to use your RC transmitter with flight simulators like VelociDrone, Liftoff, DRL Simulator, or any Windows application that supports joystick input.

Both radios use the same "WiFi joystick" protocol (the one VelociDrone Mobile speaks), so setup is identical: put the module on your WiFi and run the app. The only difference is the discovery beacon — ELRS announces itself as `ELRS`, Crossfire as `VELOCIDRONE` — and the app handles both automatically.

## ✨ Features

- 🎮 **Virtual Joystick Creation** - Uses vJoy to create Windows-compatible virtual joystick
- 📡 **ELRS + Crossfire Support** - Full support for the ELRS/Crossfire WiFi Joystick protocol (16 channels, 15-bit data)
- 🔄 **Real-time Processing** - Low latency (~90-100Hz update rate) with direct channel mapping
- 🌐 **Auto-discovery** - Automatically detects and activates ELRS *and* TBS Crossfire/Tracer modules
- 🔒 **Single-source lock** - If both an ELRS and a Crossfire module are on the same network, the app locks onto whichever streams first so they can't fight over the joystick
- 📊 **Connection Monitoring** - Real-time packet rate monitoring and connection status
- 🛡️ **Graceful Shutdown** - Clean exit with Ctrl+C
- 📦 **Single Executable** - Production builds create self-contained single-file executables

## 🚀 Quick Start

### Option 1: Download Pre-built Release (Recommended)

1. **Download** the latest release from the [Releases page](../../releases)
2. **Install vJoy** from [vjoystick.sourceforge.net](http://vjoystick.sourceforge.net/)
3. **Extract** the ZIP file and run `ELRSWifiJoystick.exe`
4. **Connect** your ELRS TX module to WiFi

> **Note**: The release package includes everything needed - no .NET installation required!

### Option 2: Build from Source

#### Prerequisites

- **vJoy Driver** - Download from [vjoystick.sourceforge.net](http://vjoystick.sourceforge.net/)
- **.NET 6.0 SDK** - Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/6.0)

#### Build Steps

1. **Clone** this repository

2. **Setup vJoy Libraries**
   - Install vJoy from the official website
   - Copy `vJoyInterfaceWrap.dll` from `C:\Program Files\vJoy\x64\` to the `lib\` folder

3. **Build the Application**
   
   **Option A: Using Build Scripts (Recommended)**
   ```bash
   # Development build (multiple files, good for testing)
   .\build.bat
   
   # Production build (single executable, good for distribution)
   .\publish.bat
   ```
   
   **Option B: Manual Commands**
   ```bash
   # Development build
   dotnet build -c Release
   
   # Production build (single executable)
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```

### Build Scripts Explained

| Script | Purpose | Output | Use Case |
|--------|---------|--------|----------|
| `build.bat` | Development build | Multiple files (DLLs, runtime, etc.) | Testing, debugging, development |
| `publish.bat` | Production build | Single executable + ZIP distribution | Distribution, deployment |

**build.bat** creates a development build with separate files, making it easier to debug and modify.  
**publish.bat** creates a single-file executable perfect for distribution to end users.

## 🔧 Configuration

### vJoy Setup

1. Install vJoy from [vjoystick.sourceforge.net](http://vjoystick.sourceforge.net/)

### ExpressLRS TX Module Setup

1. **Connect to WiFi**:
   - Put your ELRS TX module in WiFi mode
   - Connect to the TX module's WiFi access point OR connect it to your local WiFi network

2. **Access Web Interface**:
   - Open `http://10.0.0.1` or `http://elrs_tx.local` in your browser
   - ELRS should automaticaly send data for WIFI gamepad if connected

### TBS Crossfire / Tracer Setup

Setup is the same as ELRS — the module just needs to be on the same WiFi network. This
uses the "Velocidrone Mobile" support built into the TBS WiFi module (WiFi-module firmware
**v2.17 or later**). **Recommended: WiFi-module firmware v2.25.49mb** — the most stable
build for the Crossfire WiFi joystick. (Any v2.17+ works; the app was also verified
against v3.10.)

1. **Connect to WiFi**:
   - Enable WiFi on the Crossfire/Tracer TX (WiFi module powered).
   - Either connect the module to your local WiFi network (recommended), or connect your PC
     to the module's own access point (`tbs_crossfire_XXXXXXXXXXXX` / `192.168.4.1`).
   - Your PC and the module must be on the same network.

2. **Run the app** — no extra configuration needed:
   - The module continuously broadcasts a `VELOCIDRONE` discovery beacon (~every 8 s). The
     app detects it and automatically sends the activation request, then starts receiving
     channel data.
   - To skip the beacon wait and activate instantly, pass the module's IP:
     `ELRSWifiJoystick.exe --tx 192.168.2.138`
     (find the IP on the module's WiFi web page).

> **How it works:** the app POSTs `action=joystick_begin` to the module's `/udpcontrol`
> endpoint — the same request ELRS uses — and the module streams RC channels over UDP on
> port 11000. No radio-to-FC wiring, MAVLink, or ground-control software is involved; the
> WiFi module sends stick data directly.

## 🎮 Usage

1. **Start the Application**:
   ```bash
   # From source
   dotnet run -c Release
   
   # Or run the executable
   ELRSWifiJoystick.exe

   # Activate a Crossfire module instantly (skip the ~8s beacon wait)
   ELRSWifiJoystick.exe --tx 192.168.2.138

   # Listen on a custom UDP port
   ELRSWifiJoystick.exe 11001
   ```

   | Argument | Description |
   |----------|-------------|
   | `<port>` | UDP listen port (default `11000`) |
   | `--tx <ip>` | Activate the module at this IP immediately instead of waiting for its discovery beacon (also `--crossfire` / `--activate`) |

2. **Verify Connection**:
   - The application will initialize vJoy and start listening on port 11000
   - You'll see connection status and packet rate information
   - Example: `Receiving data: 100 packets/sec from 192.168.1.100`

3. **Use in Flight Simulator**:
   - Open your flight simulator (VelociDrone, Liftoff, DRL Simulator, etc.)
   - Select "vJoy Device" as your controller
   - Calibrate the axes if needed

4. **Exit**: Press `Ctrl+C`. The app centers the virtual joystick axes and releases the
   vJoy device cleanly.

> **Stopping the module's stream:** the ELRS/Crossfire WiFi module has **no remote stop
> command** — once activated it keeps broadcasting channel data until it is powered off,
> rebooted, or its WiFi drops (the same way ELRS WiFi-joystick mode is exited on the radio,
> not the PC). Closing this app only stops *reading* the stream; it does not and cannot stop
> the module from sending. This is a module-firmware behaviour, not an app limitation.

## 📋 Channel Mapping

The application maps ExpressLRS channels to vJoy axes:

| ExpressLRS Channel | vJoy Axis | Description |
|-------------------|-----------|-------------|
| 0 (Roll)          | X         | Aileron/Roll control |
| 1 (Pitch)         | Y         | Elevator/Pitch control |
| 2 (Throttle)      | RX        | Throttle control |
| 3 (Yaw)           | RY        | Rudder/Yaw control |
| 4                 | RZ        | Auxiliary control |
| 5                 | Z         | Auxiliary control |
| 6                 | Slider 0  | Auxiliary control |
| 7                 | Slider 1  | Auxiliary control |

**Note:** Values are passed directly from the module (15-bit range: 0-32767) to vJoy without scaling or filtering for maximum precision and minimal latency. This applies to both ELRS and Crossfire, which share the same channel encoding.

## 🔧 Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| "vJoy driver not enabled" | Install vJoy driver and enable device #1 in "Configure vJoy" |
| "vJoy Device 1 is already owned" | Close other applications using vJoy device #1 |
| "Waiting for joystick data..." | Ensure the TX module is connected to WiFi; for Crossfire, wait for the `VELOCIDRONE` beacon or pass `--tx <module-ip>` |
| No joystick input in simulator | Verify vJoy device is enabled and simulator recognizes "vJoy Device" |
| Crossfire not detected | Confirm the module is on the same network (ping its IP); WiFi-module firmware must be **v2.17+**. Try `--tx <ip>` to activate directly |
| Crossfire axes look wrong/half-throw | Channels are passed through as 15-bit like ELRS. If your radio's output differs, recalibrate the axes in the simulator |

### Network Issues

- **Firewall**: Allow the application through Windows Firewall
- **Network**: Ensure PC and ELRS TX are on the same network
- **Port**: Check that UDP port 11000 is not blocked
- **WiFi**: Verify ELRS TX module is connected to WiFi

### vJoy Issues

- **Installation**: Download vJoy from the official website
- **Permissions**: Run as administrator if vJoy access is denied

## 📊 Technical Details

### Protocol Specification
- **Protocol**: ELRS / TBS Crossfire WiFi Joystick Protocol ("Velocidrone Mobile" link)
- **Discovery**: module broadcasts a beacon on UDP 11000 — `ELRS` (ExpressLRS) or
  `VELOCIDRONE` (TBS Crossfire/Tracer). The app detects it and activates the module.
- **Activation**: HTTP `POST http://<module-ip>/udpcontrol` with form body
  `action=joystick_begin&interval=10000&channels=8`. The module replies `ok` and begins
  streaming. (Activation POSTs are throttled to once per 5 s per module.)
- **Transport**: UDP packets on port 11000
- **Packet Format**:
  - Byte 0: Frame type (1 = channels)
  - Byte 1: Channel count (4-16; Crossfire always sends 16)
  - Bytes 2+: Channel data (16-bit little-endian per channel)
- **Value Range**: 0-32767 (15-bit precision)
- **Update Rate**: ~90-100 Hz typical
- **Single-source lock**: the app binds to the first module that streams real channel data
  and ignores any other source until the bound one is silent for 3 s, so two radios on the
  same network can't interfere.

### Performance Characteristics
- **Latency**: < 10ms from TX to vJoy
- **CPU Usage**: Minimal (< 1% on modern systems)
- **Memory**: ~10MB runtime memory usage
- **Network**: ~1KB/s bandwidth usage

### vJoy Integration
- **Library**: vJoyInterfaceWrap.dll for Windows virtual joystick creation
- **Axes**: Supports up to 8 axes per vJoy device
- **Processing**: Direct pass-through of values for minimal latency
- **Filtering**: No smoothing or filtering applied (raw input for precise control)

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request. For major changes, please open an issue first to discuss what you would like to change.


### Reporting Issues

When reporting issues, please include:
- Windows version
- vJoy version
- ELRS firmware version
- Steps to reproduce
- Console output/logs

## 🙏 Acknowledgments

- [ExpressLRS](https://github.com/ExpressLRS/ExpressLRS) - The amazing open-source RC link system
- [vJoy](http://vjoystick.sourceforge.net/) - Virtual joystick driver for Windows
- .NET Community - For the excellent development platform

---

<div align="center">

**Made with ❤️ for the ExpressLRS community**

[⭐ Star this repository](../../stargazers) if you find it helpful!

</div>

