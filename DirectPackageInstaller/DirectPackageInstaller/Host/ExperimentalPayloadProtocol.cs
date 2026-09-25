using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace DirectPackageInstaller.Host
{
    /// <summary>
    /// Free/total bytes reported by the experimental payload (cmd 3).
    /// A storage that is missing or unreadable reports 0/0.
    /// </summary>
    public readonly record struct PayloadFreeSpace(ulong InternalFree, ulong InternalTotal, ulong ExtendedFree, ulong ExtendedTotal)
    {
        public bool HasInternal => InternalTotal > 0;
        public bool HasExtended => ExtendedTotal > 0;
    }

    /// <summary>
    /// Byte buffers for the EXPERIMENTAL PS4 payload (Payload/main_experimental.c,
    /// info_experimental.c -> payload_experimental.bin). Pure functions, no networking.
    ///
    /// Every connection the payload opens starts with a u32 command (little endian):
    ///   0 exit, 1 package (legacy, unchanged),
    ///   2 package v2 = cmd 1 fields + i32 storage,
    ///   3 free-space query: the payload replies with 4 x u64 on the same socket and
    ///     closes it, then reconnects for the next request (it does not exit).
    /// Package fields: [u32 len + bytes] URL, Name, ContentID, Type, u64 size, [u32 len + bytes] icon.
    /// The old payload.bin only understands 0 and 1 (it would misread 2/3 as a package).
    /// </summary>
    public static class ExperimentalPayloadProtocol
    {
        public const uint CmdExit = 0;
        public const uint CmdPackage = 1;
        public const uint CmdPackageV2 = 2;
        public const uint CmdFreeSpace = 3;

        /// <summary>Console default storage (same registration path as cmd 1).</summary>
        public const int StorageDefault = -1;

        /// <summary>
        /// The payload switches the console's Application Install Location setting
        /// (registry 0x02880200) while registering the task, then restores it. (The
        /// first try passed the storage as SceBgftDownloadParamEx.slot, which the
        /// console ignores.)
        /// </summary>
        public const bool StorageChoiceWorks = true;
        /// <summary>EXPERIMENTAL: sent to BGFT as SceBgftDownloadParamEx.slot = 0.</summary>
        public const int StorageInternal = 0;
        /// <summary>EXPERIMENTAL: sent to BGFT as SceBgftDownloadParamEx.slot = 1.</summary>
        public const int StorageExtended = 1;

        /// <summary>Payload buffer sizes, including the NUL: byte lengths must be smaller.</summary>
        public const int MaxUrl = 0x800, MaxName = 0x259, MaxId = 0x30, MaxType = 0x10;

        public const int FreeSpaceReplyLength = 32;

        /// <summary>The 4-byte request for cmd 3 (free space).</summary>
        public static byte[] BuildFreeSpaceQuery()
        {
            var Buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(Buffer, CmdFreeSpace);
            return Buffer;
        }

        /// <summary>
        /// Builds a cmd 2 request. Name is truncated to fit; URL, ContentID or Type
        /// that don't fit throw <see cref="ArgumentException"/> (the payload would reject them).
        /// </summary>
        public static byte[] BuildPackageV2(string URL, string? Name, string? ContentID, string Type, long PackageSize, byte[]? Icon, int Storage)
        {
            if (Storage < StorageDefault || Storage > StorageExtended)
                throw new ArgumentOutOfRangeException(nameof(Storage), "Storage must be -1 (default), 0 (internal) or 1 (extended).");
            if (PackageSize < 0)
                throw new ArgumentOutOfRangeException(nameof(PackageSize));

            var UrlData = Encoding.UTF8.GetBytes(URL ?? "");
            var NameData = TruncateUtf8(Name ?? "", MaxName - 1);
            var IDData = Encoding.UTF8.GetBytes(ContentID ?? "");
            var TypeData = Encoding.UTF8.GetBytes(Type ?? "");

            if (UrlData.Length >= MaxUrl)
                throw new ArgumentException($"Package URL is too long for the PS4 payload ({UrlData.Length} bytes, max {MaxUrl - 1}).");
            if (IDData.Length >= MaxId)
                throw new ArgumentException("Content ID is too long for the PS4 payload.");
            if (TypeData.Length >= MaxType)
                throw new ArgumentException("Package type is too long for the PS4 payload.");

            var IconData = Icon ?? Array.Empty<byte>();

            var Buffer = new List<byte>(64 + UrlData.Length + NameData.Length + IDData.Length + TypeData.Length + IconData.Length);
            AddU32(Buffer, CmdPackageV2);
            AddBlob(Buffer, UrlData);
            AddBlob(Buffer, NameData);
            AddBlob(Buffer, IDData);
            AddBlob(Buffer, TypeData);
            AddU64(Buffer, (ulong)PackageSize);
            AddBlob(Buffer, IconData);
            AddU32(Buffer, unchecked((uint)Storage));
            return Buffer.ToArray();
        }

        /// <summary>Parses the 32-byte cmd 3 reply (4 x u64 little endian).</summary>
        public static PayloadFreeSpace ParseFreeSpaceReply(ReadOnlySpan<byte> Reply)
        {
            if (Reply.Length < FreeSpaceReplyLength)
                throw new ArgumentException($"Free space reply must be {FreeSpaceReplyLength} bytes, got {Reply.Length}.", nameof(Reply));

            return new PayloadFreeSpace(
                BinaryPrimitives.ReadUInt64LittleEndian(Reply.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(Reply.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(Reply.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(Reply.Slice(24, 8)));
        }

        /// <summary>UTF-8 bytes of Value cut to at most MaxBytes without splitting a character.</summary>
        public static byte[] TruncateUtf8(string Value, int MaxBytes)
        {
            var Result = Encoding.UTF8.GetBytes(Value);
            if (Result.Length <= MaxBytes)
                return Result;

            int Length = Value.Length;
            while (Length > 0)
            {
                Length--;
                if (Length > 0 && char.IsHighSurrogate(Value[Length - 1]))
                    Length--; // keep surrogate pairs together
                if (Encoding.UTF8.GetByteCount(Value.AsSpan(0, Length)) <= MaxBytes)
                    break;
            }
            return Encoding.UTF8.GetBytes(Value.Substring(0, Length));
        }

        private static void AddU32(List<byte> Buffer, uint Value)
        {
            Span<byte> Tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(Tmp, Value);
            Buffer.AddRange(Tmp.ToArray());
        }

        private static void AddU64(List<byte> Buffer, ulong Value)
        {
            Span<byte> Tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(Tmp, Value);
            Buffer.AddRange(Tmp.ToArray());
        }

        private static void AddBlob(List<byte> Buffer, byte[] Data)
        {
            AddU32(Buffer, (uint)Data.Length);
            Buffer.AddRange(Data);
        }
    }
}
