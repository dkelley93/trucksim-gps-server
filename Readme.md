# TruckSim GPS PC Telemetry

The open-source companion telemetry server for the [TruckSim GPS](https://trucksimgps.com/) mobile app. It runs in the background while you play **Euro Truck Simulator 2** or **American Truck Simulator**, reading live game data and making it available to the mobile app on your local network.

The telemetry plugin that reads game data is also open source under the MIT license: [trucksim-gps-plugin](https://github.com/TruckSim-GPS/trucksim-gps-plugin).

## System Requirements

- **Windows 10 or 11** (64-bit)

## How to Use

1. **Download** the latest `TruckSimGPS_PC_Telemetry_Setup_*.exe` from [Releases](https://github.com/TruckSim-GPS/trucksim-gps-server/releases)
2. **Run the installer**, which will:
   - Install the VC++ Redistributable if needed
   - Add a firewall rule to allow local network communication
   - Optionally create a desktop shortcut and enable start with Windows
3. **Launch** TruckSim GPS PC Telemetry
4. **First-time setup:** The program will auto-detect your game installations and copy the required telemetry plugins
5. **Keep PC Telemetry running** while playing ETS2/ATS

The app checks for updates automatically on launch. You can also check manually via **Help > Check for Updates**.

## Troubleshooting

If you're having connection problems, see the [Connection help](https://trucksimgps.com/connect-help) page on our website.

You can also test connectivity by opening `http://<server-ip>:31377/` in a browser on any device on your network. If it doesn't load, check your PC's firewall settings.

## Privacy

Telemetry is served over the local network. To pair a device by QR code or by the 6-character code, and to reconnect a paired device when the local network fails, the server also talks to the TruckSim GPS cloud (`api.trucksimgps.com`): it sends a random PC id created on first run, and the PC name and local network addresses inside a blob encrypted with the pairing code, which the cloud cannot read. When a paired device cannot reach this PC directly, the telemetry travels through a TruckSim GPS relay server, encrypted end to end with a key only the two devices hold; the relay forwards bytes it cannot decrypt and stores nothing. Linked devices and their keys stay in the local settings file. No account is involved, nothing is tied to a person, and the update check against GitHub's public API sends no user-identifiable information.

## Building from Source

```bash
# Restore NuGet packages
nuget restore source/Funbit.Ets.Telemetry.sln

# Build
msbuild source/Funbit.Ets.Telemetry.sln /p:Configuration=Release
```

The output will be in `source/Funbit.Ets.Telemetry.Server/bin/Release/`.

## Project Links

- **Official Website:** https://trucksimgps.com/
- **Support us on Patreon:** https://www.patreon.com/TruckSimGPS
- **Join our Discord:** https://discord.gg/RdC99Er37U (discussion, support, feature requests)

---

This project is built upon the foundational work of the [Funbit ETS2 Telemetry Server](https://github.com/Funbit/ets2-telemetry-server).
Open source software licensed under **GPL-3.0**. Anyone can read the source code and verify for themselves that it's safe.
