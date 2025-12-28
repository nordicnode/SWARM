# Swarm

Swarm is a cross-platform, peer-to-peer file synchronization and transfer application designed for local area networks (LAN). It enables secure, high-performance file sharing and folder mirroring between devices without relying on external cloud servers or internet connectivity.

## Overview

Swarm automatically discovers other instances on your local network—even across VLANs or restrictive corporate environments—allowing you to:

*   **Sync Folders:** Keep a specific folder identical across multiple devices in real-time.
*   **Direct Transfer:** Send individual files directly to peers with a simple drag-and-drop interface.
*   **Secure & Private:** All data is End-to-End Encrypted (E2E). No cloud, no accounts, no subscriptions.

## Tech Stack

| Component | Technology |
|-----------|------------|
| **UI Framework** | [Avalonia UI](https://avaloniaui.net/) (Cross-platform) |
| **Core Library** | .NET 9.0 |
| **Platforms** | Windows, macOS, Linux |
| **Security** | AES-256-GCM, ECDH, ECDSA |

## Key Features

### 🚀 High-Performance Sync

*   **Delta Compression:** For large files (>1MB), Swarm calculates rolling checksums (Adler-32 + SHA-256) and transfers only the changed blocks.
*   **Parallel Transfers:** Uses a connection pool to open multiple TCP streams per peer.
*   **Compression:** All transfers are transparently compressed using Brotli.
*   **Smart Rename Detection:** Intelligently groups file moves into atomic "Directory Rename" operations.

### 🛡️ Enterprise-Grade Security

*   **End-to-End Encryption:** All traffic uses AES-256-GCM authenticated encryption.
*   **Forward Secrecy:** Sessions use ephemeral ECDH key exchange.
*   **Trust-On-First-Use (TOFU):** Devices are identified by ECDSA identity keys.

### 💾 Data Safety & Integrity

*   **Versioning:** Maintains local history of file versions in `.swarm-versions`. Browse, diff, and restore via UI.
*   **Integrity Auditing:** Built-in scanner detects "bit rot" or corruption.
*   **.swarmignore:** Exclude files using git-style patterns.

### 🌐 Universal Connectivity

*   **Dual-Stack Discovery:** UDP Broadcast + mDNS for corporate networks/VLANs.

## Getting Started

### Prerequisites

*   .NET 9.0 SDK

### Supported Platforms

| Platform | Status |
|----------|--------|
| Windows 10/11 | ✅ Fully supported |
| macOS (Intel & Apple Silicon) | ✅ Supported |
| Linux (x64, ARM64) | ✅ Supported |

### Installation

1.  Clone the repository:
    ```bash
    git clone https://github.com/your-org/swarm.git
    cd swarm
    ```

2.  Build the solution:
    ```bash
    dotnet build -c Release
    ```

3.  Run the application:
    ```bash
    dotnet run --project Swarm.Avalonia
    ```

### Publishing for Different Platforms

```bash
# Windows
dotnet publish Swarm.Avalonia -c Release -r win-x64 --self-contained

# macOS (Apple Silicon)
dotnet publish Swarm.Avalonia -c Release -r osx-arm64 --self-contained

# macOS (Intel)
dotnet publish Swarm.Avalonia -c Release -r osx-x64 --self-contained

# Linux
dotnet publish Swarm.Avalonia -c Release -r linux-x64 --self-contained
```

> [!NOTE]
> Swarm supports "Portable Mode". Create a `portable.marker` file next to the executable to save settings locally.

## Usage

### Syncing Files

*   On first launch, Swarm creates a folder at `Documents/SWARM/Synced`.
*   Files placed here are automatically encrypted, compressed, and synced to trusted peers.
*   **Conflict Handling:** Uses "Last Write Wins" but archives conflicting copies to Version History.

### Version Control

1.  Open Settings → Activity Log to view changes.
2.  Select a file to see a **Visual Diff** before restoring.

## Configuration

Settings are accessible via the **Settings** view:

*   **Device Name:** Your visible network alias.
*   **Sync Folder:** Choose your sync directory.
*   **Trusted Peers:** Devices allowed to auto-sync.
*   **Bandwidth Limits:** Upload/Download speed caps.
*   **Versioning:** Configure retention and max versions.
*   **Close to Tray:** Minimize instead of exit on close.

## Project Structure

```
swarm/
├── Swarm.sln           # Solution file
├── Swarm.Core/         # Cross-platform library (models, services)
└── Swarm.Avalonia/     # Avalonia UI application
```

## Technical Details

*   **Discovery:** UDP 37420 + mDNS `_swarm._tcp`
*   **Transport:** TCP with AES-256-GCM
*   **Protocol:** Custom binary protocol with length-prefix framing
*   **Framework:** Avalonia UI / .NET 9.0

## License

MIT License
