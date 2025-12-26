# Swarm

Swarm is a lightweight, peer-to-peer file synchronization and transfer application designed for local area networks (LAN). It enables seamless file sharing and folder mirroring between devices without relying on external cloud servers or internet connectivity.

## Overview

Swarm automatically discovers other instances on your local network, allowing you to:
- **Sync Folders**: Keep a specific folder identical across multiple devices in real-time.
- **Direct Transfer**: Send individual files directly to peers with a simple drag-and-drop interface.
- **Secure & Private**: All data stays on your local network. No cloud, no accounts, no subscriptions.

## Features

- **Zero-Config Discovery**: Automatically finds other Swarm devices on the LAN using UDP broadcasting.
- **Real-Time Synchronization**: Monitors the synced folder and propagates changes (creates, updates, deletes) instantly.
- **Smart Conflict Resolution**: Uses a "Last Write Wins" strategy combined with content hashing to handle file conflicts and prevent corruption.
- **Resumable Transfers**: Uses TCP for reliable file transport with error handling.
- **Trusted Peers**: distinct trusted devices to auto-accept transfers.
- **Minimalist UI**: Clean WPF interface that stays out of your way (System Tray support included).

## Getting Started

### Prerequisites

- Windows 10/11
- .NET 8.0 Runtime (or SDK to build)

### Installation

1. Clone the repository.
   ```bash
   git clone https://github.com/your-org/swarm.git
   ```
2. Build the solution using Visual Studio or the .NET CLI.
   ```bash
   dotnet build
   ```
3. Run the application.
   ```bash
   dotnet run --project Swarm
   ```

## Usage

### Syncing Files
1. Upon first launch, Swarm creates a folder at `Documents/SWARM/Synced`.
2. Any file or folder you place in this directory will be automatically copied to all other connected peers running Swarm.
3. Deleting a file from this folder will delete it from all peers.

### Manual File Transfer
1. Open the Swarm window to see a list of discovered peers.
2. Drag and drop a file onto a peer's name.
3. If the peer accepts the request, the file will be transferred to their `Downloads/Swarm` folder.

## Configuration

Swarm settings are accessible via the Settings icon in the application. Key configurations include:

- **Device Name**: How you appear to others on the network.
- **Sync Folder**: Change the location of your synced directory.
- **Download Path**: Set where manually received files are saved.
- **Trusted Peers**: Add devices to your trusted list to bypass acceptance dialogs.
- **Start Minimized**: Check to have Swarm start silently in the system tray.

## Technical Details

- **Discovery Port**: UDP `37420`
- **File Transfer**: TCP (Ephemeral ports)
- **Settings Storage**: `AppData/Roaming/Swarm/settings.json`
- **Framework**: WPF / .NET 8.0

## License

[MIT License](LICENSE)
