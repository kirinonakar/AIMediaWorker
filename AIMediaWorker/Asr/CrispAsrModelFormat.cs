using System.Text;

namespace AIMediaWorker.Asr;

/// <summary>
/// Reads only the GGUF header/metadata needed to distinguish a CrispASR
/// Qwen3-ASR model from a llama.cpp multimodal Qwen3 model.  CrispASR uses a
/// single GGUF containing both the audio encoder and decoder; the
/// ggml-org/llama.cpp release uses a qwen3vl model plus a separate mmproj.
/// </summary>
internal static class CrispAsrModelFormat
{
    private const uint GgufMagic = 0x46554747; // ASCII "GGUF" in little endian.
    private const uint GgufVersion2 = 2;
    private const uint GgufVersion3 = 3;
    private const ulong MaxMetadataEntries = 100_000;
    private const ulong MaxStringBytes = 16 * 1024 * 1024;

    public static bool IsCrispAsrQwen3Model(string path)
    {
        try
        {
            var architecture = ReadArchitecture(path);
            return IsCrispAsrQwen3Architecture(architecture);
        }
        catch
        {
            return false;
        }
    }

    public static void ValidateCrispAsrQwen3Model(string path)
    {
        string architecture;
        try
        {
            architecture = ReadArchitecture(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException(
                $"The Qwen3 model is not a readable GGUF file: {path}", exception);
        }

        if (IsCrispAsrQwen3Architecture(architecture)) return;

        if (architecture.Equals("qwen3vl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The model '{path}' is a llama.cpp multimodal Qwen3-VL GGUF (architecture=qwen3vl). " +
                "CrispASR requires its single-file Qwen3-ASR GGUF (architecture=qwen3asr); " +
                "the separate mmproj file cannot be consumed by the CrispASR C ABI. " +
                "Run the ASR installer to replace this file with the CrispASR-compatible Q8_0 model.");
        }

        throw new InvalidOperationException(
            $"The model '{path}' has unsupported GGUF architecture '{architecture}'. " +
            "CrispASR Qwen3 requires architecture=qwen3asr.");
    }

    public static string ReadArchitecture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A GGUF path is required.", nameof(path));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != GgufMagic)
            throw new InvalidDataException("The file does not have a GGUF header.");

        var version = reader.ReadUInt32();
        if (version is not GgufVersion2 and not GgufVersion3)
            throw new InvalidDataException($"Unsupported GGUF version {version}.");

        _ = reader.ReadUInt64(); // tensor count
        var metadataCount = reader.ReadUInt64();
        if (metadataCount > MaxMetadataEntries)
            throw new InvalidDataException($"The GGUF metadata count is invalid: {metadataCount}.");

        for (ulong index = 0; index < metadataCount; index++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            if (key.Equals("general.architecture", StringComparison.Ordinal))
            {
                if (type != 8) throw new InvalidDataException("GGUF general.architecture is not a string.");
                return ReadString(reader);
            }

            SkipValue(reader, type);
        }

        throw new InvalidDataException("GGUF metadata does not contain general.architecture.");
    }

    private static bool IsCrispAsrQwen3Architecture(string architecture) =>
        architecture.Equals("qwen3asr", StringComparison.OrdinalIgnoreCase) ||
        architecture.Equals("qwen3_asr", StringComparison.OrdinalIgnoreCase);

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > MaxStringBytes || length > int.MaxValue)
            throw new InvalidDataException($"GGUF string length is invalid: {length}.");

        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > (ulong)long.MaxValue) throw new InvalidDataException("GGUF string length is invalid.");
        SkipBytes(reader, (long)length);
    }

    private static void SkipValue(BinaryReader reader, uint type)
    {
        switch (type)
        {
            case 0: _ = reader.ReadByte(); break;    // uint8
            case 1: _ = reader.ReadSByte(); break;   // int8
            case 2: _ = reader.ReadUInt16(); break; // uint16
            case 3: _ = reader.ReadInt16(); break;  // int16
            case 4: _ = reader.ReadUInt32(); break; // uint32
            case 5: _ = reader.ReadInt32(); break;  // int32
            case 6: _ = reader.ReadSingle(); break; // float32
            case 7: _ = reader.ReadBoolean(); break;
            case 8: SkipString(reader); break;
            case 9:
            {
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                if (count > MaxMetadataEntries * 10) throw new InvalidDataException("GGUF array length is invalid.");
                for (ulong index = 0; index < count; index++) SkipValue(reader, elementType);
                break;
            }
            case 10: _ = reader.ReadUInt64(); break; // uint64
            case 11: _ = reader.ReadInt64(); break;  // int64
            case 12: _ = reader.ReadDouble(); break; // float64
            default: throw new InvalidDataException($"Unknown GGUF metadata value type {type}.");
        }
    }

    private static void SkipBytes(BinaryReader reader, long count)
    {
        if (count < 0 || reader.BaseStream.Position > reader.BaseStream.Length - count)
            throw new EndOfStreamException();
        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }
}
