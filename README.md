# ExpressLRS WiFi Joystick for Windows

<div align="center">

**A Windows implementation of the ExpressLRS WiFi Joystick application**

[![.NET](https://img.shields.io/badge/.NET-6.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/6.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

</div>

This application receives UDP packets from your ExpressLRS (ELRS) TX module and creates a virtual joystick using vJoy, allowing you to use your RC transmitter with flight simulators like VelociDrone, Liftoff, DRL Simulator, or any Windows application that supports joystick input.

## ✨ Features

- 🎮 **Virtual Joystick Creation** - Uses vJoy to create Windows-compatible virtual joystick
- 📡 **ExpressLRS Protocol Support** - Full support for ELRS WiFi Joystick protocol (15-bit channel data)
- 🔄 **Real-time Processing** - Low latency (~100Hz update rate) with direct channel mapping
- 🌐 **Auto-discovery** - Automatically detects and connects to ELRS TX modules
- 📊 **Connection Monitoring** - Real-time packet rate monitoring and connection status
- 🛡️ **Graceful Shutdown** - Clean exit with Ctrl+C
- 📦 **Single Executable** - Production builds create self-contained single-file executables

## 🚀 Quick Start

### Option 1: Download Pre-built Release (Recommended)

1. **Download** the latest release from the [Releases page](../../releases)
2. **Install vJoy** from [vjoystick.sourceforge.net](http://vjoystick.sourceforge.net/)
3. **Configure vJoy** with at least 8 axes (X, Y, Z, RX, RY, RZ, Slider0, Slider1)
4. **Extract** the ZIP file and run `ELRSWifiJoystick.exe`
5. **Connect** your ELRS TX module to WiFi

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
2. Run "Configure vJoy" from the Start menu
3. Configure device #1 with at least 8 axes:
   - X, Y, Z, RX, RY, RZ, Slider0, Slider1
4. Enable the device

### ExpressLRS TX Module Setup

1. **Connect to WiFi**:
   - Put your ELRS TX module in WiFi mode
   - Connect to the TX module's WiFi access point OR connect it to your local WiFi network

2. **Access Web Interface**:
   - Open `http://10.0.0.1` or `http://elrs_tx.local` in your browser
   - ELRS should automaticaly send data for WIFI gamepad if connected

## 🎮 Usage

1. **Start the Application**:
   ```bash
   # From source
   dotnet run -c Release
   
   # Or run the executable
   ELRSWifiJoystick.exe
   ```

2. **Verify Connection**:
   - The application will initialize vJoy and start listening on port 11000
   - You'll see connection status and packet rate information
   - Example: `Receiving data: 100 packets/sec from 192.168.1.100`

3. **Use in Flight Simulator**:
   - Open your flight simulator (VelociDrone, Liftoff, DRL Simulator, etc.)
   - Select "vJoy Device" as your controller
   - Calibrate the axes if needed

4. **Exit**: Press `Ctrl+C` for graceful shutdown

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

**Note:** Values are passed directly from ELRS (15-bit range: 0-32767) to vJoy without scaling or filtering for maximum precision and minimal latency.

## 🔧 Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| "vJoy driver not enabled" | Install vJoy driver and enable device #1 in "Configure vJoy" |
| "vJoy Device 1 is already owned" | Close other applications using vJoy device #1 |
| "Connection lost. Waiting for ELRS TX..." | Ensure ELRS TX is connected to WiFi and joystick mode is active |
| No joystick input in simulator | Verify vJoy device is enabled and simulator recognizes "vJoy Device" |

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
- **Protocol**: ExpressLRS WiFi Joystick Protocol (Version 1)
- **Transport**: UDP packets on port 11000
- **Packet Format**:
  - Byte 0: Frame type (1 = channels)
  - Byte 1: Channel count (4-16)
  - Bytes 2+: Channel data (16-bit little-endian per channel)
- **Value Range**: 0-32767 (15-bit precision)
- **Update Rate**: ~100 Hz typical

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

