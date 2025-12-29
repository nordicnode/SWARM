# SWARM Security Whitepaper

This document provides a transparent, technical overview of SWARM's security architecture. We believe security through obscurity is no security at all—users deserve to understand exactly how their data is protected.

## Table of Contents

1. [Transport Security](#transport-security)
2. [Encrypted Folders (Vaults)](#encrypted-folders-vaults)
3. [Peer Authentication](#peer-authentication)
4. [Key Management](#key-management)
5. [Threat Model](#threat-model)

---

## Transport Security

All peer-to-peer communication in SWARM is encrypted using authenticated encryption.

### Protocol Stack

| Layer | Technology |
|-------|------------|
| Transport | TCP with TLS-like handshake |
| Encryption | AES-256-GCM |
| Key Exchange | ECDH (Elliptic Curve Diffie-Hellman) |
| Identity | ECDSA (Elliptic Curve Digital Signature Algorithm) |

### Session Establishment

1. **Handshake**: Peers exchange ephemeral ECDH public keys.
2. **Key Derivation**: A shared secret is derived using ECDH.
3. **Session Key**: The shared secret is used to derive AES-256 session keys.
4. **Forward Secrecy**: Each session uses new ephemeral keys, so compromising one session does not compromise past sessions.

### Wire Format

All messages use length-prefix framing:
```
[4 bytes: length][12 bytes: nonce][N bytes: ciphertext][16 bytes: GCM tag]
```

---

## Encrypted Folders (Vaults)

Encrypted folders provide at-rest encryption for sensitive files. Even if an attacker gains access to your sync folder, they cannot read the contents without the password.

### Cryptographic Primitives

| Component | Algorithm | Parameters |
|-----------|-----------|------------|
| Key Derivation | PBKDF2-HMAC-SHA256 | 100,000 iterations |
| Encryption | AES-256-GCM | 256-bit key, 96-bit nonce |
| Salt | Random | 128 bits (16 bytes) |

### Password-Based Key Derivation

```
salt = RandomBytes(16)
key = PBKDF2(password, salt, iterations=100000, hash=SHA256, keyLength=32)
```

- **Why PBKDF2?** It's a well-audited, NIST-recommended algorithm that slows brute-force attacks.
- **Why 100,000 iterations?** This provides ~100ms of computation time per guess, making offline attacks impractical for strong passwords.
- **Salt Storage**: The salt is stored in `.swarm-vault/config.json`. It is not secret—its purpose is to prevent rainbow table attacks.

### Password Verification

We never store the password or a hash of it. Instead, we store an encrypted "verifier":

```
verifier = AES-GCM-Encrypt(key, "SWARM-VAULT-VERIFY-2024")
```

On unlock, we attempt to decrypt the verifier. If decryption succeeds and produces the expected plaintext, the password is correct.

### File Encryption

Files are encrypted using 32KB chunked AES-256-GCM:

```
Header: [4 bytes: "SENC"][2 bytes: version][2 bytes: chunkSizeKB]

For each 32KB chunk:
    nonce = RandomBytes(12)
    ciphertext, tag = AES-GCM-Encrypt(key, nonce, chunk)
    Write: [4 bytes: length][12 bytes: nonce][ciphertext][16 bytes: tag]
```

**Why Chunking?**
- Allows streaming decryption of large files without loading entire file into memory.
- Each chunk has a unique nonce, preventing nonce reuse.
- 32KB aligns with typical filesystem block sizes.

### Filename Obfuscation

To prevent metadata leakage (e.g., `Layoffs_2025.xlsx` revealing intent), filenames are obfuscated:

| Original | Stored As |
|----------|-----------|
| `Budget.xlsx` | `7f8a9d3c1b2e.senc` |
| `TaxReturns.pdf` | `a1b2c3d4e5f6.senc` |

The mapping is stored in an encrypted manifest (`.swarm-vault/manifest.senc`), which is itself encrypted with the vault key.

### Auto-Lock

Vaults automatically lock after 15 minutes of inactivity (configurable). On lock:
1. The cached key is zeroed from memory (`Array.Clear`).
2. The vault state is set to "locked".
3. Files remain encrypted on disk.

---

## Peer Authentication

SWARM uses a Trust-On-First-Use (TOFU) model with optional secure pairing.

### Identity Keys

Each SWARM instance generates an ECDSA P-256 key pair on first launch:
- **Private Key**: Stored in platform-specific secure storage (DPAPI on Windows, Keychain on macOS).
- **Public Key**: Shared during peer discovery.

### Trust Establishment

**Option 1: Manual Trust**
Users manually approve peers after visual verification of the device name.

**Option 2: Secure Pairing (Recommended)**
1. Device A displays a 6-digit pairing code.
2. User enters the code on Device B.
3. Devices exchange public keys over an encrypted channel.
4. Both devices verify the code matches.

### Message Authentication

All sync messages are signed with the sender's ECDSA private key. Recipients verify signatures using the sender's trusted public key.

---

## Key Management

### Storage Locations

| Platform | Key Storage |
|----------|-------------|
| Windows | DPAPI (Data Protection API) |
| macOS | Keychain |
| Linux | File with restricted permissions (`~/.config/swarm/`) |
| Portable Mode | `keys.json` next to executable (encrypted with machine key) |

### Key Hierarchy

```
Identity Key (ECDSA P-256)
└── Used for: Peer authentication, message signing

Session Keys (AES-256, ephemeral)
└── Used for: Transport encryption (one per session)

Vault Keys (AES-256, derived from password)
└── Used for: At-rest file encryption
```

---

## Threat Model

### What SWARM Protects Against

| Threat | Protection |
|--------|------------|
| Network eavesdropping | AES-256-GCM transport encryption |
| Man-in-the-middle | ECDH key exchange + optional pairing codes |
| Stolen sync folder | Encrypted folders (vaults) with AES-256-GCM |
| Brute-force password attacks | PBKDF2 with 100k iterations |
| Rainbow table attacks | Unique random salt per vault |
| Metadata leakage | Filename obfuscation |

### What SWARM Does NOT Protect Against

| Threat | Limitation |
|--------|------------|
| Compromised endpoint | If malware has access to your unlocked vault, it can read files. |
| Weak passwords | PBKDF2 slows attacks but cannot compensate for "password123". |
| Physical access while unlocked | An attacker with physical access to an unlocked machine can read files. |
| Key compromise | If your identity key is stolen, an attacker can impersonate you. |

### Recommendations

1. **Use strong vault passwords** (12+ characters, mixed case, numbers, symbols).
2. **Enable auto-lock** with a short timeout for sensitive vaults.
3. **Use secure pairing** instead of manual trust for new devices.
4. **Keep your OS updated** to protect against endpoint compromise.

---

## Cryptographic Library

SWARM uses the .NET cryptography libraries (`System.Security.Cryptography`), which are FIPS-compliant implementations provided by the operating system:
- Windows: CNG (Cryptography Next Generation)
- macOS/Linux: OpenSSL

No custom cryptographic implementations are used.

---

## Audit & Transparency

This document and the full source code are available for review. We welcome security audits and responsible disclosure of vulnerabilities.

**Contact**: [Create an issue on GitHub]

---

*Last Updated: December 2024*
