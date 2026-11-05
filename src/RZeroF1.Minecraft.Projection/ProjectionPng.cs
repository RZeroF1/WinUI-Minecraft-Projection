using System.Buffers.Binary;
using System.IO.Compression;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionPng
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] EncodeBgra(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("The BGRA pixel buffer does not match its dimensions.", nameof(pixels));

        using var raw = new MemoryStream(checked((width * 4 + 1) * height));
        var row = new byte[checked(width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            row[0] = 0;
            var source = pixels.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                row[offset + 1] = source[offset + 2];
                row[offset + 2] = source[offset + 1];
                row[offset + 3] = source[offset];
                row[offset + 4] = source[offset + 3];
            }
            raw.Write(row);
        }

        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            raw.CopyTo(zlib);

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(name);
        output.Write(data);
        var crc = 0xffffffffu;
        foreach (var value in name)
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[index] = value;
        }
        return table;
    }
}
