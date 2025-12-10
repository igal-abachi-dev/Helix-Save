# Helix
**Helix** is a high-performance, atomic, and tamper-evident binary persistence system designed for .NET  applications/games. 
can save c# class to binary file


It combines LZ4 compression with HMAC-SHA256 integrity checks to ensure save data is small, fast, 
and immune to corruption or external modification.

Designed for reliability under power-loss conditions (ACID-compliant file swapping)
and ease of long-term maintenance (forward-compatible versioning).

---

## ⚡ Core Features

*   **Atomic Durability:** Uses a transactional `Write Temp` → `Flush(Disk)` → `Atomic Swap` pipeline. Prevents file corruption even if the PC loses power during a save.
*   **Tamper-Evident Security:** Every file is signed with **HMAC-SHA256**. Modified bytes are detected immediately, preventing save-file editing and splicing attacks.
*   **High-Performance Compression:** built on **MessagePack + LZ4**, offering parsing speeds 10x faster than JSON and 50% smaller file sizes.
*   **Hardware Binding:** Includes a local KeyStore that binds save files to the specific machine/installation (Anti-Cheat / Anti-Sharing).
*   **Zero-Copy Loading:** Optimized `Span<byte>` and `ReadOnlyMemory<byte>` paths to minimize GC pressure during gameplay.

---

## 📦 Dependencies

Helix relies on the MessagePack library.

```xml
<PackageReference Include="MessagePack" Version="2.*" />
<PackageReference Include="MessagePack.Annotations" Version="2.*" />
```

---

## 🚀 Quick Start

### 1. Define Your Data
Helix uses `MessagePackObject` attributes. Versioning is handled via integer Keys. You can add new fields later without breaking old saves.

```csharp
using MessagePack;

[MessagePackObject]
public class GameState
{
    // Key(0) allows for version handling if you need custom migration logic later
    [Key(0)] public int SchemaVersion = 1;

    // Complex objects are supported automatically
    [Key(1)] public PlayerData Player { get; set; } = new();
    [Key(2)] public WorldData World { get; set; } = new();
    
    // Added in Patch 1.2 - Old save files will load this as null/default without crashing
    [Key(3)] public string? NewDLCRegion { get; set; } 
}

[MessagePackObject]
public class PlayerData 
{ 
    [Key(0)] public int Health { get; set; }
    [Key(1)] public List<string> Inventory { get; set; } 
}
```

### 2. Save and Load
API calls are static, thread-safe, and handle all file I/O operations safely.

```csharp
// Saving
var myState = new GameState();
SaveSystem.Save("saves/slot1.sav", myState);

// Loading (Returns new GameState() if file missing or corrupted)
var loadedState = SaveSystem.LoadOrNew<GameState>("saves/slot1.sav");
```

---

## 💾 File Format Specification

Helix writes a custom binary envelope. The file on disk looks like this:

| Offset | Size | Type | Description |
| :--- | :--- | :--- | :--- |
| 0x00 | 4 | `ASCII` | **Magic Header** (`%SAV`) |
| 0x04 | 2 | `UInt16` | **Format Version** (Internal struct version) |
| 0x06 | 4 | `Int32` | **Payload Length** (N) |
| 0x0A | N | `Binary` | **Payload** (MessagePack + LZ4 Compressed) |
| 0x0A+N | 32 | `Bytes` | **Integrity Tag** (HMAC-SHA256 of Header(format ver and payload length) + Payload) |

*   **Magic Header:** Validates file type instantly.
*   **Payload Length:** Strict bounds checking prevents buffer overflow attacks during load.
*   **HMAC Tag:** Verifies that the header and payload have not been modified.

---

## 🛡️ Security & Anti-Cheat

Helix utilizes a `MacKeyStore` to generate a cryptographic key.

*   **Current Mode:** **Per-Install Isolation.**
    *   A random 32-byte key is generated in `%LocalAppData%` upon first run.
    *   **Effect:** Save files copied to another PC will fail the Integrity Check (won't load). This prevents casual save sharing.
*   **Cloud Save Mode (Optional):**
    *   To support Steam Cloud / Cross-Save, modify `MacKeyStore.cs` to use a hardcoded (obfuscated) static key instead of a generated one.

---

## ⚠️ Best Practices

1.  **Versioning:** Always append new fields with higher `[Key(x)]` IDs. Never remove or reorder existing keys.
2.  **Threading:** While `SaveSystem` writes to unique paths safely, do not call `Save` on the *same file path* from multiple threads simultaneously.
