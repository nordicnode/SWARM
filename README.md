# SWARM

SWARM is a cross-platform, peer-to-peer file synchronization and transfer application designed for local area networks (LAN). It enables secure, high-performance file sharing and folder mirroring between devices without relying on external cloud servers or internet connectivity.

## Overview

SWARM automatically discovers other instances on your local network—even across VLANs or restrictive corporate environments—allowing you to:

*   **Sync Folders:** Keep a specific folder identical across multiple devices in real-time.
*   **Direct Transfer:** Send individual files directly to peers with a simple drag-and-drop interface.
*   **Secure & Private:** All data is End-to-End Encrypted (E2E). No cloud, no accounts, no subscriptions.

## Tech Stack

| Component | Technology |
|-----------|------------|
| **UI Framework** | [Avalonia UI](https://avaloniaui.net/) (Cross-platform) |
| **Charts** | [LiveCharts2](https://livecharts.dev/) (SkiaSharp) |
| **Core Library** | .NET 9.0 |
| **Platforms** | Windows, macOS, Linux |
| **Security** | AES-256-GCM, ECDH, ECDSA |

## Key Features

### 🚀 High-Performance Sync

*   **Delta Compression:** For large files (>1MB), calculates rolling checksums (Adler-32 + SHA-256) and transfers only changed blocks.
*   **Parallel Transfers:** Uses a connection pool to open multiple TCP streams per peer.
*   **Compression:** All transfers are transparently compressed using Brotli.
*   **Smart Rename Detection:** Intelligently groups file moves into atomic "Directory Rename" operations.

### 🛡️ Enterprise-Grade Security

*   **End-to-End Encryption:** All traffic uses AES-256-GCM authenticated encryption.
*   **Forward Secrecy:** Sessions use ephemeral ECDH key exchange.
*   **Trust-On-First-Use (TOFU):** Devices are identified by ECDSA identity keys.
*   **Secure Pairing:** 6-digit pairing codes with automatic key exchange.

### 💾 Data Safety & Integrity

*   **Versioning:** Maintains local history of file versions in `.swarm-versions`. Browse, diff, and restore via UI.
*   **Conflict Resolution:** Configurable modes (Auto-Newest, Keep Both, Always Local/Remote, Ask User).
*   **Conflict History:** Track and review all resolved conflicts with resolution details.
*   **Integrity Auditing:** Built-in scanner detects "bit rot" or corruption.
*   **.swarmignore:** Exclude files using git-style patterns.

### 📊 Monitoring & Analytics

*   **Bandwidth Dashboard:** Real-time upload/download speed graphs with 60-second history.
*   **Transfer Tracking:** View active transfers with progress, peak speeds, and session totals.
*   **Activity Log:** Comprehensive history of all sync events with filtering and export.

### 🖥️ Modern User Interface

*   **Dark Theme:** Modern, polished dark interface with glassmorphism effects.
*   **Keyboard Shortcuts:**
    *   `F5` - Refresh current view
    *   `Ctrl+,` - Open Settings
    *   `Delete` - Delete selected files
    *   `Ctrl+F` - Focus search/filter
*   **Drag & Drop:** Send files by dragging onto peer cards.
*   **System Tray:** Quick access to Activity Log, Conflict History, Bandwidth Monitor.
*   **Loading States:** Skeleton screens and progress indicators throughout.
*   **Delete Confirmation:** Safe deletion with confirmation dialogs.

### 🌐 Universal Connectivity

*   **Dual-Stack Discovery:** UDP Broadcast + mDNS for corporate networks/VLANs.
*   **IPv6 Support:** Full dual-stack networking.
*   **Offline Indicator:** Visual feedback when network is unavailable.

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

### Build Scripts (Windows PowerShell)

```powershell
# Build for Windows
.\Build-Project.ps1

# Build for Linux
.\Build-Project.ps1 -Platform linux-x64

# Build for all platforms
.\Build-Project.ps1 -All

# Portable mode (settings saved next to executable)
.\Build-Project.ps1 -Portable

# Clean build artifacts
.\Clean-Project.ps1

# Clean including publish folder
.\Clean-Project.ps1 -IncludePublish
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
> SWARM supports "Portable Mode". Create a `portable.marker` file next to the executable to save settings locally.

## Usage

### Syncing Files

*   On first launch, SWARM creates a folder at `Documents/SWARM/Synced`.
*   Files placed here are automatically encrypted, compressed, and synced to trusted peers.
*   **Conflict Handling:** Configurable - uses "Last Write Wins" by default but archives conflicting copies to Version History.

### Version Control

1.  Right-click a file → **View History** to see all versions.
2.  Use **Visual Diff** to compare versions before restoring.
3.  Restore any previous version with one click.

### Bandwidth Monitoring

*   Click **Bandwidth** in the sidebar to view real-time transfer speeds.
*   Monitor active uploads/downloads with progress bars.
*   View transfer history with average speeds.

### Conflict Management

*   **Settings → Conflict Resolution** to configure behavior.
*   View resolved conflicts via **Activity Log** or **Conflict History** in system tray.

## Configuration

Settings are accessible via the **Settings** view:

*   **Device Name:** Your visible network alias.
*   **Sync Folder:** Choose your sync directory.
*   **Trusted Peers:** Devices allowed to auto-sync.
*   **Excluded Folders:** Selective sync to ignore specific subdirectories.
*   **Bandwidth Limits:** Upload/Download speed caps.
*   **Versioning:** Configure retention period and max versions per file.
*   **Conflict Resolution:** Choose auto-resolution strategy.
*   **Close to Tray:** Minimize instead of exit on close.

## Project Structure

```
swarm/
├── Swarm.sln             # Solution file
├── Swarm.Core/           # Cross-platform library (models, services)
├── Swarm.Core.Tests/     # Unit tests
├── Swarm.Avalonia/       # Avalonia UI application
├── Build-Project.ps1     # Build script
└── Clean-Project.ps1     # Cleanup script
```

## Core Services

| Service | Purpose |
|---------|---------|
| `DiscoveryService` | UDP/mDNS peer discovery |
| `TransferService` | File transfer with connection pooling |
| `SyncService` | Real-time folder synchronization |
| `VersioningService` | File history and restore |
| `ConflictResolutionService` | Conflict detection and resolution |
| `BandwidthTrackingService` | Real-time speed monitoring |
| `ActivityLogService` | Event logging and filtering |
| `CryptoService` | Encryption/decryption operations |
| `PairingService` | Secure device pairing |

## Technical Details

*   **Discovery:** UDP 37420 + mDNS `_swarm._tcp`
*   **Transport:** TCP with AES-256-GCM
*   **Protocol:** Custom binary protocol with length-prefix framing
*   **Framework:** Avalonia UI / .NET 9.0
*   **Charts:** LiveCharts2 with SkiaSharp rendering

## License

MIT License
