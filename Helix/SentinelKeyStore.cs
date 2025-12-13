using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace HelixFormatter;

static class SentinelKeyStore
{
    public static readonly byte[] MachineKey;//machine key

    public static readonly byte[] GlobalKey;

    static SentinelKeyStore()
    {
        GlobalKey = DecodeGlobalKey();
        MachineKey = LoadOrCreateMachineKey(GetAppId());
    }

    private static string GetAppId() => Assembly.GetExecutingAssembly().GetName().Name!;

    private static byte[] LoadOrCreateMachineKey(string appName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);

        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "machine.key");
        // Try Load
        if (File.Exists(keyPath))
        {
            try
            {
                byte[] raw = File.ReadAllBytes(keyPath);
                // DPAPI CHECK: Is this Windows?
                //if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                //{
                //    // Decrypts using current user credentials. 
                //    // Fails if file was copied from another PC.
                //    return ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                //}
                return raw; // Linux/Mac/Android fallback (Raw bytes)
            }
            catch
            {
                // If read fails (rare), proceed to generate new
            }
        }

        // Generate new per-install 32-byte key
        var key = RandomNumberGenerator.GetBytes(32);
        // 3. Encrypt before saving (Windows Only)
        //if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        //{
        //    key = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        //}

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
            if (File.Exists(keyPath)) return File.ReadAllBytes(keyPath); //no call to LoadOrCreateMachineKey to not loop
        }
        return key;
    }


    private static byte[] DecodeGlobalKey()
    {
        // 1. The Raw Key Parts (Obfuscated in compiled code)
        // Changing these numbers changes the key.
        ulong p1 = 0xDF_19_63_CB_87_8A_AB_AF;
        ulong p2 = 0xBF_4B_D9_74_94_AA_03_4A;
        ulong p3 = 0xFF_79_26_7B_F1_E6_11_15;
        ulong p4 = 0x72_B7_65_58_47_BA_8A_5E;

        // If a debugger is attached, we corrupt 'p1'.
        // The key generation continues normally, but the result will be wrong.
        // The Save/Load will fail the HMAC check, looking like "File Corruption"
        if (System.Diagnostics.Debugger.IsAttached)
        {
            // ---ANTI - TAMPER / ANTI - DEBUG-- -
            // XOR with a "Bad Code" mask to silently ruin the key
            p1 ^= 0xBAD0_C0DE_DEAD_BEEF;
        }

        // The key is calculated at runtime.
        byte[] buffer = new byte[32];
        Span<byte> span = buffer; // Use Span for speed

        // Write the longs into the buffer (Filling the 32 bytes)

        BinaryPrimitives.WriteUInt64LittleEndian(span, p1);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8), p2);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), p3);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), p4);

        // Part B: The "Key" to unlock the seed (The "Salt")
        // Use a compile-time constant string logic, but treat it as bytes

        // Use a string that is exactly 32 chars or longer 
        ReadOnlySpan<byte> salt = "H3l1x-DoN0tCh@ngeThisString#%@!!"u8;
        // XOR Mix (vernam Obfuscation)
        // This ensures the final key is not just the numbers above.
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(buffer[i] ^ salt[i % salt.Length]);
        }

        return buffer;
    }
    /*

     maybe use DPAPI on the key to secure it better in windows, problem its windows only?

     HMAC is great for tamper-evidence if the key is secret. But :

The per-install key is stored on disk next to the app data → an attacker can copy/edit it along with the save.

The global key is embedded in the binary (even with obfuscation) → a determined attacker can extract it and forge HMACs.
     */
}