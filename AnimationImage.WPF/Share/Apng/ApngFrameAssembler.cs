using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace AnimationImage.Apng
{
    /// <summary>PNG 标准 CRC-32（多项式 0xEDB88320，位反射版本）。</summary>
    internal static class Crc32Png
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>对连续多段数据计算 CRC32（PNG chunk 覆盖 type + data）。</summary>
        public static uint Compute(ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2 = default)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < part1.Length; i++)
                c = Table[(c ^ part1[i]) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < part2.Length; i++)
                c = Table[(c ^ part2[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>
    /// 将 APNG 中的单个动画帧重新拼装为一张独立标准 PNG 字节流，供 SKCodec 直接解码。
    /// 结构：[signature] + [IHDR(改宽高)] + [全局辅助块] + [IDAT(拼接 fdAT/IDAT 数据)] + [IEND]。
    /// </summary>
    internal sealed class ApngFrameAssembler
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        // IEND 恒定内容 = length(0) + "IEND" + crc(0xAE426082)
        private static readonly byte[] IendChunk = { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

        private readonly Stream _source;
        private readonly byte[] _ihdrData;                     // 原始 IHDR 数据（13 字节）
        private readonly (byte[] Type, byte[] Data)[] _ancillary; // 预加载的全局辅助块

        public ApngFrameAssembler(Stream source, ApngHeader header)
        {
            _source = source;

            // 缓存 IHDR 原始 13 字节
            _ihdrData = new byte[13];
            source.Position = header.IhdrChunk.DataOffset;
            source.ReadExactly(_ihdrData, 0, 13);

            // 缓存全局辅助块（类型 + 数据）
            _ancillary = new (byte[], byte[])[header.AncillaryChunks.Count];
            for (int i = 0; i < header.AncillaryChunks.Count; i++)
            {
                var loc = header.AncillaryChunks[i];
                var data = new byte[loc.Length];
                source.Position = loc.DataOffset;
                source.ReadExactly(data, 0, loc.Length);
                // 类型放在 loc.DataOffset - 4 处（chunk 结构：len(4) + type(4) + data + crc(4)）
                var type = new byte[4];
                source.Position = loc.DataOffset - 4;
                source.ReadExactly(type, 0, 4);
                _ancillary[i] = (type, data);
            }
        }

        /// <summary>
        /// 组装 <paramref name="frame"/> 对应的 mini-PNG。<br/>
        /// 返回的 <paramref name="rented"/> 来自 <see cref="ArrayPool{Byte}.Shared"/>，调用方用完后必须归还。
        /// </summary>
        public bool TryBuild(ApngFrameEntry frame, out byte[] rented, out int length)
        {
            rented = [];
            length = 0;
            if (frame.DataChunks.Count == 0)
                return false;

            // 预估容量：signature + IHDR + ancillaries + IDAT + IEND
            int dataTotal = 0;
            for (int i = 0; i < frame.DataChunks.Count; i++)
                dataTotal += frame.DataChunks[i].Length;

            int ancillaryTotal = 0;
            for (int i = 0; i < _ancillary.Length; i++)
                ancillaryTotal += 12 + _ancillary[i].Data.Length;

            int capacity = 8                    // signature
                         + 12 + 13              // IHDR chunk (length 4 + type 4 + data 13 + crc 4)
                         + ancillaryTotal
                         + 12 + dataTotal       // IDAT chunk（合并所有数据段为单个 IDAT）
                         + IendChunk.Length;

            rented = ArrayPool<byte>.Shared.Rent(capacity);
            var span = rented.AsSpan();
            int p = 0;

            // 1) PNG signature
            Signature.CopyTo(span);
            p += 8;

            // 2) IHDR：复制 13 字节，把 width/height 改成 fcTL 里的子帧尺寸
            Span<byte> ihdrForFrame = stackalloc byte[13];
            _ihdrData.AsSpan().CopyTo(ihdrForFrame);
            BinaryPrimitives.WriteInt32BigEndian(ihdrForFrame.Slice(0, 4), frame.Width);
            BinaryPrimitives.WriteInt32BigEndian(ihdrForFrame.Slice(4, 4), frame.Height);
            p += WriteChunk(span.Slice(p), ChunkTypeIhdr, ihdrForFrame);

            // 3) 全局辅助块（PLTE/tRNS/...）原样写回
            for (int i = 0; i < _ancillary.Length; i++)
            {
                p += WriteChunk(span.Slice(p), _ancillary[i].Type, _ancillary[i].Data);
            }

            // 4) IDAT：从 source 依次读取所有 DataChunks，拼装到一个 IDAT chunk 里
            //    先写 length + type，再流式复制数据到 span，最后回填 CRC
            int idatLenPos = p;
            span[p + 4] = (byte)'I';
            span[p + 5] = (byte)'D';
            span[p + 6] = (byte)'A';
            span[p + 7] = (byte)'T';
            int idatDataStart = p + 8;
            int idatDataEnd = idatDataStart;

            for (int i = 0; i < frame.DataChunks.Count; i++)
            {
                var loc = frame.DataChunks[i];
                _source.Position = loc.DataOffset;
                int remaining = loc.Length;
                while (remaining > 0)
                {
                    int r = _source.Read(rented, idatDataEnd, remaining);
                    if (r <= 0) return FailReturn(rented, out rented, out length);
                    idatDataEnd += r;
                    remaining -= r;
                }
            }

            int idatDataLen = idatDataEnd - idatDataStart;
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(idatLenPos, 4), idatDataLen);
            uint idatCrc = Crc32Png.Compute(span.Slice(idatLenPos + 4, 4), span.Slice(idatDataStart, idatDataLen));
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(idatDataEnd, 4), idatCrc);
            p = idatDataEnd + 4;

            // 5) IEND
            IendChunk.CopyTo(span.Slice(p));
            p += IendChunk.Length;

            length = p;
            return true;
        }

        private static bool FailReturn(byte[] rented, out byte[] outRented, out int outLength)
        {
            ArrayPool<byte>.Shared.Return(rented);
            outRented = [];
            outLength = 0;
            return false;
        }

        /// <summary>写 chunk：length(4) + type(4) + data(N) + crc(4)，返回写入字节数。</summary>
        private static int WriteChunk(Span<byte> dst, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            BinaryPrimitives.WriteInt32BigEndian(dst.Slice(0, 4), data.Length);
            type.CopyTo(dst.Slice(4, 4));
            data.CopyTo(dst.Slice(8, data.Length));
            uint crc = Crc32Png.Compute(dst.Slice(4, 4), data);
            BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(8 + data.Length, 4), crc);
            return 12 + data.Length;
        }

        private static readonly byte[] ChunkTypeIhdr = { 0x49, 0x48, 0x44, 0x52 }; // "IHDR"
    }
}
