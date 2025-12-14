# Helix-Save
**Helix** formatter is a high-performance, atomic, and tamper-evident binary persistence system designed for .NET  applications/games. 
can save c# class to binary file (signed , compressed, not editable externally by users, acid like reliablity)
like internal settings , and game states.. (for external settings editable by users use json instead)

## It serializes C# classes to binary files that are **Signed**, **Compressed**, **Type-Safe**, and **ACID-Compliant**.

It combines LZ4 compression with HMAC-SHA256 integrity checks to ensure save data is small, fast, 
and immune to corruption or external modification.

Designed for reliability under power-loss conditions (ACID-compliant file swapping)
and ease of long-term maintenance (forward-compatible versioning).

> **Why Helix?**
> JSON is editable and slow. Raw BinaryWriter is fragile. SQLite is overkill.
> Helix sits in the middle: It combines the speed of **MessagePack + LZ4** with a binary envelope that ensures your data is never corrupted, swapped, or tampered with.

[![NuGet](https://img.shields.io/nuget/v/Helix.Save.svg)](https://www.nuget.org/packages/Helix.Save)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Helix.Save.svg)](https://www.nuget.org/packages/Helix.Save)
---

## ⚡ Core Features

*   **Atomic Durability:** Uses a transactional `Write Temp` → `Flush(Disk)` → `Atomic Swap` pipeline. Prevents file corruption even if the PC loses power during a save.
*   **Tamper-Evident Security:** Every file is signed with **HMAC-SHA256**. Modified bytes are detected immediately, preventing hex-editing and save-splicing attacks.
*   **High-Performance Compression:** built on **MessagePack + LZ4**, offering parsing speeds 10x faster than JSON and 50% smaller file sizes.
*   **Hardware Binding (optional):** Includes a local KeyStore that binds save files to the specific machine/installation (Anti-Cheat / Anti-Sharing).
*   **Zero-Copy Loading:** Optimized `Span<byte>` and `ReadOnlyMemory<byte>` paths to minimize GC pressure during gameplay.
*   **Type Safe** The header includes a **SHA-256 Hash** of the C# Class Type. won't load wrong classes

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

var secrets = new AppCredentials { ApiKey = "12345" };
Helix.Save(secrets, "config/secrets.hlx", portable: false);//cant move to other user/machine

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
Use standard MessagePack attributes. Versioning is handled via integer Keys.

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


//saving multiple objects: let message pack handle it:

[MessagePackObject]
public class WorldSave
{
    [Key(0)] public LevelState Level1 { get; set; }
    [Key(1)] public LevelState Level2 { get; set; }
}


[MessagePackObject]
public class SaveBundle
{
    [Key(0)] public string BundleName;
    [Key(1)] public List<GameState> States; // The array is inside
}

is better than:

//var saves = new List<GameState> { state1, state2, state3 };
//Helix.Save(saves, "all_saves.hlx");
```

---

## 💾 File Format Specification

Helix writes a custom binary envelope. The file on disk looks like this:
strict **50-byte Binary Header** followed by the compressed payload.



**Layout:** `[Magic] [Ver] [TypeHash] [Timestamp] [Length] [Payload...] [HMAC]`
Magic(4) + Ver(2) + TypeHash(32) + Timestamp(8) + Len(4) + Payload(N) + Tag(32)

| Offset | Size | Type | Description |
| :--- | :--- | :--- | :--- |
| 0x00 | 4 | `ASCII` | **Magic Header** (`%HLX`) |
| 0x04 | 2 | `UInt16` | **Format Version** (Internal struct version) |
| 0x06 | 32 | `Bytes` | **Type Hash** (SHA-256 of the C# Class Name) |
| 0x26 | 8 | `Int64` | **Timestamp** (UTC Ticks, signed by HMAC) |
| 0x2E | 4 | `Int32` | **Payload Length** (N) |
| 0x32 | N | `Binary` | **Payload** (MessagePack + LZ4 Compressed) |
| 0x32+N | 32 | `Bytes` | **Integrity Tag** (HMAC-SHA256 of Header(format ver class type hash and timestamp ) + Payload) |

*   **Magic Header:** Validates file type instantly.
*   **Payload Length:** Strict bounds checking prevents buffer overflow attacks during load.
*   **HMAC Tag:** Verifies that the header and payload have not been modified.
*   **Type Hash:** Ensures `Helix.Load<Player>(file)` fails immediately if the file actually contains `Settings` data.
*   **Timestamp:** The save time is embedded in the signed header. Prevents "Rollback Attacks" (users trying to fake save times).

---

## 🛡️ Security & Anti-Cheat

Helix utilizes a MAC `SentinelKeyStore` to generate a cryptographic key.

*   **Machine key Mode:** **Per-Install Isolation.**
    *   A random 32-byte key is generated in `%LocalAppData%` upon first run.
    *   **Effect:** Save files copied to another PC will fail the Integrity Check (won't load). This prevents casual save sharing.

Helix uses `SentinelKeyStore` to manage keys based on the `portable` flag.

### Mode A: Portable (Default)
*   **Use Case:** Cloud, Shareable Files.

### Mode B: Machine Local key (secure apps/non cloud games)
*   **Use Case:** Login Tokens, cached credentials, non sharable game files 

---

## ⚠️ Best Practices

1.  **Versioning:** Always append new fields with higher `[Key(x)]` IDs. Never remove or reorder existing keys.
2.  **Threading:** While `Helix` writes to unique paths safely, do not call `Save` on the *same file path* from multiple threads simultaneously.
3.  **OS** use with windows for ACID,on POSIX filesystems its crash-safe against partial writes,as durable rename typically also requires syncing the parent directory after the rename/replace (add an optional FsyncParentDirectory(path) after File.Replace/File.Move (P/Invoke fsync on the directory FD; macOS may want F_FULLFSYNC)


4.

The "Snapshot" vs. "Random Access" 
Helix (Snapshot Engine):
To change one integer (e.g., Player.Gold), Helix must serialize everything, compress everything, hash everything, and rewrite the entire file.
To read one value, it must load the entire file into RAM.
Limit: Efficient up to ~50MB.

ESE JetBlue / SQLite (Paged Database):
The file is split into 4KB "Pages".
To change Player.Gold, it only rewrites the specific 4KB page where that number lives. It does not touch the rest of the 1GB file.
Can handle Terabytes of data efficiently.


To make Helix work like ESE, it need to implement B-Trees and Paging. 
This is more complex and would require large refactor

However, Helix IS better than SQLite for:
Game Saves: Because games usually load the whole state anyway.
Configuration Files: Because you always read the whole config.
Session Blobs: Storing user session data in a web server