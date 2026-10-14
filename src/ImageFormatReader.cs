using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LayerChannelLive;

internal sealed record ImageFormatInfo(string Container, string Encoding, string Details)
{
    public string Display => string.IsNullOrWhiteSpace(Encoding) ? Container : Container + " · " + Encoding;
}

internal static class ImageFormatReader
{
    public static ImageFormatInfo Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new("未保存", "格式未知", "当前文档还没有对应的图片文件");
        string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (extension == "JPG") extension = "JPEG";
        if (extension == "TIF") extension = "TIFF";
        string container = extension.Length > 0 ? extension : "格式未知";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[148];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            ReadOnlySpan<byte> header = bytes.AsSpan(0, length);
            if (length >= 4 && header[..4].SequenceEqual("DDS "u8)) return ParseDds(header);
            if (container == "DDS") return new("DDS", "文件头不可识别", "文件头不符合 DDS 标识");
            if (length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) container = "PNG";
            else if (length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255) container = "JPEG";
            else if (length >= 2 && header[..2].SequenceEqual("BM"u8)) container = "BMP";
            else if (length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) container = "GIF";
            else if (length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) container = "WEBP";
            return new(container, "", "文件：" + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(container, container == "DDS" ? "编码暂不可读" : "", "当前无法读取文件头：" + path);
        }
    }

    // DDS_HEADER / DDS_PIXELFORMAT / DDS_HEADER_DXT10，字段均为 little endian。
    // 参考：Microsoft DDS Programming Guide 与 DXGI_FORMAT，原格式只读，不使用保存选项猜测。
    internal static ImageFormatInfo ParseDds(ReadOnlySpan<byte> header)
    {
        if (header.Length < 128 || !header[..4].SequenceEqual("DDS "u8) || U32(header, 4) != 124 || U32(header, 76) != 32)
            return new("DDS", "文件头不完整", "未能解析 DDS_HEADER / DDS_PIXELFORMAT");
        uint flags = U32(header, 80), code = U32(header, 84);
        string fourCC = Encoding.ASCII.GetString(header.Slice(84, 4)).TrimEnd('\0');
        if ((flags & 4) != 0)
        {
            if (fourCC == "DX10")
            {
                if (header.Length < 148) return new("DDS", "DX10 头不完整", "缺少 DDS_HEADER_DXT10");
                uint format = U32(header, 128);
                return new("DDS", DescribeDxgi(format), $"DX10 文件头 · DXGI_FORMAT={format} · 原文件编码");
            }
            string name = fourCC switch
            {
                "DXT1" => "BC1（线性，DXT1）",
                "DXT2" => "BC2（线性，DXT2，预乘）",
                "DXT3" => "BC2（线性，DXT3）",
                "DXT4" => "BC3（线性，DXT4，预乘）",
                "DXT5" => "BC3（线性，DXT5）",
                "RXGB" => "BC3（线性，RXGB）",
                "ATI1" => "BC4（线性，无符号，ATI1）",
                "BC4U" => "BC4（线性，无符号）",
                "BC4S" => "BC4（线性，有符号）",
                "ATI2" or "A2XY" => "BC5（线性，无符号，ATI2）",
                "BC5U" => "BC5（线性，无符号）",
                "BC5S" => "BC5（线性，有符号）",
                "BC6H" => "BC6H（线性，无符号）",
                "BC7L" or "BC7" => "BC7（线性）",
                _ => code switch
                {
                    36 => "R16G16B16A16（线性，无符号）", 110 => "R16G16B16A16（线性，有符号）",
                    111 => "R16（线性，浮点）", 112 => "R16G16（线性，浮点）", 113 => "R16G16B16A16（线性，浮点）",
                    114 => "R32（线性，浮点）", 115 => "R32G32（线性，浮点）", 116 => "R32G32B32A32（线性，浮点）",
                    _ => $"未知编码（FourCC {fourCC} / {code}）"
                }
            };
            return new("DDS", name, $"传统 DDS 文件头 · FourCC={fourCC} · 原文件编码");
        }
        uint bits = U32(header, 88), r = U32(header, 92), g = U32(header, 96), b = U32(header, 100), a = U32(header, 104);
        string uncompressed = (bits, r, g, b, a) switch
        {
            (32, 0xff0000, 0xff00, 0xff, 0xff000000) => "B8G8R8A8（线性，A8R8G8B8）",
            (32, 0xff0000, 0xff00, 0xff, 0) => "B8G8R8X8（线性，X8R8G8B8）",
            (32, 0xff, 0xff00, 0xff0000, 0xff000000) => "R8G8B8A8（线性，A8B8G8R8）",
            (32, 0xff, 0xff00, 0xff0000, 0) => "R8G8B8X8（线性，X8B8G8R8）",
            (24, 0xff0000, 0xff00, 0xff, 0) => "B8G8R8（线性，R8G8B8）",
            (16, 0xf800, 0x7e0, 0x1f, 0) => "B5G6R5（线性，R5G6B5）",
            (16, 0x7c00, 0x3e0, 0x1f, 0x8000) => "B5G5R5A1（线性，A1R5G5B5）",
            (16, 0xf00, 0xf0, 0xf, 0xf000) => "B4G4R4A4（线性，A4R4G4B4）",
            (8, 0xff, 0, 0, 0) => "R8（线性，无符号）",
            (16, 0xffff, 0, 0, 0) => "R16（线性，无符号）",
            (8, 0, 0, 0, 0xff) => "A8（线性）",
            _ => $"{bits} 位未压缩"
        };
        return new("DDS", uncompressed, $"传统 DDS 文件头 · {bits} 位像素 · 原文件编码");
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static string DescribeDxgi(uint format) => format switch
    {
        70 => "BC1（类型未指定）", 71 => "BC1（线性，DX10+）", 72 => "BC1（sRGB，DX10+）",
        73 => "BC2（类型未指定）", 74 => "BC2（线性，DX10+）", 75 => "BC2（sRGB，DX10+）",
        76 => "BC3（类型未指定）", 77 => "BC3（线性，DX10+）", 78 => "BC3（sRGB，DX10+）",
        79 => "BC4（类型未指定）", 80 => "BC4（线性，无符号）", 81 => "BC4（线性，有符号）",
        82 => "BC5（类型未指定）", 83 => "BC5（线性，无符号）", 84 => "BC5（线性，有符号）",
        94 => "BC6H（类型未指定）", 95 => "BC6H（线性，无符号）", 96 => "BC6H（线性，有符号）",
        97 => "BC7（类型未指定）", 98 => "BC7（线性，DX11+）", 99 => "BC7（sRGB，DX11+）",
        85 => "B5G6R5（线性）", 86 => "B5G5R5A1（线性）", 115 => "B4G4R4A4（线性）",
        87 => "B8G8R8A8（线性）", 91 => "B8G8R8A8（sRGB）", 88 => "B8G8R8X8（线性）", 93 => "B8G8R8X8（sRGB）",
        28 => "R8G8B8A8（线性）", 29 => "R8G8B8A8（sRGB）", 31 => "R8G8B8A8（有符号）",
        49 => "R8G8（线性，无符号）", 51 => "R8G8（线性，有符号）",
        61 => "R8（线性，无符号）", 63 => "R8（线性，有符号）", 65 => "A8（线性）",
        56 => "R16（线性，无符号）", 58 => "R16（线性，有符号）", 54 => "R16（线性，浮点）",
        41 => "R32（线性，浮点）", 16 => "R32G32（线性，浮点）", 2 => "R32G32B32A32（线性，浮点）",
        _ => $"DXGI {format}（未识别编码）"
    };
}
