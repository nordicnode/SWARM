# Swarm

Swarm is a production-grade, peer-to-peer file synchronization and transfer application designed for local area networks (LAN). It enables secure, high-performance file sharing and folder mirroring between devices without relying on external cloud servers or internet connectivity.

## Overview

Swarm automatically discovers other instances on your local network—even across VLANs or restrictive corporate environments—allowing you to:

*   **Sync Folders:** Keep a specific folder identical across multiple devices in real-time.
*   **Direct Transfer:** Send individual files directly to peers with a simple drag-and-drop interface.
*   **Secure & Private:** All data is End-to-End Encrypted (E2E). No cloud, no accounts, no subscriptions.

## Key Features

### 🚀 High-Performance Sync

*   **Delta Compression ("Rsync-style"):** For large files (>1MB), Swarm calculates rolling checksums (Adler-32 + SHA-256) and transfers only the changed blocks, drastically reducing bandwidth usage.
*   **Parallel Transfers:** Uses a connection pool to open multiple TCP streams per peer, eliminating bottlenecks when syncing thousands of small files.
*   **Compression:** All transfers are transparently compressed using Brotli to maximize throughput.
*   **Smart Rename Detection:** Intelligently groups file moves into atomic "Directory Rename" operations to prevent network storms.

### 🛡️ Enterprise-Grade Security

*   **End-to-End Encryption:** All traffic is wrapped in a custom `SecureStream` using AES-256-GCM for authenticated encryption.
*   **Forward Secrecy:** Sessions are established using ephemeral ECDH key exchange.
*   **Trust-On-First-Use (TOFU):** Devices are identified by cryptographically generated ECDSA identity keys.

### 💾 Data Safety & Integrity

*   **Versioning ("Time Machine"):** Accidental overwrites are safe. Swarm maintains a local history of file versions in a hidden `.swarm-versions` folder. You can browse, diff, and restore previous versions via the UI.
*   **Integrity Auditing:** Built-in integrity scanner re-computes file hashes to detect "bit rot" or corruption.
*   **.swarmignore Support:** Exclude specific files or folders (e.g., `node_modules`, `*.tmp`) using standard git-style patterns.

### 🌐 Universal Connectivity

*   **Dual-Stack Discovery:** Combines traditional UDP Broadcast (for home LANs) with mDNS / Multicast DNS (for corporate networks/VLANs), ensuring peers find each other where other apps fail.

## Getting Started

### Prerequisites

*   Windows 10/11
*   .NET 9.0 Runtime

### Installation

1.  Clone the repository.
    ```bash
    git clone https://github.com/your-org/swarm.git
    ```
2.  Build the solution.
    ```bash
    dotnet build -c Release
    ```
3.  Run the application.
    ```bash
    dotnet run --project Swarm
    ```

> [!NOTE]
> Swarm supports a "Portable Mode". Create a file named `portable.marker` next to the executable to save settings locally instead of in `AppData`.

## Usage

### Syncing Files

*   Upon first launch, Swarm creates a folder at `Documents/SWARM/Synced`.
*   Any file placed here is automatically encrypted, compressed, and synced to trusted peers.
*   **Conflict Handling:** Swarm uses a "Last Write Wins" strategy but always archives the conflicting local copy to the Version History, ensuring no data is ever lost.

### Version Control

1.  Right-click the tray icon or open the main window.
2.  Click "History" to view the timeline of changes.
3.  Select a file to see a **Visual Diff** (color-coded comparison) of exactly what changed before restoring.

## Configuration

Swarm settings are accessible via the **Settings** icon:

*   **Device Name:** Your visible network alias.
*   **Trusted Peers:** Distinct devices that are allowed to auto-sync.
*   **Bandwidth Limits:** Set optional Upload/Download speed caps to prevent network saturation.
*   **Portable Mode:** Run entirely from a USB stick.

## Technical Details

*   **Discovery:** UDP 37420 + mDNS `_swarm._tcp`
*   **Transport:** TCP (Dynamic/Ephemeral ports) with AES-256-GCM
*   **Protocol:** Custom binary protocol with length-prefix framing
*   **Framework:** WPF / .NET 9.0

## License

MIT License
