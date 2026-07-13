using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace AnimationImage.Apng
{
    /// <summary>PNG 分块（chunk）在文件中的物理位置。</summary>
    internal readonly struct ChunkLocation
    {
        public long DataOffset { get; }
        public int Length { get; }

        public ChunkLocation(long dataOffset, int length)
        {
            DataOffset = dataOffset;
            Length = length;
        }
    }

    /// <summary>APNG dispose_op（帧结束时对上一帧区域的处理）。</summary>
    internal enum ApngDisposeOp : byte
    {
        None = 0,       // 保留当前帧内容
        Background = 1, // 将当前帧区域清成透明
        Previous = 2    // 恢复到本帧渲染前的画布快照
    }

    /// <summary>APNG blend_op（当前帧像素与画布的混合方式）。</summary>
    internal enum ApngBlendOp : byte
    {
        Source = 0, // 直接覆盖（含 alpha）
        Over = 1    // 按 alpha 混合到画布
    }

    /// <summary>单个动画帧的参数与数据块指针。</summary>
    internal sealed class ApngFrameEntry
    {
        public int Width;                          // 子帧宽
        public int Height;                         // 子帧高
        public int OffsetX;                        // 子帧在主画布上的 X 偏移
        public int OffsetY;                        // 子帧在主画布上的 Y 偏移
        public double Duration;                  // 该帧显示时长（毫秒）
        public ApngDisposeOp DisposeOp;
        public ApngBlendOp BlendOp;
        public List<ChunkLocation> DataChunks = []; // IDAT / fdAT 的物理位置列表（保持出现顺序）
    }

    /// <summary>APNG 全局元数据（来自 IHDR + acTL）。</summary>
    internal sealed class ApngHeader
    {
        public int CanvasWidth;                    // IHDR width
        public int CanvasHeight;                   // IHDR height
        public byte BitDepth;
        public byte ColorType;
        public byte CompressionMethod;
        public byte FilterMethod;
        public byte InterlaceMethod;
        public int NumFrames;                      // acTL num_frames
        public int NumPlays;                       // acTL num_plays（0 表示无限循环）
        public ChunkLocation IhdrChunk;            // IHDR 数据段（13 字节）
        public List<ChunkLocation> AncillaryChunks = []; // PLTE / tRNS / gAMA / cHRM / sRGB / bKGD 等
    }

    /// <summary>
    /// 扫描 APNG 文件的 chunk 表，产出 <see cref="ApngHeader"/> 与逐帧 <see cref="ApngFrameEntry"/> 列表。
    /// 只做单次顺序扫描，随后按记录的 offset/length 惰性读取帧数据。
    /// </summary>
    internal static class ApngChunkReader
    {
        // PNG 文件签名（8 字节）
        private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>读取签名并逐 chunk 扫描，构建 <see cref="ApngHeader"/> 与帧列表。</summary>
        /// <param name="stream">可 Seek 的 PNG/APNG 流，扫描完成后位置不定。</param>
        /// <param name="header">解析出的全局头信息。</param>
        /// <param name="frames">按出现顺序的帧列表；非 APNG（无 acTL）时返回空列表。</param>
        /// <returns>是否为 APNG（包含 acTL 且至少 1 个 fcTL）。</returns>
        public static bool TryScan(Stream stream, out ApngHeader header, out List<ApngFrameEntry> frames)
        {
            header = new ApngHeader();
            frames = new List<ApngFrameEntry>();

            if (!stream.CanSeek)
                return false;

            stream.Position = 0;

            // 1) 校验 PNG 签名
            Span<byte> sig = stackalloc byte[8];
            if (stream.Read(sig) != 8)
                return false;
            for (int i = 0; i < 8; i++)
                if (sig[i] != Signature[i])
                    return false;

            var buf = new byte[8]; // chunk length(4) + type(4)
            // 固定尺寸的 chunk data 缓冲区（提到循环外，避免 stackalloc-in-loop）
            Span<byte> ihdrBuf = stackalloc byte[13];
            Span<byte> actlBuf = stackalloc byte[8];
            Span<byte> fctlBuf = stackalloc byte[26];
            ApngFrameEntry? current = null;
            bool sawActl = false;
            bool sawIdat = false;
            bool firstFrameOwnsIdat = false; // fcTL 是否出现在第一个 IDAT 之前

            while (true)
            {
                if (stream.Read(buf, 0, 8) != 8)
                    break;

                int length = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(0, 4));
                string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
                long dataOffset = stream.Position;

                switch (type)
                {
                    case "IHDR":
                        if (length != 13) return false;
                        if (stream.Read(ihdrBuf) != 13) return false;
                        header.CanvasWidth = BinaryPrimitives.ReadInt32BigEndian(ihdrBuf.Slice(0, 4));
                        header.CanvasHeight = BinaryPrimitives.ReadInt32BigEndian(ihdrBuf.Slice(4, 4));
                        header.BitDepth = ihdrBuf[8];
                        header.ColorType = ihdrBuf[9];
                        header.CompressionMethod = ihdrBuf[10];
                        header.FilterMethod = ihdrBuf[11];
                        header.InterlaceMethod = ihdrBuf[12];
                        header.IhdrChunk = new ChunkLocation(dataOffset, length);
                        break;

                    case "acTL":
                        if (length < 8) return false;
                        if (stream.Read(actlBuf) != 8) return false;
                        header.NumFrames = BinaryPrimitives.ReadInt32BigEndian(actlBuf.Slice(0, 4));
                        header.NumPlays = BinaryPrimitives.ReadInt32BigEndian(actlBuf.Slice(4, 4));
                        // 跳过多余字节（规范固定 8，但容错）
                        if (length > 8) stream.Position += length - 8;
                        sawActl = true;
                        break;

                    case "fcTL":
                        if (length < 26) return false;
                        if (stream.Read(fctlBuf) != 26) return false;
                        // seq[0..4] 我们不校验；只取参数
                        var entry = new ApngFrameEntry
                        {
                            Width = BinaryPrimitives.ReadInt32BigEndian(fctlBuf.Slice(4, 4)),
                            Height = BinaryPrimitives.ReadInt32BigEndian(fctlBuf.Slice(8, 4)),
                            OffsetX = BinaryPrimitives.ReadInt32BigEndian(fctlBuf.Slice(12, 4)),
                            OffsetY = BinaryPrimitives.ReadInt32BigEndian(fctlBuf.Slice(16, 4)),
                            DisposeOp = (ApngDisposeOp)fctlBuf[24],
                            BlendOp = (ApngBlendOp)fctlBuf[25]
                        };
                        ushort delayNum = BinaryPrimitives.ReadUInt16BigEndian(fctlBuf.Slice(20, 2));
                        ushort delayDen = BinaryPrimitives.ReadUInt16BigEndian(fctlBuf.Slice(22, 2));
                        // 规范：delay_den == 0 时按 100 处理
                        if (delayDen == 0) delayDen = 100;
                        entry.Duration = delayNum * 1000.0 / delayDen;
                        frames.Add(entry);
                        current = entry;
                        if (length > 26) stream.Position += length - 26;
                        if (!sawIdat) firstFrameOwnsIdat = true;
                        break;

                    case "IDAT":
                        // PNG 允许将一个 zlib 流拆成多个连续 IDAT chunk（大帧常见）；
                        // 若 IDAT 归属第 0 帧（fcTL(0) 在首个 IDAT 前），所有连续 IDAT 都属于它。
                        if (firstFrameOwnsIdat && current != null)
                        {
                            current.DataChunks.Add(new ChunkLocation(dataOffset, length));
                        }
                        sawIdat = true;
                        stream.Position += length;
                        break;

                    case "fdAT":
                        // fdAT 前 4 字节是 sequence number，真实数据从 offset+4 开始
                        if (current != null && length > 4)
                            current.DataChunks.Add(new ChunkLocation(dataOffset + 4, length - 4));
                        stream.Position += length;
                        break;

                    case "IEND":
                        goto EndLoop;

                    // 全局辅助块（对所有帧都生效，需要在 mini-PNG 里保留）
                    case "PLTE":
                    case "tRNS":
                    case "gAMA":
                    case "cHRM":
                    case "sRGB":
                    case "iCCP":
                    case "sBIT":
                    case "bKGD":
                    case "pHYs":
                        header.AncillaryChunks.Add(new ChunkLocation(dataOffset, length));
                        stream.Position += length;
                        break;

                    default:
                        // 未知或不影响解码的私有块，跳过
                        stream.Position += length;
                        break;
                }

                // 跳过每个 chunk 末尾的 4 字节 CRC
                stream.Position += 4;
            }

        EndLoop:
            return sawActl && frames.Count > 0;
        }

        /// <summary>快速判定：不解析全部 chunk，仅在文件前若干字节中寻找 acTL 签名。</summary>
        /// <remarks>APNG 规范要求 acTL 必须位于第一个 IDAT 之前，绝大多数文件在头部 1KB 内即可命中。</remarks>
        public static bool IsApng(Stream stream, int probeBytes = 65536)
        {
            if (!stream.CanSeek || stream.Length < 8)
                return false;

            var originalPos = stream.Position;
            try
            {
                stream.Position = 0;
                Span<byte> sig = stackalloc byte[8];
                if (stream.Read(sig) != 8) return false;
                for (int i = 0; i < 8; i++)
                    if (sig[i] != Signature[i]) return false;

                int limit = (int)Math.Min(probeBytes, stream.Length - 8);
                var buf = ArrayPool<byte>.Shared.Rent(limit);
                try
                {
                    int read = stream.Read(buf, 0, limit);
                    // 在原始字节流中查找 "acTL" 四字符签名
                    for (int i = 0; i <= read - 4; i++)
                    {
                        if (buf[i] == 0x61 && buf[i + 1] == 0x63 && buf[i + 2] == 0x54 && buf[i + 3] == 0x4C)
                            return true;
                    }
                    return false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            finally
            {
                stream.Position = originalPos;
            }
        }
    }
}
