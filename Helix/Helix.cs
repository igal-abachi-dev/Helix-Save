using MessagePack;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HelixFormatter;

//MessagePack payload + (magic header + checksum) + atomic temp→replace + .bak fallback.
public static class Helix
{
    // MessagePack is fast/compact and has built-in LZ4 support (great for games / local durable blobs). 
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            // UntrustedData mode is the recommended defensive setting when deserializing untrusted data. 
            .WithSecurity(MessagePackSecurity.UntrustedData  // protects against malicious saves HashCollisionResistant
                .WithMaximumObjectGraphDepth(2048));

    //    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);//PathLock class with SemaphoreSlim + refcount and FileLockGuard  struct with key , windows is OrdinalIgnoreCase 

    // File envelope
    private static ReadOnlySpan<byte> Magic => "%HLX"u8; // 4 bytes, readable in a hex editor
    private const ushort FormatVer = 1;

    // Manual BinaryWriter for header envelop, msgpack for payload , like mkv container with inner video streams    
    // Header Breakdown:
    // Magic (4) + Ver (2) + TypeHash (32) + Timestamp (8) + Len (4) = 50 bytes
    private const int HeaderSize = 4 + 2 + TypeHashSize + 8 + 4;
    private const int TagSize = 32;    // HMAC-SHA256
    private const int TypeHashSize = 32; // SHA-256

    /*
    MessagePack + LZ4: speed and compression. easy maintenance
    Atomic Swap: Prevents corruption on power loss.
    HMAC-SHA256: Prevents file tampering (Anti-Cheat / Anti-Corruption).
    Versioning: Future-proofs your data. if data class changes
    */

    public static void Save<T>(T data, string path, bool portable = true)
    {
        // 1) Serialize (+ LZ4 compression due to options)
        byte[] payload = MessagePackSerializer.Serialize(data, Options);
        SaveBytes<T>(path, payload, portable);
    }

    public static void SaveBytes<T>(string path, byte[] payload,
                                    bool portable = true) //so serialization and save can be split process in different threads(Helix.Options is public)
    {
        //var fileLock = GetLockForPath(path);
        //fileLock.Wait(); // block here if another thread is already saving this path
        //try
        //{
        //must Serialize on the main thread before putting it in the channel<T> of bg thread, when splitting save between threads (like timer auto-save)

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var tmp = path + ".tmp";
        var bak = path + ".bak";

        // 1. Calculate Metadata

        Span<byte> typeHash = stackalloc byte[TypeHashSize];
        ComputeTypeHash(typeof(T), typeHash);

        long timestamp = DateTime.UtcNow.Ticks; // Absolute time, 100ns precision
                                                // 2) Integrity(detects corruption and some save cheating)
                                                // MAC over header fields + payload (prevents splicing)
        byte[] tag = ComputeTag(FormatVer, typeHash, timestamp, payload, portable);

        //aes ahead encrypt: payload?
        //"Preview metadata + Blob" Pattern?

        // 3) Write temp
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic); // 4
            bw.Write(FormatVer); // 2 format version
            bw.Write(typeHash); // 32
            bw.Write(timestamp); // 8
            bw.Write(payload.Length); // 4
            bw.Write(payload); // N
            bw.Write(tag); // 32 integrity

            // CRITICAL: Force OS to write physical bits to disk
            bw.Flush();
            fs.Flush(true); // clears intermediate OS buffers in case of power failure
        }

        // 4) Atomic swap (same-volume requirement; keep tmp next to target) 
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, bak,
                    ignoreMetadataErrors: true); // atomic replace + backup  , ensures intermediate OS buffers are flushed too
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
        //}
        //finally
        //{
        //    fileLock.Release();
        //}
    }

    //private static SemaphoreSlim GetLockForPath(string path)
    //{
    //    return _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
    //}


    /*
     impl later...
    public static void Append<T>(string path, T eventData)
    {

    }
    */

    public static T LoadOrNew<T>(string path, bool portable = true) where T : new()
    {
        if (TryLoad(path, out T data, out _, portable)) return data;
        if (TryLoad(path + ".bak", out data, out _, portable)) return data;

        return new T();
    }


    //Peek Header: impl later...
    //	public static bool PeekMetadata(string file, out long timestamp)
    //  {
    //	}

    private static bool TryLoad<T>(string file, out T data, out long timestamp, bool portable)
    {
        data = default!;
        timestamp = 0;
        if (!File.Exists(file)) return false;
        try
        {
            byte[] all = File.ReadAllBytes(file);
            // Minimum header = 22 + N + 32 = 54 bytes , too small
            if (all.Length < HeaderSize + TagSize) return false;

            var span = all.AsSpan();
            if (!span[0..4].SequenceEqual(Magic)) return false;

            ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            if (ver != FormatVer) return false;



            ReadOnlySpan<byte> fileTypeHash = span.Slice(6, TypeHashSize);

            Span<byte> expectedHash = stackalloc byte[TypeHashSize];
            ComputeTypeHash(typeof(T), expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(fileTypeHash, expectedHash))
                return false; // Type mismatch



            // Offset logic: Magic(4) + Ver(2) + Hash(32) = 38
            timestamp = BinaryPrimitives.ReadInt64LittleEndian(span[38..]);

            // Offset logic: 38 + Time(8) = 46
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span[46..]);
            if (payloadLen <= 0 || payloadLen > 100 * 1024 * 1024 || all.Length != HeaderSize + payloadLen + TagSize)
                return false;

            var payload = span.Slice(HeaderSize, payloadLen);
            var tag = span.Slice(HeaderSize + payloadLen, TagSize);
            if (tag.Length != TagSize) return false;

            // Verify Integrity (Includes TypeHash and Timestamp in calculation)
            var expected = ComputeTag(ver, fileTypeHash, timestamp, payload, portable);//check tampered or corrupted

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


    private static void ComputeTypeHash(Type t, Span<byte> destination)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(t.FullName ?? t.Name);
        SHA256.HashData(nameBytes, destination);
    }

    private static byte[] ComputeTag(ushort formatVer, ReadOnlySpan<byte> typeHash, long timestamp, ReadOnlySpan<byte> payload, bool portable)
    {
        //prevents splicing and editing.
        using (var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, portable ? SentinelKeyStore.GlobalKey : SentinelKeyStore.MachineKey))
        {
            // We hash the header fields so they cannot be swapped (e.g. changing timestamp to prevent rollback detection)

            // Create a buffer for the header parts we want to sign
            // Ver(2) + TypeHash(32) + Timestamp(8) = 42 bytes
            Span<byte> header = stackalloc byte[42]; // 2 + 32 + 8

            BinaryPrimitives.WriteUInt16LittleEndian(header, formatVer);
            // Copy the 32-byte hash into the header buffer at offset 2
            typeHash.CopyTo(header.Slice(2));
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(34), timestamp);

            h.AppendData(header);
            h.AppendData(payload);
            return h.GetHashAndReset();// 32 bytes
        }
    }
}