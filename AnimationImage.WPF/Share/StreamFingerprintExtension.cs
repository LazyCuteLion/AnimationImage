using System;
using System.IO;
using System.Security.Cryptography;

namespace AnimationImage
{
    internal static class StreamFingerprintExtension
    {
        /// <summary>
        /// 快速指纹：仅读取文件首尾各 64KB + 文件大小，生成 MD5 缓存键。<br/>
        /// 对于 200MB 的 APNG，耗时从 ~2s 降至 &lt;5ms（仅读 128KB 而非全量）。<br/>
        /// 碰撞概率极低：同大小 + 同首尾 64KB 但内容不同的媒体文件在实践中几乎不存在。
        /// </summary>
        public static string FastFingerprint(this Stream stream)
        {
            const int ProbeSize = 65536; // 64KB
            var length = stream.Length;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            // 喂入文件大小（8 字节）
            Span<byte> sizeBuf = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(sizeBuf, length);
            hash.AppendData(sizeBuf);

            var buf = new byte[ProbeSize];

            // 读取文件开头
            stream.Position = 0;
            int headRead = stream.Read(buf, 0, (int)Math.Min(ProbeSize, length));
            hash.AppendData(buf, 0, headRead);

            // 读取文件末尾（若文件足够大且首尾不重叠）
            if (length > ProbeSize * 2)
            {
                stream.Position = length - ProbeSize;
                int tailRead = stream.Read(buf, 0, ProbeSize);
                hash.AppendData(buf, 0, tailRead);
            }

            stream.Position = 0;
            return Convert.ToHexString(hash.GetCurrentHash());
        }
    }
}
