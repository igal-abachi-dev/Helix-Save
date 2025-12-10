# Helix-Save
**Helix** formatter is a high-performance, atomic, and tamper-evident binary persistence system designed for .NET  applications/games. 
can save c# class to binary file (signed , compressed, not editable externally by users, acid like reliablity)
like internal settings , and game states.. (for external settings editable by users use json instead)


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


No corruption on crash/power loss:
Atomic File.Replace + Flush(true)

No partial writes:
Temp file + atomic swap

Works for both config and gamestate:
Generic Save<T> / LoadOrNew<T>

Future-proof (add fields forever):
MessagePack with keys

Fast and small files:
MessagePack + LZ4

Anti-cheat / anti-tamper:
HMAC-SHA256 bound to machine

Survives corrupted saves:
.bak fallback

Works on Windows/Linux/macOS

---
should work better than those:

*Raw BinaryWriter + atomic
Too fragile, manual versioning, big files

*WAL +checkpoint journaling pattern
Overkill, complex, slower

*sqlite like binary
no need for searching or partial reading


* unity has paid Easy Save / Odin Serializer products , godot FileAccess.StoreVar Save System	,
helix uses HMAC (Signing) + Hardware Binding,  designed to prevent tampering even if the file is readable.
but soon will implement AHEAD AES encryption / hybrid encryption(rsa+aes) , to encrypt the inner payload too

*maybe later will add: public static void Append<T>(string path, T eventData) {
feature , for data that appends like binary logs
as currently:
Helix is a Snapshot Engine (It overwrites the whole file with the new state).
so proccings need a Journaling Engine (It appends small events to the end of a list).
instead of whole file

---

## 📦 Dependencies

Helix relies on the MessagePack library.

```xml
<PackageReference Include="MessagePack" Version="2.*" />
<PackageReference Include="MessagePack.Annotations" Version="2.*" />
```

---

## 🚀 Quick Start
---


### 1. Save and Load
API calls are static, thread-safe, and handle all file I/O operations safely.

```csharp

using HelixFormatter;

// Saving
var settings = new GameSettings();
Helix.Save(settings, "saves/settings.hlx");

// Loading (Returns new GameSettings() if file missing or corrupted)
 settings = Helix.LoadOrNew<GameSettings>("saves/settings.hlx");

var myState = new GameState();
string timelineId = TemporalCore.CurrentBranch.Id; //guid
Helix.Save(gameState, $"saves/timeline_{timelineId}.hlx");

// Jump to another branch
var branch = TemporalCore.GetBranch("helix-prime-616");
var state = Helix.LoadOrNew<GameState>($"saves/timeline_{branch.Id}.hlx");

//can also be used for auto-save: like every 10 sec on a in memory object
```


### 2. Define Your Data
Helix uses `MessagePackObject` attributes. Versioning is handled via integer Keys. You can add new fields later without breaking old saves.

```csharp
using MessagePack;

using HelixFormatter;

    [MessagePackObject]
    public class GameState
    {
        // Key(0) allows for version handling if you need custom migration logic later
        [Key(0)] public int Version = 1;

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
        [Key(1)] public List<string> Inventory { get; set; } = new List<string>();
    }

    [MessagePackObject]
    public class WorldData
    {
        [Key(0)] public int year { get; set; }
    }
```


### 3. Polymorphism , Circular Ref

```csharp
[MessagePackObject]
// You MUST tell MessagePack about the subclasses here
[Union(0, typeof(Sword))]
[Union(1, typeof(Cure))]
public abstract class Item 
{
    [Key(0)] public int Id { get; set; }
}

[MessagePackObject]
public class Sword : Item
{
    [Key(1)] public int Damage { get; set; }
}

[MessagePackObject]
public class Cure : Item
{
    [Key(1)] public int HealAmount { get; set; }
}



// BAD (Circular loop, crashes messagepack)
public class Player {
    public Player Target; // Points to another monster
}

// GOOD (Helix Friendly) Avoid Circular References
public class Player {
    public int TargetID; // Save the ID, look it up after loading
}
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

Helix utilizes a MAC `SentinelKeyStore` to generate a cryptographic key.

*   **Current Mode:** **Per-Install Isolation.**
    *   A random 32-byte key is generated in `%LocalAppData%` upon first run.
    *   **Effect:** Save files copied to another PC will fail the Integrity Check (won't load). This prevents casual save sharing.
*   **Cloud Save Mode (Optional):**
    *   To support Steam Cloud / Cross-Save, modify `MacKeyStore.cs` to use a hardcoded (obfuscated) static key instead of a generated one.

---

## ⚠️ Best Practices

1.  **Versioning:** Always append new fields with higher `[Key(x)]` IDs. Never remove or reorder existing keys.
2.  **Threading:** While `Helix` writes to unique paths safely, do not call `Save` on the *same file path* from multiple threads simultaneously.
