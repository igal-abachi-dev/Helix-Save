# Helix-Save // Secure, Atomic, Fast Binary Serialization
**Helix** formatter is a high-performance, atomic, and tamper-evident binary persistence system designed for .NET  applications/games. 
can save c# class to binary file (signed , compressed, not editable externally by users, acid like reliablity)
like internal settings , and game states.. (for external settings editable by users use json instead if no in-app GUI editor for them)

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

## 📦 Installation

```bash
dotnet add package Helix.Save
```
---

> **why not just messagepack alone?**
 Helix has a strong envelop format , messagepack is missing for Persistence & reliability,
 when used for files instead of network streams of many GB size

use case:
1. You are saving "Documents" to disk
Examples: Game Saves, User Settings, Application Config, Cached Data.
Why: You need ACID safety. If you use raw File.WriteAllBytes and the power cuts out, the file becomes 0 bytes and the user loses everything. Helix guarantees this never happens.

2. You need Security/Integrity
Examples: Preventing users from hacking their gold/level, or ensuring a config file hasn't been corrupted by a bad disk sector.
Why: Raw MessagePack has no checksum. Helix has HMAC-SHA256.

3. You need Versioning Safety
Examples: Ensuring you don't load a Settings file into a Player object.
Why: Raw MessagePack will happily try to deserialize garbage data into your class, 
resulting in weird nulls or crashes. Helix's TypeHash stops this immediately.
---

## ⚡ Core Features

*   **Atomic Durability:** Uses a transactional `Write Temp` → `Flush(Disk)` → `Atomic Swap` pipeline. Prevents file corruption even if the PC loses power during a save.
*   **Tamper-Evident Security:** Every file is signed with **HMAC-SHA256**. Modified bytes are detected immediately, preventing hex-editing and save-splicing attacks.
*   **High-Performance Compression:** built on **MessagePack + LZ4**, offering parsing speeds 10x faster than JSON and 50% smaller file sizes.
*   **Hardware Binding (optional):** Includes a local KeyStore that binds save files to the specific machine/installation (Anti-Cheat / Anti-Sharing).
*   **Zero-Copy Loading:** Optimized `Span<byte>` and `ReadOnlyMemory<byte>` paths to minimize GC pressure during gameplay.
*   **Type Safe** The header includes a **SHA-256 Hash** of the C# Class Type. won't load wrong classes
*   **Repairable:** Includes tool to export binary data to JSON for debugging or fixing user issues. and import back to binary , for user settings
*   **Flexibility:** can extract msgpack uncompressed payload and strip envelop , to use interop with other langs/net streams , can save uncompressed , can save without bak file for big downloads caches use case

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

*json
1) Local Cache / Blobs (cache.hlx):
Why: If your app downloads 10,000 products from an API, save them locally in Helix. Parsing 50MB of JSON is slow; parsing 25MB of Helix is instant.
2) Complex Object Graphs:
Why: JSON struggles with circular references and polymorphism (inheritance). Helix (MessagePack) handles them natively.

by default all private data that is not user editable should be binary , the rest can be json


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


