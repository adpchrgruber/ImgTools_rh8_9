using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ImgTools
{
    /// <summary>
    /// Minimal image writers: greyscale and RGB PNG (8 or 16 bit), RGBA PNG for the live views,
    /// and uncompressed float OpenEXR with any channels. No dependencies, the same on Mac and Windows.
    /// </summary>
    internal static class ImageWriters
    {
        public static void WritePng8(string path, int width, int height, byte[] pixels)
        {
            WritePng(path, width, height, 8, pixels);
        }

        /// <summary>
        /// Grey levels as an opaque RGBA PNG into a stream, compressed fast. For the live views:
        /// Rhino 8 does not draw greyscale bitmaps in the viewport, RGBA it does.
        /// </summary>
        public static void WritePngRgba(Stream stream, int width, int height, byte[] grey)
        {
            var rgba = new byte[grey.Length * 4];
            for (int i = 0, k = 0; i < grey.Length; i++, k += 4)
            {
                rgba[k] = rgba[k + 1] = rgba[k + 2] = grey[i];
                rgba[k + 3] = 255;
            }
            WritePng(stream, width, height, 8, rgba, CompressionLevel.Fastest, colorType: 6);
        }

        /// <summary>RGB triples (r, g, b per pixel) as an opaque RGBA PNG into a stream, compressed fast.</summary>
        public static void WritePngRgbaFromRgb(Stream stream, int width, int height, byte[] rgb)
        {
            var rgba = new byte[rgb.Length / 3 * 4];
            for (int i = 0, k = 0; i < rgb.Length; i += 3, k += 4)
            {
                rgba[k] = rgb[i];
                rgba[k + 1] = rgb[i + 1];
                rgba[k + 2] = rgb[i + 2];
                rgba[k + 3] = 255;
            }
            WritePng(stream, width, height, 8, rgba, CompressionLevel.Fastest, colorType: 6);
        }

        /// <summary>8-bit RGB PNG; rgb holds r, g, b per pixel.</summary>
        public static void WritePngRgb8(string path, int width, int height, byte[] rgb)
        {
            using (var stream = File.Create(path))
                WritePng(stream, width, height, 8, rgb, CompressionLevel.Optimal, colorType: 2);
        }

        /// <summary>16-bit RGB PNG; rgb holds r, g, b per pixel.</summary>
        public static void WritePngRgb16(string path, int width, int height, ushort[] rgb)
        {
            using (var stream = File.Create(path))
                WritePng(stream, width, height, 16, BigEndian(rgb), CompressionLevel.Optimal, colorType: 2);
        }

        public static void WritePng16(string path, int width, int height, ushort[] pixels)
        {
            WritePng(path, width, height, 16, BigEndian(pixels));
        }

        static byte[] BigEndian(ushort[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                bytes[2 * i] = (byte)(samples[i] >> 8);   // PNG samples are big-endian
                bytes[2 * i + 1] = (byte)samples[i];
            }
            return bytes;
        }

        /// <summary>
        /// Scanline OpenEXR, no compression, one FLOAT channel named "Z", rows top to bottom.
        /// </summary>
        public static void WriteExr(string path, int width, int height, float[] z)
        {
            WriteExr(path, width, height, ("Z", z));
        }

        /// <summary>
        /// Scanline OpenEXR, no compression, FLOAT channels (name, one value per pixel), rows top
        /// to bottom. Channels are stored in name order, as the format requires.
        /// </summary>
        public static void WriteExr(string path, int width, int height, params (string Name, float[] Data)[] channels)
        {
            var sorted = ((string Name, float[] Data)[])channels.Clone();
            Array.Sort(sorted, (p, q) => string.CompareOrdinal(p.Name, q.Name));
            using (var stream = File.Create(path))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(20000630);  // magic
                w.Write(2);         // version 2, single-part scanline

                int listSize = 1;
                foreach (var c in sorted)
                    listSize += c.Name.Length + 1 + 16;
                Attribute(w, "channels", "chlist", listSize, () =>
                {
                    foreach (var c in sorted)
                    {
                        w.Write(Encoding.ASCII.GetBytes(c.Name + "\0"));
                        w.Write(2);           // FLOAT
                        w.Write(0);           // pLinear + 3 reserved bytes
                        w.Write(1);           // xSampling
                        w.Write(1);           // ySampling
                    }
                    w.Write((byte)0);         // end of channel list
                });
                Attribute(w, "compression", "compression", 1, () => w.Write((byte)0));
                Attribute(w, "dataWindow", "box2i", 16, () => { w.Write(0); w.Write(0); w.Write(width - 1); w.Write(height - 1); });
                Attribute(w, "displayWindow", "box2i", 16, () => { w.Write(0); w.Write(0); w.Write(width - 1); w.Write(height - 1); });
                Attribute(w, "lineOrder", "lineOrder", 1, () => w.Write((byte)0));
                Attribute(w, "pixelAspectRatio", "float", 4, () => w.Write(1f));
                Attribute(w, "screenWindowCenter", "v2f", 8, () => { w.Write(0f); w.Write(0f); });
                Attribute(w, "screenWindowWidth", "float", 4, () => w.Write(1f));
                w.Write((byte)0);   // end of header

                // Offset table: one block per scanline, each block = y, size, data
                // (each channel's row in turn).
                long tableStart = stream.Position;
                long rowBytes = 4L * width * sorted.Length;
                long first = tableStart + 8L * height;
                for (int y = 0; y < height; y++)
                    w.Write(first + y * (8 + rowBytes));
                for (int y = 0; y < height; y++)
                {
                    w.Write(y);
                    w.Write((int)rowBytes);
                    foreach (var c in sorted)
                        for (int x = 0; x < width; x++)
                            w.Write(c.Data[y * width + x]);
                }
            }
        }

        static void Attribute(BinaryWriter w, string name, string type, int size, Action value)
        {
            w.Write(Encoding.ASCII.GetBytes(name + "\0" + type + "\0"));
            w.Write(size);
            value();
        }

        static void WritePng(string path, int width, int height, int bitDepth, byte[] samples)
        {
            using (var stream = File.Create(path))
                WritePng(stream, width, height, bitDepth, samples, CompressionLevel.Optimal);
        }

        /// <param name="colorType">0 greyscale, 2 RGB, 6 RGBA.</param>
        static void WritePng(Stream stream, int width, int height, int bitDepth, byte[] samples, CompressionLevel level, int colorType = 0)
        {
            int channels = colorType == 6 ? 4 : colorType == 2 ? 3 : 1;
            int rowBytes = width * channels * bitDepth / 8;
            byte[] compressed;
            using (var zlib = new MemoryStream())
            {
                zlib.WriteByte(0x78);   // zlib header: deflate, 32K window
                zlib.WriteByte(0x01);
                uint a = 1, b = 0;      // Adler-32 of the uncompressed data
                using (var deflate = new DeflateStream(zlib, level, true))
                {
                    var row = new byte[rowBytes + 1];
                    row[0] = 2;         // filter "Up": depth changes slowly from row to row
                    for (int y = 0; y < height; y++)
                    {
                        int o = y * rowBytes;
                        for (int i = 0; i < rowBytes; i++)
                            row[i + 1] = (byte)(samples[o + i] - (y > 0 ? samples[o - rowBytes + i] : 0));
                        deflate.Write(row, 0, row.Length);
                        foreach (var v in row)
                        {
                            a = (a + v) % 65521;
                            b = (b + a) % 65521;
                        }
                    }
                }
                WriteBigEndian(zlib, (b << 16) | a);
                compressed = zlib.ToArray();
            }

            {
                stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var header = new MemoryStream();
                WriteBigEndian(header, (uint)width);
                WriteBigEndian(header, (uint)height);
                header.WriteByte((byte)bitDepth);
                header.WriteByte((byte)colorType);   // 0 greyscale, 2 RGB, 6 RGBA
                header.WriteByte(0);    // deflate
                header.WriteByte(0);    // adaptive filtering
                header.WriteByte(0);    // no interlace
                Chunk(stream, "IHDR", header.ToArray());
                Chunk(stream, "IDAT", compressed);
                Chunk(stream, "IEND", new byte[0]);
            }
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            WriteBigEndian(s, (uint)data.Length);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(Crc32(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
            WriteBigEndian(s, crc);
        }

        static void WriteBigEndian(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        static uint Crc32(uint crc, byte[] data)
        {
            foreach (var b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
