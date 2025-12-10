using MessagePack;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Helix;

//MessagePack payload + (magic header + checksum) + atomic temp→replace + .bak fallback.
public static class Helix
{
    // MessagePack is fast/compact and has built-in LZ4 support (great for games). 
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            // UntrustedData mode is the recommended defensive setting when deserializing untrusted data. 
            .WithSecurity(MessagePackSecurity.UntrustedData  // protects against malicious saves HashCollisionResistant
                .WithMaximumObjectGraphDepth(2048));

    // File envelope
    private static ReadOnlySpan<byte> Magic => "%SAV"u8; // 4 bytes, readable in a hex editor
    private const ushort FormatVer = 1;
    private const int HeaderSize = 10; // Magic(4) + Ver(2) + Len(4)
    private const int TagSize = 32;    // HMAC-SHA256

    /*
    MessagePack + LZ4: speed and compression. easy maintenance
    Atomic Swap: Prevents corruption on power loss.
    HMAC-SHA256: Prevents file tampering (Anti-Cheat / Anti-Corruption).
    Versioning: Future-proofs your data. if data class changes
    */

    public static void Save<T>(string path, T data)
    {
        // 1) Serialize (+ LZ4 compression due to options)
        byte[] payload = MessagePackSerializer.Serialize(data, Options);
        SaveBytes(path, payload);
    }

    public static void SaveBytes(string path, byte[] payload) //so serialization and save can be split process in different threads(Helix.Options is public)
    {
        //must Serialize on the main thread before putting it in the channel<T> of bg thread, when splitting save between threads (like timer auto-save)

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var tmp = path + ".tmp";
        var bak = path + ".bak";

        // 2) Integrity(detects corruption and some save cheating)
        // MAC over header fields + payload (prevents splicing)
        byte[] tag = ComputeTag(FormatVer, payload);

        //aes ahead encrypt: payload?

        // 3) Write temp
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic);            // 4
            bw.Write(FormatVer);        // 2 format version
            bw.Write(payload.Length);   // 4
            bw.Write(payload);          // N
            bw.Write(tag);              // 32 integrity

            // CRITICAL: Force OS to write physical bits to disk
            bw.Flush();
            fs.Flush(true); // clears intermediate OS buffers in case of power failure
        }

        // 4) Atomic swap (same-volume requirement; keep tmp next to target) 
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, bak, ignoreMetadataErrors: true);// atomic replace + backup  , ensures intermediate OS buffers are flushed too
            }
            else
            {
                File.Move(tmp, path); // first save (Replace would throw if destination missing) 
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /*
    public static void Append<T>(string path, T eventData)
    {

    }
    */

    public static T LoadOrNew<T>(string path) where T : new()
    {
        if (TryLoad(path, out T data)) return data;
        if (TryLoad(path + ".bak", out data)) return data;

        return new T();
    }

    private static bool TryLoad<T>(string file, out T data)
    {
        data = default!;
        if (!File.Exists(file)) return false;
        try
        {
            byte[] all = File.ReadAllBytes(file);
            // Minimum header = 4 + 2 + 4 + N + 32 = 42 bytes , too small
            if (all.Length < HeaderSize + TagSize) return false;

            var span = all.AsSpan();
            if (!span[0..4].SequenceEqual(Magic)) return false;

            ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            if (ver != FormatVer) return false;


            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span[6..]);
            if (payloadLen <= 0 || payloadLen > 100 * 1024 * 1024 || all.Length != HeaderSize + payloadLen + TagSize) return false;

            var payload = span.Slice(HeaderSize, payloadLen);
            var tag = span.Slice(HeaderSize + payloadLen, TagSize);
            if (tag.Length != TagSize) return false;

            var expected = ComputeTag(ver, payload);//check tampered or corrupted

            // Constant-time compare to avoid timing leaks
            if (!CryptographicOperations.FixedTimeEquals(tag, expected))
                return false;

            // Create zero-copy ReadOnlyMemory from the original array 'all' , start slice after header index
            var payloadMemory = new ReadOnlyMemory<byte>(all, HeaderSize, payloadLen);
            data = MessagePackSerializer.Deserialize<T>(payloadMemory, Options);
            return true;
        }
        catch
        {
            // File corrupted or format mismatch
            return false;
        }
    }
    private static byte[] ComputeTag(ushort formatVer, ReadOnlySpan<byte> payload)
    {
        using (var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, SentinelKeyStore.Key))
        {
            // We must hash the header data exactly as it appears in the file
            Span<byte> header = stackalloc byte[6];
            // 2 + 4 bytes header (little-endian, explicit)
            BinaryPrimitives.WriteUInt16LittleEndian(header, formatVer);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(2), payload.Length);

            h.AppendData(header);
            h.AppendData(payload);
            return h.GetHashAndReset();// 32 bytes
        }
    }
}