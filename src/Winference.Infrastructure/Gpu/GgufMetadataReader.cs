// Adapted from Osmantic/ODS, ods/extensions/services/dashboard-api/gguf_inspector.py
// ODS revision 72e59cdc665fc576d300061cabb5b980cbbaf1da; Apache-2.0.
// Original SHA256: b9e8bccb32c6d8f40215b53148fcc3d0ab731202cd319e87cb2c09d032d8014c.
// Changes: native streaming reader, strict UTF-8, bounded metadata/arrays, selected
// scalar fields only, and rejection of unsupported split/non-chat/projector files.
// Value-type widths, nested-array skipping and llama_ftype labels are source ports.
using System.Text;
using Winference.Core.Gpu;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Gpu;

public static class GgufMetadataReader
{
    public const long MaximumMetadataBytes = 32L * 1024 * 1024;
    private const int MaximumStringBytes = 1_048_576;
    private const ulong MaximumArrayItems = 2_000_000;
    private const int MaximumArrayDepth = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Dictionary<ulong, string> FileTypes = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0",
        [8] = "Q5_0", [9] = "Q5_1", [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M",
        [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M", [16] = "Q5_K_S",
        [17] = "Q5_K_M", [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS",
        [21] = "Q2_K_S", [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S",
        [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M", [28] = "IQ2_S",
        [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M", [32] = "BF16",
        [36] = "TQ1_0", [37] = "TQ2_0"
    };

    /// <summary>Reads only the metadata header. The caller owns the seekable stream.</summary>
    public static GgufModelMetadata Read(Stream stream, string fallbackName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("GGUF metadata needs a readable seekable stream.", nameof(stream));
        try
        {
            stream.Position = 0;
            var reader = new Reader(stream, cancellationToken);
            if (!reader.Bytes(4).SequenceEqual("GGUF"u8.ToArray()))
                throw Invalid("The selected file is not a GGUF model.");
            var version = reader.UInt32();
            if (version is not (2 or 3)) throw Invalid("This GGUF version is not supported. Choose a GGUF version 2 or 3 file.");
            var tensorCount = reader.UInt64();
            var count = reader.UInt64();
            if (tensorCount is 0 or > 1_000_000 || count is 0 or > 100_000)
                throw Invalid("The GGUF header has an invalid tensor or metadata count.");
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var visionMetadata = false;
            for (ulong index = 0; index < count; ++index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = reader.String(4096);
                if (key.Length == 0 || !keys.Add(key)) throw Invalid("The GGUF contains an empty or duplicate metadata key.");
                var type = reader.UInt32();
                visionMetadata |= key.StartsWith("clip.", StringComparison.Ordinal) || key.StartsWith("vision.", StringComparison.Ordinal);
                if (Wanted(key))
                {
                    if (type == 9) throw Invalid("The GGUF contains an unsupported array in its chat model settings.");
                    values[key] = reader.Scalar(type);
                }
                else reader.SkipValue(type);
            }
            foreach (var setting in values.Keys.Where(key => key is "general.file_type" or "general.alignment" or "split.count" || key.EndsWith(".context_length", StringComparison.Ordinal)))
                if (Number(values, setting) is null) throw Invalid("A GGUF numeric model setting has an invalid value type.");
            foreach (var setting in new[] { "general.name", "general.architecture", "general.type", "tokenizer.ggml.model", "tokenizer.chat_template" })
                if (values.TryGetValue(setting, out var value) && value is not string)
                    throw Invalid("A GGUF text model setting has an invalid value type.");
            // Tensor descriptors are still header data, not tensor payload.
            // Require their names/dimensions/offsets to be present before accepting a completed download.
            var alignment = Number(values, "general.alignment") ?? 32;
            if (alignment is 0 or > 4096 || (alignment & (alignment - 1)) != 0)
                throw Invalid("The GGUF declares an invalid tensor alignment.");
            ulong greatestOffset = 0;
            for (ulong index = 0; index < tensorCount; ++index)
            {
                if (reader.String(4096).Length == 0) throw Invalid("A GGUF tensor has no name.");
                var dimensions = reader.UInt32();
                if (dimensions is 0 or > 8) throw Invalid("A GGUF tensor has an invalid dimension count.");
                for (uint dimension = 0; dimension < dimensions; ++dimension)
                    if (reader.UInt64() is 0 or > long.MaxValue) throw Invalid("A GGUF tensor has an invalid dimension.");
                _ = reader.UInt32(); // llama.cpp validates the selected build's tensor-type support on activation.
                greatestOffset = Math.Max(greatestOffset, reader.UInt64());
            }
            var payloadStart = checked(((ulong)stream.Position + alignment - 1) / alignment * alignment);
            if (payloadStart >= (ulong)stream.Length || greatestOffset >= (ulong)stream.Length - payloadStart)
                throw Invalid("The GGUF tensor payload is missing or truncated. Finish the download before choosing this file.");
            if (Number(values, "split.count") is { } splits && splits != 1 ||
                keys.Contains("split.no") && !values.ContainsKey("split.count"))
                throw Invalid("Split GGUF models are not supported in this phase. Choose one complete GGUF file.");
            var architecture = Text(values, "general.architecture");
            if (string.IsNullOrWhiteSpace(architecture) || architecture.Length > 64 ||
                architecture.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
                throw Invalid("The GGUF does not identify a supported text-model architecture.");
            var modelType = Text(values, "general.type");
            if (visionMetadata || modelType is "projector" or "adapter" ||
                architecture.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                architecture.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
                architecture is "clip" or "bert" or "nomic-bert" or "jina-bert-v2" or "t5" or "t5encoder")
                throw Invalid("Choose a text-chat GGUF. Projectors, vision, embedding and adapter files are not supported in this phase.");
            var tokenizer = Text(values, "tokenizer.ggml.model");
            var template = Text(values, "tokenizer.chat_template");
            if (string.IsNullOrWhiteSpace(tokenizer) || string.IsNullOrWhiteSpace(template))
                throw Invalid("This GGUF does not include a tokenizer and chat template. Choose a complete instruction/chat model.");
            var maximumContext = Number(values, architecture + ".context_length");
            int? context = maximumContext is > 0 and <= int.MaxValue ? (int)maximumContext.Value : null;
            if (context is < 512) throw Invalid("This GGUF's declared context is too small for the bounded chat profile.");
            var name = Text(values, "general.name");
            if (string.IsNullOrWhiteSpace(name)) name = fallbackName;
            name = SanitizeName(name);
            var fileType = Number(values, "general.file_type");
            var quantization = fileType is { } kind ? FileTypes.GetValueOrDefault(kind, "Unknown") : "Unknown";
            return new(name, architecture, quantization, context, tokenizer, template, tensorCount, version);
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            throw Invalid("The GGUF metadata is truncated or malformed. The existing file was preserved.", error);
        }
    }

    private static bool Wanted(string key) => key is "general.name" or "general.architecture" or "general.file_type" or "general.alignment" or
        "general.type" or "tokenizer.ggml.model" or "tokenizer.chat_template" or "split.count" ||
        key.EndsWith(".context_length", StringComparison.Ordinal);
    private static string Text(Dictionary<string, object?> values, string key) => values.GetValueOrDefault(key) as string ?? string.Empty;
    private static ulong? Number(Dictionary<string, object?> values, string key) => values.GetValueOrDefault(key) switch
    {
        byte value => value, ushort value => value, uint value => value, ulong value => value,
        sbyte value when value >= 0 => (ulong)value, short value when value >= 0 => (ulong)value,
        int value when value >= 0 => (ulong)value, long value when value >= 0 => (ulong)value,
        _ => null
    };
    private static string SanitizeName(string value)
    {
        var name = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0) name = "Local GGUF model";
        return name[..Math.Min(name.Length, 200)];
    }
    private static RuntimeException Invalid(string message, Exception? inner = null) => new("gguf_metadata", message, inner);

    private sealed class Reader(Stream stream, CancellationToken token)
    {
        private readonly BinaryReader _binary = new(stream, StrictUtf8, leaveOpen: true);
        public byte[] Bytes(int length)
        {
            Ensure(length);
            var bytes = _binary.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return bytes;
        }
        public uint UInt32() { Ensure(4); return _binary.ReadUInt32(); }
        public ulong UInt64() { Ensure(8); return _binary.ReadUInt64(); }
        public string String(int limit = MaximumStringBytes)
        {
            var length = UInt64();
            if (length > (ulong)limit) throw Invalid("A GGUF metadata string exceeds the supported size.");
            return StrictUtf8.GetString(Bytes(checked((int)length)));
        }
        public object Scalar(uint type)
        {
            if (type == 8) return String();
            Ensure(Width(type));
            return type switch
            {
                0 => _binary.ReadByte(), 1 => _binary.ReadSByte(), 2 => _binary.ReadUInt16(), 3 => _binary.ReadInt16(),
                4 => _binary.ReadUInt32(), 5 => _binary.ReadInt32(), 6 => _binary.ReadSingle(), 7 => _binary.ReadBoolean(),
                10 => _binary.ReadUInt64(), 11 => _binary.ReadInt64(), 12 => _binary.ReadDouble(),
                _ => throw Invalid("The GGUF contains an unknown metadata value type.")
            };
        }
        public void SkipValue(uint type, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (type == 8)
            {
                var length = UInt64();
                if (length > MaximumMetadataBytes) throw Invalid("A GGUF metadata string exceeds the supported header size.");
                Skip(checked((long)length));
            }
            else if (type == 9)
            {
                if (depth >= MaximumArrayDepth) throw Invalid("The GGUF has excessively nested metadata arrays.");
                var itemType = UInt32();
                var count = UInt64();
                if (count > MaximumArrayItems) throw Invalid("The GGUF metadata array exceeds the supported size.");
                if (itemType is not (8 or 9)) Skip(checked(Width(itemType) * (long)count));
                else for (ulong index = 0; index < count; ++index) SkipValue(itemType, depth + 1);
            }
            else Skip(Width(type));
        }
        private void Skip(long length) { Ensure(length); stream.Seek(length, SeekOrigin.Current); }
        private void Ensure(long length)
        {
            token.ThrowIfCancellationRequested();
            if (length < 0 || stream.Position > MaximumMetadataBytes - length || stream.Position > stream.Length - length)
                throw new EndOfStreamException();
        }
        private static int Width(uint type) => type switch
        {
            0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8,
            _ => throw Invalid("The GGUF contains an unknown metadata value type.")
        };
    }
}
