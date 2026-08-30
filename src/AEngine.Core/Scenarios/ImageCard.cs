using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AEngine.Core.Scenarios;

/// <summary>
/// Image "cards": scenario documents (modules.json, world.json) embedded in
/// a PNG or JPEG's metadata so a scenario can be shared as a single image.
/// The payload is one JSON object {file name: file text}, zlib-compressed.
/// PNG carries it in a zTXt chunk (keyword "aengine-card"), with the
/// scenario title in a standard tEXt "Title" chunk; JPEG carries both in
/// COM segments (0xFFFE), chunked under the 64KB segment limit. Embedding
/// and stripping copy the image bytes verbatim — no recompression.
/// </summary>
public static class ImageCard
{
    public enum Format
    {
        Png,
        Jpeg,
    }

    private const string CardKeyword = "aengine-card";
    private const string TitleKeyword = "Title"; // the standard PNG tEXt keyword
    private const string JpegCardHeader = "aengine-card "; // + "<i>/<n>\n" + chunk bytes
    private const string JpegTitleHeader = "aengine-title\n";
    private const int JpegChunkMax = 65000; // COM segment data limit is 65533; leave room for the header

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Detect the image format by magic bytes (extension-independent).</summary>
    public static Format? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(PngMagic))
            return Format.Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return Format.Jpeg;
        return null;
    }

    /// <summary>Detect the image format of a file, reading only its header.</summary>
    public static Format? DetectFile(string path)
    {
        if (!File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        return Detect(header[..stream.Read(header)]);
    }

    /// <summary>Encode scenario documents as the compressed card payload.</summary>
    public static byte[] EncodePayload(IReadOnlyDictionary<string, string> documents)
    {
        var json = JsonSerializer.Serialize(documents);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    /// <summary>Decode a compressed card payload back into scenario documents.</summary>
    public static IReadOnlyDictionary<string, string> DecodePayload(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(output.ToArray())
            ?? throw new InvalidDataException("Card payload parsed to null.");
    }

    /// <summary>Return the image with the scenario documents (and title)
    /// embedded; any existing card data is replaced.</summary>
    public static byte[] Embed(
        byte[] image, Format format, IReadOnlyDictionary<string, string> documents, string? title = null) =>
        format == Format.Png
            ? EmbedPng(image, EncodePayload(documents), title)
            : EmbedJpeg(image, EncodePayload(documents), title);

    /// <summary>Extract the scenario documents, or null when the image carries none.</summary>
    public static IReadOnlyDictionary<string, string>? Extract(byte[] image, Format format) =>
        format == Format.Png ? ExtractPng(image) : ExtractJpeg(image);

    /// <summary>Extract the scenario title (the PNG tEXt "Title" chunk or
    /// the JPEG title COM segment), or null when the image carries none.</summary>
    public static string? ExtractTitle(byte[] image, Format format) =>
        format == Format.Png ? ExtractPngTitle(image) : ExtractJpegTitle(image);

    /// <summary>Return the image with all card metadata removed.</summary>
    public static byte[] Strip(byte[] image, Format format) =>
        format == Format.Png ? StripPng(image) : StripJpeg(image);

    // --- PNG ---

    private static List<(string Type, byte[] Data)> ParsePng(byte[] png)
    {
        if (Detect(png) != Format.Png)
            throw new InvalidDataException("Not a PNG image.");
        var chunks = new List<(string, byte[])>();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var dataEnd = pos + 8 + (int)length;
            if (dataEnd + 4 > png.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            chunks.Add((type, png[(pos + 8)..dataEnd]));
            pos = dataEnd + 4; // skip the CRC (not validated)
            if (type == "IEND")
                return chunks;
        }
        throw new InvalidDataException("PNG has no IEND chunk.");
    }

    private static byte[] BuildPng(List<(string Type, byte[] Data)> chunks)
    {
        using var output = new MemoryStream();
        output.Write(PngMagic);
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            typeBytes.CopyTo(header[4..]);
            output.Write(header);
            output.Write(data);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(typeBytes, data));
            output.Write(crc);
        }
        return output.ToArray();
    }

    /// <summary>The keyword of a tEXt/zTXt chunk (the bytes before the null separator).</summary>
    private static string TextKeyword(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0);
        return Encoding.ASCII.GetString(data, 0, end < 0 ? data.Length : end);
    }

    private static bool IsCardChunk(string type, byte[] data) =>
        (type == "zTXt" || type == "tEXt") &&
        TextKeyword(data) is CardKeyword or TitleKeyword;

    private static byte[] EmbedPng(byte[] png, byte[] payload, string? title)
    {
        var chunks = ParsePng(png);
        chunks.RemoveAll(c => IsCardChunk(c.Type, c.Data));
        var insertAt = chunks.Count - 1; // before IEND (tEXt/zTXt are legal anywhere after IHDR)
        if (title is not null)
        {
            // tEXt is Latin-1; unmappable characters degrade to '?'
            var data = Encoding.Latin1.GetBytes(TitleKeyword + '\0' + title);
            chunks.Insert(insertAt++, ("tEXt", data));
        }
        var zdata = new List<byte>(Encoding.ASCII.GetBytes(CardKeyword)) { 0, 0 } // keyword, null, method 0 (zlib)
            .Concat(payload).ToArray();
        chunks.Insert(insertAt, ("zTXt", zdata));
        return BuildPng(chunks);
    }

    private static IReadOnlyDictionary<string, string>? ExtractPng(byte[] png)
    {
        foreach (var (type, data) in ParsePng(png))
        {
            if (type != "zTXt" || TextKeyword(data) != CardKeyword)
                continue;
            var start = TextKeyword(data).Length + 1; // past the null separator
            if (start >= data.Length || data[start] != 0)
                throw new InvalidDataException("Card zTXt chunk uses an unsupported compression method.");
            return DecodePayload(data[(start + 1)..]);
        }
        return null;
    }

    private static string? ExtractPngTitle(byte[] png)
    {
        foreach (var (type, data) in ParsePng(png))
        {
            if (type != "tEXt" || TextKeyword(data) != TitleKeyword)
                continue;
            return Encoding.Latin1.GetString(data, TitleKeyword.Length + 1,
                data.Length - TitleKeyword.Length - 1);
        }
        return null;
    }

    private static byte[] StripPng(byte[] png)
    {
        var chunks = ParsePng(png);
        chunks.RemoveAll(c => IsCardChunk(c.Type, c.Data));
        return BuildPng(chunks);
    }

    // --- JPEG ---

    /// <summary>JPEG segments up to the scan (SOS); the rest of the file is
    /// returned verbatim. A null Data marks a standalone (length-less) marker.</summary>
    private static (List<(byte Marker, byte[]? Data)> Segments, byte[] Remainder) ParseJpeg(byte[] jpeg)
    {
        if (Detect(jpeg) != Format.Jpeg)
            throw new InvalidDataException("Not a JPEG image.");
        var segments = new List<(byte, byte[]?)>();
        var pos = 2; // past SOI
        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw new InvalidDataException("Corrupt JPEG segment.");
            var marker = jpeg[pos + 1];
            if (marker == 0xDA) // start of scan: entropy-coded data follows
                return (segments, jpeg[pos..]);
            if (marker is 0x01 or 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) // standalone markers
            {
                segments.Add((marker, null));
                pos += 2;
                continue;
            }
            if (pos + 4 > jpeg.Length)
                throw new InvalidDataException("Truncated JPEG segment.");
            var end = pos + 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (end > jpeg.Length)
                throw new InvalidDataException("Truncated JPEG segment.");
            segments.Add((marker, jpeg[(pos + 4)..end]));
            pos = end;
        }
        return (segments, []); // no SOS: tolerate, the whole file parsed as segments
    }

    private static byte[] BuildJpeg(List<(byte Marker, byte[]? Data)> segments, byte[] rest)
    {
        using var output = new MemoryStream();
        output.WriteByte(0xFF);
        output.WriteByte(0xD8); // SOI
        foreach (var (marker, data) in segments)
        {
            output.WriteByte(0xFF);
            output.WriteByte(marker);
            if (data is null)
                continue;
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(data.Length + 2));
            output.Write(length);
            output.Write(data);
        }
        output.Write(rest);
        return output.ToArray();
    }

    private static bool IsCardSegment(byte marker, byte[]? data, out string header)
    {
        header = "";
        if (marker != 0xFE || data is null)
            return false;
        var text = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 64));
        if (text.StartsWith(JpegCardHeader, StringComparison.Ordinal))
            header = JpegCardHeader;
        else if (text.StartsWith(JpegTitleHeader, StringComparison.Ordinal))
            header = JpegTitleHeader;
        return header.Length > 0;
    }

    private static byte[] EmbedJpeg(byte[] jpeg, byte[] payload, string? title)
    {
        var (segments, rest) = ParseJpeg(jpeg);
        segments.RemoveAll(s => IsCardSegment(s.Marker, s.Data, out _));
        // insert after any leading APP0/APP1 so JFIF/EXIF recognition survives
        var insertAt = segments.FindIndex(s => s.Marker is not (0xE0 or 0xE1));
        if (insertAt < 0)
            insertAt = segments.Count;
        if (title is not null)
            segments.Insert(insertAt++, ((byte)0xFE, Encoding.UTF8.GetBytes(JpegTitleHeader + title)));
        var chunks = (payload.Length + JpegChunkMax - 1) / JpegChunkMax;
        for (var i = 0; i < chunks; i++)
        {
            var header = Encoding.ASCII.GetBytes($"{JpegCardHeader}{i}/{chunks}\n");
            var chunk = payload.Skip(i * JpegChunkMax).Take(JpegChunkMax).ToArray();
            segments.Insert(insertAt++, ((byte)0xFE, header.Concat(chunk).ToArray()));
        }
        return BuildJpeg(segments, rest);
    }

    private static IReadOnlyDictionary<string, string>? ExtractJpeg(byte[] jpeg)
    {
        var (segments, _) = ParseJpeg(jpeg);
        var chunks = new List<(int Index, int Count, byte[] Data)>();
        foreach (var (marker, data) in segments)
        {
            if (!IsCardSegment(marker, data, out var header) || header != JpegCardHeader)
                continue;
            var headerEnd = Array.IndexOf(data!, (byte)'\n');
            var parts = Encoding.ASCII.GetString(data!, 0, headerEnd)[JpegCardHeader.Length..].Split('/');
            chunks.Add((int.Parse(parts[0]), int.Parse(parts[1]), data![(headerEnd + 1)..]));
        }
        if (chunks.Count == 0)
            return null;
        var count = chunks[0].Count;
        if (chunks.Any(c => c.Count != count) ||
            chunks.Select(c => c.Index).OrderBy(i => i).ToArray() is var order &&
            (order[0] != 0 || order[^1] != count - 1 || order.Distinct().Count() != count))
            throw new InvalidDataException("JPEG card segments are incomplete or inconsistent.");
        var payload = chunks.OrderBy(c => c.Index).SelectMany(c => c.Data).ToArray();
        return DecodePayload(payload);
    }

    private static string? ExtractJpegTitle(byte[] jpeg)
    {
        var (segments, _) = ParseJpeg(jpeg);
        foreach (var (marker, data) in segments)
        {
            if (IsCardSegment(marker, data, out var header) && header == JpegTitleHeader)
                return Encoding.UTF8.GetString(data![JpegTitleHeader.Length..]);
        }
        return null;
    }

    private static byte[] StripJpeg(byte[] jpeg)
    {
        var (segments, rest) = ParseJpeg(jpeg);
        segments.RemoveAll(s => IsCardSegment(s.Marker, s.Data, out _));
        return BuildJpeg(segments, rest);
    }

    // --- CRC32 (PNG chunk checksums; System.IO.Hashing is not in the BCL) ---

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in type)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
