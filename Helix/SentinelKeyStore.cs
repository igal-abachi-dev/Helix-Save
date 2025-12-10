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
        // Try Load
        if (File.Exists(keyPath))
        {
            try
            {
                return File.ReadAllBytes(keyPath);
            }
            catch
            {
                // If read fails (rare), proceed to generate new
            }
        }

        // Generate new per-install 32-byte key
        var key = RandomNumberGenerator.GetBytes(32);

        // Atomic-ish create (temp + move)
        var tmp = keyPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(key, 0, key.Length);
            fs.Flush(true); // make the key file durable too 
        }

        //Safe Move (Handles race conditions if two instances start at once)
        try
        {
            if (File.Exists(keyPath))
                File.Delete(keyPath);

            File.Move(tmp, keyPath);
        }
        catch
        {
            // If Move failed, it means another process probably wrote the key 
            // at the exact same time. Try reading it again.
            if (File.Exists(keyPath)) return File.ReadAllBytes(keyPath);
        }
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