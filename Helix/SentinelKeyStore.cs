using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Helix;

static class SentinelKeyStore
{
    public static readonly byte[] Key;
    static SentinelKeyStore()
    {
        Key = LoadOrCreate(GetAppId());
    }

    private static string GetAppId() => Assembly.GetExecutingAssembly().GetName().Name!;

    private static byte[] LoadOrCreate(string appName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);

        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, $"save.key");
        if (File.Exists(keyPath))
            return File.ReadAllBytes(keyPath);

        // Generate per-install 32-byte key
        var key = RandomNumberGenerator.GetBytes(32);

        // Atomic-ish create (temp + move)
        var tmp = keyPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(key);
            fs.Flush(true); // make the key file durable too 
        }
        if (File.Exists(keyPath)) File.Delete(keyPath); // Rare race condition handling
        File.Move(tmp, keyPath);
        return key;
    }

    /*
         generating a random key per installation and storing it in %LocalAppData%.
    The Problem: If a user copies their save1.bin to a new computer (or reinstalls Windows), the save file will not load. The new PC will generate a different key, the HMAC check will fail, and the save will be rejected as "corrupted."
    The Fix:
    For Anti-Cheat: Keep your current approach (binds save to PC).
    For Portability (Steam Cloud / Transferring Saves): You must use a fixed key hardcoded in your game (obfuscated),or Steam syncs the encrypted save + the key file
     or derive the key from the user's ID/Password. Do not generate it randomly on disk
        */

}