//--   internal protection from dll import:
make classes of msgpack objects, internal instead of public
and on assembly info file:
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MessagePack")]
[assembly: InternalsVisibleTo("MessagePack.Resolvers.DynamicObjectResolver")]
```

### 4. The Repair Tool (JSON Interop)
Binary files are hard to debug. Helix includes a helper to let you (or your users) fix broken files by converting them to JSON and import back. [only for the files/classes you choose that allow it]

Add this to your `Main()` / Startup:

```csharp
static void Main(string[] args)
{
    // Check for CLI args to convert files
    // Usage: MyGame.exe --helix-export saves/slot1.hlx
    if (HelixRepairTool.HandleConsoleArgs<GameState>(args, "saves/slot1.hlx", portable: true))
    {
        return; // Exit after tool runs
    }

    // Normal Game Start...
}
```

---

## 💾 File Format Specification

Helix writes a custom binary envelope. The file on disk looks like this:
strict **51-byte Binary Header** followed by the compressed payload.

**Layout:** `[Magic] [Ver] [Flags] [TypeHash] [Timestamp] [Length] [Payload...] [HMAC]`
Magic(4) + Ver(2) + Flags(1) + TypeHash(32) + Timestamp(8) + Len(4) + Payload(N) + Tag(32)

| Offset | Size | Type | Description |
| :--- | :--- | :--- | :--- |
| 0 | 4 | `ASCII` | **Magic Header** (`%HLX`) |
| 4 | 2 | `UInt16` | **Format Version** (Internal struct version) |
| 6 | 1 | `Byte` | **Flags** (Compression: 0=None, 1=LZ4) |
| 7 | 32 | `Byte[]` | **Type Hash** (SHA-256 of the C# Class Name) |
| 39 | 8 | `Int64` | **Timestamp** (UTC Ticks, signed by HMAC) |
| 47 | 4 | `Int32` | **Payload Length** (N) |
| 51 | N | `Byte[]` | **Payload** (MessagePack + LZ4 Compressed[optional]) |
| 51+N | 32 | `Byte[]` | **Integrity Tag** (HMAC-SHA256 of Header(format ver class type hash and timestamp ) + Payload) |

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

If your game state updates at 60 FPS, you should not write to disk at 60 FPS. You should write to Memory at 60 FPS, and flush to Disk every few seconds (or when the game pauses/exits).

3.  **OS** use with windows for ACID,on POSIX filesystems its crash-safe against partial writes,as durable rename typically also requires syncing the parent directory after the rename/replace (add an optional FsyncParentDirectory(path) after File.Replace/File.Move (P/Invoke fsync on the directory FD; macOS may want F_FULLFSYNC)


4.

The "Snapshot" vs. "Random Access" 
Helix (Snapshot Engine):
To change one integer (e.g., Player.Gold), Helix must serialize everything, compress everything, hash everything, and rewrite the entire file.
To read one value, it must load the entire file into RAM.
Limit: Efficient up to ~50MB. Helix is perfect for files up to ~100MB max.

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



If you try to split it into pages (e.g., 4KB chunks):
MessagePack Incompatibility: MessagePack is a stream. You cannot simply "read page 5" of a MessagePack file to get Player.Inventory. You must read pages 1, 2, 3, and 4 to know the context and offsets of page 5.
Compression Killer: LZ4 works best on large blocks (64KB+). If you compress 4KB pages individually, your file size will increase significantly.
ACID Nightmare: Currently, you use File.Replace (Atomic Swap). If you split the file into pages, you can't use atomic swap. You would need to implement a Write-Ahead Log (WAL) to ensure that if the power fails while writing Page 5, the database isn't corrupted. This is incredibly hard to write correctly.
HMAC Complexity: How do you sign the file? If you sign every page, it's slow. If you sign the whole file, you have to read the whole file to check it, defeating the purpose of paging.

The "Correct" Architecture for Large Data: Sharding
If you have a game or app with too much data for one Helix file (e.g., an Open World game), do not split the file internally. Instead, split your data into multiple Helix files.
This is how Minecraft and Skyrim work. They don't put the whole world in one file.


Use Helix for: Game States, App Configs, User Session Blobs, Key/Value storage.
```csharp
// /Saves/Slot1/
//     ├── player.hlx       (Small, encrypted, atomic) -> Player stats, Inventory
//     ├── world_global.hlx (Medium) -> Quest states, Global variables
//     ├── region_0_0.hlx   (Large) -> Terrain/Objects for Map Sector 0,0
//     ├── region_0_1.hlx   (Large) -> Terrain/Objects for Map Sector 0,1
//     └── metadata.hlx     (Tiny) -> Timestamp, Screenshot, Level Name
```
	
	

---

# 📚 Helix.Save API Reference

## Namespace: `HelixFormatter`
>	Note: `SentinelKeyStore` is omitted because it is defined as `internal`, making it inaccessible outside the library.

### 1. `public static class Helix`
The core class for handling binary serialization, file I/O, and ACID operations.

#### **Configuration Fields**
*   **`public static readonly MessagePackSerializerOptions Options`**
    *   The default options used for serialization.
    *   **Settings:** LZ4 Compression (BlockArray), allows private/internal classes, security enabled (UntrustedData).
*   **`public static readonly MessagePackSerializerOptions OptionsNoCompression`**
    *   Options used when `compress` is set to `false`.
    *   **Settings:** No Compression, allows private/internal classes, security enabled.

#### **Core Persistence Methods**
*   **`public static void Save<T>(T data, string path, bool portable = true, bool backup = true, bool compress = true)`**
    *   Serializes an object of type `T` and saves it to the specified path with an atomic swap.
    *   **Constraints:** `where T : new()`
    *   **Parameters:**
        *   `path`: File destination.
        *   `portable`: If `true` (default), uses the Global Key (Cloud/Game saves). If `false`, uses Machine Key (DPAPI/Local saves).
        *   `backup`: If `true` (default), keeps the previous version as `.bak`.
        *   `compress`: If `true` (default), uses LZ4. If `false`, uses raw MessagePack (useful for interop).

*   **`public static void SaveRawMsgPackBytes<T>(string path, byte[] payload, bool portable = true, bool backup = true, bool isCompressed = true)`**
    *   Wraps pre-serialized bytes into the Helix binary envelope (Header + HMAC) and saves to disk.
    *   **Constraints:** `where T : new()` (Used for TypeHash calculation).

*   **`public static T LoadOrNew<T>(string path, bool portable = true)`**
    *   Attempts to load and deserialize the file.
    *   **Returns:** The loaded data, or `new T()` if the file is missing, corrupted, or has a hash mismatch.
    *   **Constraints:** `where T : new()`

*   **`public static T LoadOrFail<T>(string path, bool portable = true)`**
    *   Attempts to load and deserialize the file.
    *   **Throws:** `FileNotFoundException` or `InvalidOperationException` if loading fails (does not return a default object).
    *   **Constraints:** `where T : new()`

#### **Static File Helpers**
*Convenience wrappers that save simple types inside the Helix envelope.*

*   **`public static string ReadAllText(string path, bool portable = true)`**
*   **`public static void WriteAllText(string path, string? contents, bool portable = true)`**
*   **`public static void WriteAllText(string path, ReadOnlySpan<char> contents, bool portable = true)`**
*   **`public static byte[] ReadAllBytes(string path, bool portable = true)`**
*   **`public static void WriteAllBytes(string path, byte[] bytes, bool portable = true)`**
*   **`public static string[] ReadAllLines(string path, bool portable = true)`**
*   **`public static void WriteAllLines(string path, string[] contents, bool portable = true)`**
*   **`public static void WriteAllLines(string path, IEnumerable<string> contents, bool portable = true)`**

---

### 2. `public static class HelixRepairTool`
A utility class meant to be called during application startup to handle command-line maintenance tasks (Exporting/Importing JSON).

#### **Methods**
*   **`public static bool HandleConsoleArgs<T>(string[] args, string hlxPath, bool portable = true)`**
    *   Checks the provided command-line arguments for specific Helix commands.
    *   **Commands Handled:**
        *   `--helix-export`: Converts the binary file to a readable `.json` file.
        *   `--helix-import`: Converts a `.json` file back to the binary format.
    *   **Returns:** `true` if a command was executed (indicating the app should likely exit), `false` if normal execution should continue.
    *   **Constraints:** `where T : new()`