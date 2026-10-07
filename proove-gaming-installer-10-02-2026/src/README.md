# src

All production code. The solution file is `src/ProveGaming.slnx` (note the single "o").

| Project | What it is |
|---------|------------|
| `ProoveGaming.App` | The desktop app (Avalonia). |
| `ProoveGaming.DeviceHost` | Windows service (`LocalSystem`) that owns HID and Bluetooth device I/O and serves it to the App over gRPC. |
| `ProoveGaming.Infrastructure` | Device protocols and HID/Bluetooth abstractions shared by the App and the DeviceHost. |
| `ProoveGaming.GrpcDependencies` | The gRPC contract (`Protos/deviceservice.proto`) both sides are generated from. |
| `ProoveGaming.Common` | UI plumbing and install layout shared by the App, the installer and the uninstaller. |
| `ProoveGaming.Installer` | Single-EXE installer; embeds the App, the DeviceHost and the uninstaller. |
| `ProoveGaming.Uninstaller` | Uninstaller copied into the install folder and registered with Windows. |
| `ProoveGaming.Device.Testing` | Developer console of hardware protocol probes. Not shipped. |
| `ProoveGaming.DeviceHost.Checker` | Developer console that polls the DeviceHost's gRPC endpoint. Not shipped. |

Tests live in `../tests/`, not here. Build and test commands are in the root `README.md`; the architecture and coding rules are in `CLAUDE.md`.
