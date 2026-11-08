// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JinraShaderSync
{
    internal static class ShaderCache
    {
        internal const int Version = 54;
        internal const string FileName = "vulkan.fork-v54.bin";
        private const string Magic = "yuzucach";

        internal struct Entry
        {
            internal long Offset, Length;
            internal string Id;
        }

        internal static void WriteHeader(Stream file)
        {
            using (var writer = new BinaryWriter(file, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(Version);
            }
        }

        internal static List<Entry> Scan(Stream file, bool hasHeader, CancellationToken cancellation)
        {
            file.Position = 0;
            var entries = new List<Entry>();
            using (var reader = new BinaryReader(file, Encoding.ASCII, true))
            using (var sha = SHA256.Create())
            {
                if (hasHeader && (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic ||
                                  reader.ReadInt32() != Version))
                    throw new InvalidDataException("The file is not a Jinra Vulkan v54 cache.");

                while (file.Position < file.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long start = file.Position;
                    uint environments = reader.ReadUInt32();
                    if (environments < 1 || environments > 6)
                        throw new InvalidDataException("Invalid shader environment count.");

                    bool compute = false;
                    for (int i = 0; i < environments; i++)
                    {
                        // GenericEnvironment::Serialize writes six u64 counts and seven u32 fields.
                        ulong code = reader.ReadUInt64();
                        if (code > 64UL * 1024 * 1024 || code % 8 != 0)
                            throw new InvalidDataException("Invalid shader code size.");
                        long bytes = (long)code + MapBytes(reader, 8) + MapBytes(reader, 8) +
                                     MapBytes(reader, 12) + MapBytes(reader, 9) + MapBytes(reader, 12);
                        Skip(file, 6 * sizeof(uint));
                        uint stage = reader.ReadUInt32();
                        if (stage > 8 || (stage == 5 && environments != 1))
                            throw new InvalidDataException("Invalid shader stage.");
                        compute = stage == 5;
                        int headerBytes = compute ? 16 : 80;
                        if (stage == 3)
                            headerBytes += 32;
                        if (stage == 7 || stage == 8)
                            headerBytes += 92;
                        Skip(file, bytes + headerBytes);
                    }

                    int keySize = compute ? 24 : 1056;
                    byte[] key = reader.ReadBytes(keySize);
                    if (key.Length != keySize)
                        throw new EndOfStreamException("Incomplete Vulkan pipeline key.");
                    entries.Add(new Entry {
                        Offset = start, Length = file.Position - start,
                        Id = Hex(sha.ComputeHash(key), 16)
                    });
                }
            }
            return entries;
        }

        internal static string Hex(byte[] bytes, int length)
        {
            return BitConverter.ToString(bytes, 0, length).Replace("-", "").ToLowerInvariant();
        }

        internal static void Copy(Stream source, Stream destination, long length, byte[] buffer,
                                  CancellationToken cancellation)
        {
            while (length > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read == 0)
                    throw new EndOfStreamException("The cache ended during a copy.");
                destination.Write(buffer, 0, read);
                length -= read;
            }
        }

        private static long MapBytes(BinaryReader reader, int recordSize)
        {
            ulong count = reader.ReadUInt64();
            if (count > 1000000)
                throw new InvalidDataException("Invalid shader metadata count.");
            return (long)count * recordSize;
        }

        private static void Skip(Stream file, long bytes)
        {
            if (bytes > file.Length - file.Position)
                throw new EndOfStreamException("Incomplete shader environment.");
            file.Position += bytes;
        }
    }
}

