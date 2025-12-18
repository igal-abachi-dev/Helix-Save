using MessagePack;
using MessagePack.Resolvers;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HelixFormatter;

//MessagePack payload + (magic header + checksum) + atomic temp→replace + .bak fallback.
public static class Helix
{
    // OPTION A: High Performance (Default options)
    // MessagePack is fast/compact and has built-in LZ4 support (great for games / local durable blobs). 
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolverAllowPrivate.Instance) //to allow save internal Classes + InternalsVisibleTo (Compile-Time Defense)
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            // UntrustedData mode is the recommended defensive setting when deserializing untrusted data. 
            .WithSecurity(MessagePackSecurity.UntrustedData  // protects against malicious saves HashCollisionResistant
                .WithMaximumObjectGraphDepth(2048));


    // OPTION B: Interoperability (Python/Go... friendly)
    public static readonly MessagePackSerializerOptions OptionsNoCompression =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolverAllowPrivate.Instance)
            .WithCompression(MessagePackCompression.None) // <--- RAW MessagePack
            .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(2048));

    // Thread Safety: Required for Kestrel/ASP.NET to prevent IO crashes on concurrent saves , kestrel use case will be less common, no need for now,
    // _fileLocks disabled for now as its too basic impl (accumulates memory and not evict locks , usage in kestrel should sync the lib from outside the lib)
    //    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);//better impl: PathLock class with SemaphoreSlim + refcount +long LastUsedMs and FileLockGuard  struct with key , windows is OrdinalIgnoreCase 

    // File envelope:
    private static ReadOnlySpan<byte> Magic => "%HLX"u8; // 4 bytes, readable in a hex editor
    private const ushort FormatVer = 1;

    // Manual BinaryWriter for header envelop, msgpack for payload , like mkv container with inner video streams    
    // Header Breakdown:
    // Magic (4) + Ver (2) + Flags(1) + TypeHash (32) + Timestamp (8) + Len (4) = 51 bytes
    private const int HeaderSize = 4 + 2 + 1 + TypeHashSize + 8 + 4;
    private const int TagSize = 32;    // HMAC-SHA256
    private const int TypeHashSize = 32; // SHA-256

    /*
    MessagePack + LZ4: speed and compression. easy maintenance
    Atomic Swap: Prevents corruption on power loss.
    HMAC-SHA256: Prevents file tampering (Anti-Cheat / Anti-Corruption).
    Versioning: Future-proofs your data. if data class changes
    */

    public static void Save<T>(T data, string path, bool portable = true, bool backup = true, bool compress = true) where T : new()//keep backup true , false is for download cache/or larger than 100MB save , that can be redownloaded , non critical save like cache
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // 1) Serialize (+ LZ4 compression due to options) , keep compress true, unless you must interop with other langs and make payload open to get its raw msgpack stream

        // Choose options based on compression flag
        var options = compress ? Options : OptionsNoCompression;
        byte[] payload = MessagePackSerializer.Serialize(data, options);
        SaveRawMsgPackBytes<T>(path, payload, portable, backup, compress);
    }

    public static void SaveRawMsgPackBytes<T>(string path, byte[] payload,
                                    bool portable = true, bool backup = true, bool isCompressed = true) where T : new() //so serialization and save can be split process in different threads(Helix.Options is public)
    {
        //var fileLock = GetLockForPath(path);
        //fileLock.Wait(); // block here if another thread is already saving this path
        //try
        //{
        //must Serialize on the main thread before putting it in the channel<T> of bg thread, when splitting save between threads (like timer auto-save)

        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var tmp = path + ".tmp";
        var bak = path + ".bak";

        // 1. Calculate Metadata

        Span<byte> typeHash = stackalloc byte[TypeHashSize];
        ComputeTypeHash(typeof(T), typeHash);

        long timestamp = DateTime.UtcNow.Ticks; // Absolute time, 100ns precision
                                                // 2) Integrity(detects corruption and some save cheating)
                                                // MAC over header fields + payload (prevents splicing)
        byte[] tag = ComputeTag(FormatVer, typeHash, timestamp, payload, portable, isCompressed);

        //aes ahead encrypt: payload?
        //"Preview metadata + Blob" Pattern?

        // 3) Write temp
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic); // 4
            bw.Write(FormatVer); // 2 format version
            bw.Write(isCompressed);  //1 byte: 0x00 or 0x01
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
                File.Replace(tmp, path, backup ? bak : null,
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
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (TryLoad(path, out T data, out _, portable)) return data;
        if (TryLoad(path + ".bak", out data, out _, portable)) return data;

        return new T();
    }
    public static T LoadOrFail<T>(string path, bool portable = true) where T : new()
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (TryLoad(path, out T data, out _, portable)) return data;
        if (TryLoad(path + ".bak", out data, out _, portable)) return data;

        throw new FileNotFoundException($"Could not load file or backup: {path}");
    }


    //Peek Header: impl later...
    //	public static bool PeekMetadata(string file, out long timestamp)
    //  {
    //	}

    private static bool TryLoad<T>(string file, out T data, out long timestamp, bool portable) where T : new()
    {
        data = default!;
        timestamp = 0;
        if (!File.Exists(file)) return false;
        try
        {
            byte[] all = File.ReadAllBytes(file);
            // Minimum header = 51 + N + 32 = 83 bytes , too small
            if (all.Length < HeaderSize + TagSize) return false;

            var span = all.AsSpan();
            if (!span[0..4].SequenceEqual(Magic)) return false;

            ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            if (ver != FormatVer) return false;



            // Read Flags (Offset 6)
            bool isCompressed = span[6] != 0;

            // Read type Hash (Offset 7)
            ReadOnlySpan<byte> fileTypeHash = span.Slice(7, TypeHashSize);

            Span<byte> expectedHash = stackalloc byte[TypeHashSize];
            ComputeTypeHash(typeof(T), expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(fileTypeHash, expectedHash))
                return false; // Type mismatch



            // Offset logic: Magic(4) + Ver(2) + Flags(1) + Hash(32) = 39
            timestamp = BinaryPrimitives.ReadInt64LittleEndian(span[39..]);

            // Offset logic: 39 + Time(8) = 47
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span[47..]);
            if (payloadLen <= 0 || all.Length != HeaderSize + payloadLen + TagSize)
                return false; //no need to limit to 100MB , user should use sharding for splitting large binary files

            var payload = span.Slice(HeaderSize, payloadLen);
            var tag = span.Slice(HeaderSize + payloadLen, TagSize);
            if (tag.Length != TagSize) return false;

            // Verify Integrity (Includes TypeHash and Timestamp in calculation)
            var expected = ComputeTag(ver, fileTypeHash, timestamp, payload, portable, isCompressed);//check tampered or corrupted

            // Constant-time compare to avoid timing leaks
            if (!CryptographicOperations.FixedTimeEquals(tag, expected))
                return false;

            // Create zero-copy ReadOnlyMemory from the original array 'all' , start slice after header index
            var payloadMemory = new ReadOnlyMemory<byte>(all, HeaderSize, payloadLen);

            // CRITICAL: Choose the correct deserializer based on the file flag!
            var options = isCompressed ? Options /*default best*/: OptionsNoCompression /*lang interop*/;
            data = MessagePackSerializer.Deserialize<T>(payloadMemory, options);
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

    private static byte[] ComputeTag(ushort formatVer, ReadOnlySpan<byte> typeHash, long timestamp, ReadOnlySpan<byte> payload, bool portable, bool isCompressed)
    {
        //prevents splicing and editing.
        using (var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, portable ? SentinelKeyStore.GlobalKey : SentinelKeyStore.MachineKey))
        {
            // We hash the header fields so they cannot be swapped (e.g. changing timestamp to prevent rollback detection)

            // Create a buffer for the header parts we want to sign
            // Ver(2) + TypeHash(32) + Timestamp(8) = 42 bytes
            Span<byte> header = stackalloc byte[43]; // 2 + 1 + 32 + 8

            BinaryPrimitives.WriteUInt16LittleEndian(header, formatVer);
            header[2] = (byte)(isCompressed ? 1 : 0); // Include Flag in HMAC
            // Copy the 32-byte hash into the header buffer at offset 3
            typeHash.CopyTo(header.Slice(3));
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(35), timestamp);

            h.AppendData(header);
            h.AppendData(payload);
            return h.GetHashAndReset();// 32 bytes
        }
    }

    // To Pure MessagePack for other lang introp/network stream
    private static void ExtractMsgPackToFile<T>(string path, bool portable = true) where T : new()
    {
        byte[] rawBytes = ExtractMsgPackCore<T>(path, portable);
        string outPath = Path.ChangeExtension(path, ".msgpack");
        File.WriteAllBytes(outPath, rawBytes);
    }
    private static MemoryStream ExtractMsgPackToStream<T>(string path, bool portable = true) where T : new()
    {
        byte[] rawBytes = ExtractMsgPackCore<T>(path, portable);
        return new MemoryStream(rawBytes, false);
    }

    private static byte[] ExtractMsgPackCore<T>(string path, bool portable) where T : new()
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var fs = File.OpenRead(path);
        if (fs.Length < HeaderSize + TagSize) throw new Exception("File too short");

        // 1. Peek Compression Flag (Offset 6)
        fs.Seek(6, SeekOrigin.Begin);
        int flag = fs.ReadByte();
        bool isCompressed = flag == 1;
        if (isCompressed)
        {
            //throw new InvalidOperationException("can only extract uncompressed payload");

            // SLOW PATH: // We must properly load it and re-save it as Raw MessagePack.
            fs.Close(); // Close stream so Helix load can open it

            // This verifies HMAC, Decrypts LZ4, and deserializes to object
            T data = LoadOrFail<T>(path, portable);

            // Serialize back to byte[] using NO compression options
            return MessagePackSerializer.Serialize(data, OptionsNoCompression);
        }

        // FAST PATH: Payload is already Raw MessagePack.
        // We just strip the Header (51 bytes) and Footer (32 bytes).
        // No deserialization needed.
        fs.Seek(47, SeekOrigin.Begin);

        // Read Payload Length
        byte[] lenBuffer = new byte[4];
        fs.ReadExactly(lenBuffer, 0, 4);
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuffer);

        // Validation
        if (payloadLen <= 0 || fs.Length != HeaderSize + payloadLen + TagSize)
            throw new Exception("File size mismatch / Corruption detected.");

        //doesn’t verify HMAC / type , if user chose no compression he chose that file will be public/open to extraction

        // Read exactly the payload bytes
        var rawBytes = new byte[payloadLen];
        fs.ReadExactly(rawBytes, 0, payloadLen);

        return rawBytes;
    }
    //.net file class static ops: into binary file

    public static string ReadAllText(string path, bool portable = true)
    {
        return LoadOrFail<TextWrapper>(path, portable).Contents;
    }

    public static void WriteAllText(string path, string? contents, bool portable = true)
    {
        Save<TextWrapper>(new TextWrapper { Contents = contents }, path, portable);
    }

    public static void WriteAllText(string path, ReadOnlySpan<char> contents, bool portable = true)
    {
        Save<TextWrapper>(new TextWrapper { Contents = contents.ToString() }, path, portable);
    }

    public static byte[] ReadAllBytes(string path, bool portable = true)
    {
        return LoadOrFail<BytesWrapper>(path, portable).Bytes;
    }

    public static void WriteAllBytes(string path, byte[] bytes, bool portable = true)
    {
        Save<BytesWrapper>(new BytesWrapper { Bytes = bytes }, path, portable);
    }

    public static string[] ReadAllLines(string path, bool portable = true)
    {
        return LoadOrFail<LinesWrapper>(path, portable).Contents;
    }
    public static void WriteAllLines(string path, string[] contents, bool portable = true)
        => WriteAllLines(path, (IEnumerable<string>)contents, portable);

    public static void WriteAllLines(string path, IEnumerable<string> contents, bool portable = true)
    {
        Save<LinesWrapper>(new LinesWrapper { Contents = contents.ToArray() }, path, portable);
    }


    [MessagePackObject]
    internal class TextWrapper
    {
        [Key(0)] public string Contents { get; set; }
    }
    [MessagePackObject]
    internal class LinesWrapper
    {
        [Key(0)] public string[] Contents { get; set; }
    }
    [MessagePackObject]
    internal class BytesWrapper
    {
        [Key(0)] public byte[] Bytes { get; set; }
    }
}