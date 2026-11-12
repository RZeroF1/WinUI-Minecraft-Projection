using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionPng
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal sealed record DecodedImage(int Width, int Height, byte[] Rgba)
    {
        internal bool HasTransparency
        {
            get
            {
                for (var index = 3; index < Rgba.Length; index += 4)
                    if (Rgba[index] != 255)
                        return true;
                return false;
            }
        }
    }

    internal static DecodedImage DecodeCanonicalPng(ReadOnlySpan<byte> png)
    {
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]));
        var result = ProjectionTextureLoader.DecodePng(png.ToArray(), out var pixels, out var decodedWidth, out var decodedHeight);
        if (result < 0 || decodedWidth != width || decodedHeight != height)
        {
            ProjectionTextureLoader.FreePng(pixels);
            throw new InvalidDataException("TextureDecodeFailure");
        }
        try
        {
            var rgba = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, rgba, 0, rgba.Length);
            for (var offset = 0; offset < rgba.Length; offset += 4)
                (rgba[offset], rgba[offset + 2]) = (rgba[offset + 2], rgba[offset]);
            return new(width, height, rgba);
        }
        finally { ProjectionTextureLoader.FreePng(pixels); }
    }

    internal static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("The RGBA pixel buffer does not match its dimensions.", nameof(rgba));
        var bgra = rgba.ToArray();
        for (var offset = 0; offset < bgra.Length; offset += 4)
            (bgra[offset], bgra[offset + 2]) = (bgra[offset + 2], bgra[offset]);
        return EncodeBgra(bgra, width, height);
    }

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
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    internal static DecodedImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Invalid PNG signature.");

        var cursor = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();
        while (cursor <= png.Length - 12)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[cursor..]));
            cursor += 4;
            if (length < 0 || length > 16 * 1024 * 1024 || cursor > png.Length - 8 || length > png.Length - cursor - 8)
                throw new InvalidDataException("PNG chunk exceeds the image budget.");
            var type = png.Slice(cursor, 4);
            cursor += 4;
            var data = png.Slice(cursor, length);
            cursor += length;
            cursor += 4;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                bitDepth = data[8];
                colorType = data[9];
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384 ||
                    (long)width * height > 16 * 1024 * 1024 || data[10] != 0 || data[11] != 0)
                    throw new InvalidDataException("PNG dimensions or compression are unsupported.");
                interlace = data[12];
                if (bitDepth != 8 || interlace != 0 || colorType is not (0 or 2 or 3 or 4 or 6))
                    throw new InvalidDataException("PNG color format is unsupported.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (length == 0 || length % 3 != 0 || length > 768) throw new InvalidDataException("Invalid PNG palette.");
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
                transparency = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8))
                compressed.Write(data);
            else if (type.SequenceEqual("IEND"u8))
                break;
        }

        if (width <= 0 || height <= 0 || compressed.Length == 0)
            throw new InvalidDataException("PNG is missing required chunks.");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Indexed PNG is missing its palette.");

        var channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
        var rowBytes = checked(width * channels);
        var filtered = new byte[checked((rowBytes + 1) * height)];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            var offset = 0;
            while (offset < filtered.Length)
            {
                var read = zlib.Read(filtered, offset, filtered.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            if (offset != filtered.Length || zlib.ReadByte() >= 0)
                throw new InvalidDataException("PNG scanline data is truncated or has trailing data.");
        }

        var scanlines = new byte[checked(rowBytes * height)];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (rowBytes + 1)];
            var source = filtered.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            var target = scanlines.AsSpan(y * rowBytes, rowBytes);
            var previous = y == 0 ? ReadOnlySpan<byte>.Empty : scanlines.AsSpan((y - 1) * rowBytes, rowBytes);
            Unfilter(source, target, previous, filter, channels);
        }

        var rgba = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = scanlines.AsSpan((y * width + x) * channels, channels);
            var destination = rgba.AsSpan((y * width + x) * 4, 4);
            switch (colorType)
            {
                case 0:
                    destination[0] = destination[1] = destination[2] = source[0];
                    destination[3] = transparency is { Length: >= 2 } && source[0] == transparency[1] ? (byte)0 : (byte)255;
                    break;
                case 2:
                    source[..3].CopyTo(destination);
                    destination[3] = transparency is { Length: >= 6 } &&
                        source[0] == transparency[1] && source[1] == transparency[3] && source[2] == transparency[5] ? (byte)0 : (byte)255;
                    break;
                case 3:
                    var paletteIndex = source[0];
                    if (palette is null || paletteIndex * 3 + 2 >= palette.Length)
                        throw new InvalidDataException("PNG palette index is invalid.");
                    destination[0] = palette[paletteIndex * 3];
                    destination[1] = palette[paletteIndex * 3 + 1];
                    destination[2] = palette[paletteIndex * 3 + 2];
                    destination[3] = transparency is not null && paletteIndex < transparency.Length ? transparency[paletteIndex] : (byte)255;
                    break;
                case 4:
                    destination[0] = destination[1] = destination[2] = source[0];
                    destination[3] = source[1];
                    break;
                case 6:
                    source.CopyTo(destination);
                    break;
            }
        }
        return new(width, height, rgba);
    }

    private static void Unfilter(ReadOnlySpan<byte> source, Span<byte> target, ReadOnlySpan<byte> previous, byte filter, int bytesPerPixel)
    {
        if (filter > 4) throw new InvalidDataException("PNG filter is invalid.");
        for (var index = 0; index < source.Length; index++)
        {
            byte left = index < bytesPerPixel ? (byte)0 : target[index - bytesPerPixel];
            byte up = previous.IsEmpty ? (byte)0 : previous[index];
            byte upperLeft = previous.IsEmpty || index < bytesPerPixel ? (byte)0 : previous[index - bytesPerPixel];
            target[index] = filter switch
            {
                0 => source[index],
                1 => unchecked((byte)(source[index] + left)),
                2 => unchecked((byte)(source[index] + up)),
                3 => unchecked((byte)(source[index] + ((left + up) / 2))),
                _ => unchecked((byte)(source[index] + Paeth(left, up, upperLeft)))
            };
        }
    }

    private static byte Paeth(byte left, byte up, byte upperLeft)
    {
        var prediction = left + up - upperLeft;
        var leftDistance = Math.Abs(prediction - left);
        var upDistance = Math.Abs(prediction - up);
        var upperLeftDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance ? left :
            upDistance <= upperLeftDistance ? up : upperLeft;
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
