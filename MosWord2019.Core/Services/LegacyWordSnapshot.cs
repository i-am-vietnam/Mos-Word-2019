using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MosWord2019.Core.Services
{
    // Read-only MS-CFB/MS-DOC reader: no Office automation and no file-format conversion.
    internal sealed class LegacyWordSnapshot
    {
        private readonly byte[] bytes;
        private readonly int sectorSize;
        private readonly List<uint> fat = new List<uint>();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private byte[] miniStream;
        private List<uint> miniFat;
        private const uint End = 0xFFFFFFFE;

        private sealed class Entry { internal uint Start; internal long Size; }
        private static void Require(bool condition) { if (!condition) throw new InvalidDataException("Invalid binary Word structure."); }
        private static uint UInt(byte[] data, int at) { Require(at >= 0 && at <= data.Length - 4); return BitConverter.ToUInt32(data, at); }
        private static ushort UShort(byte[] data, int at) { Require(at >= 0 && at <= data.Length - 2); return BitConverter.ToUInt16(data, at); }
        private byte[] Sector(uint id)
        {
            long at = ((long)id + 1) * sectorSize;
            Require(at >= sectorSize && at + sectorSize <= bytes.Length);
            var result = new byte[sectorSize]; Buffer.BlockCopy(bytes, (int)at, result, 0, result.Length); return result;
        }
        private byte[] Chain(uint start, IList<uint> allocation, bool mini)
        {
            var visited = new HashSet<uint>();
            using (var result = new MemoryStream())
            {
                uint current = start;
                while (current != End)
                {
                    Require(current < allocation.Count && visited.Add(current));
                    if (mini)
                    {
                        long offset = (long)current * 64;
                        Require(miniStream != null && offset + 64 <= miniStream.Length);
                        result.Write(miniStream, (int)offset, 64);
                    }
                    else { byte[] sector = Sector(current); result.Write(sector, 0, sector.Length); }
                    Require(result.Length <= bytes.Length);
                    current = allocation[(int)current];
                }
                return result.ToArray();
            }
        }
        private byte[] Stream(string name)
        {
            Entry entry; Require(entries.TryGetValue(name, out entry));
            Require(entry.Size >= 0 && entry.Size <= bytes.Length);
            var content = Chain(entry.Start, entry.Size < 4096 ? miniFat : fat, entry.Size < 4096);
            Require(entry.Size <= content.Length);
            return content.Take((int)entry.Size).ToArray();
        }
        internal LegacyWordSnapshot(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Require(input.Length >= 512 && input.Length <= 256 * 1024 * 1024);
                bytes = new byte[(int)input.Length];
                int offset = 0, count; while (offset < bytes.Length && (count = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
                Require(offset == bytes.Length);
            }
            Require(bytes.Take(8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }));
            int version = UShort(bytes, 26), shift = UShort(bytes, 30);
            Require(UShort(bytes, 28) == 0xFFFE && ((version == 3 && shift == 9) || (version == 4 && shift == 12)) && UShort(bytes, 32) == 6);
            sectorSize = 1 << shift;
            Require(UInt(bytes, 56) == 4096);
            uint fatCount = UInt(bytes, 44); Require(fatCount > 0 && fatCount <= bytes.Length / sectorSize);
            var sectors = new List<uint>();
            for (int i = 0; i < 109; i++) { uint id = UInt(bytes, 76 + i * 4); if (id != uint.MaxValue) sectors.Add(id); }
            uint next = UInt(bytes, 68), difatCount = UInt(bytes, 72); Require(difatCount <= bytes.Length / sectorSize);
            var seen = new HashSet<uint>();
            for (int i = 0; i < difatCount; i++)
            {
                Require(seen.Add(next)); byte[] sector = Sector(next);
                for (int j = 0; j < sector.Length / 4 - 1; j++) { uint id = UInt(sector, j * 4); if (id != uint.MaxValue) sectors.Add(id); }
                next = UInt(sector, sector.Length - 4);
            }
            Require(sectors.Count == fatCount && sectors.Distinct().Count() == sectors.Count);
            foreach (uint id in sectors) { byte[] sector = Sector(id); for (int i = 0; i < sector.Length; i += 4) fat.Add(UInt(sector, i)); }
            byte[] directory = Chain(UInt(bytes, 48), fat, false);
            Entry root = null; uint rootChild = uint.MaxValue;
            for (int at = 0; at + 128 <= directory.Length; at += 128)
            {
                byte type = directory[at + 66]; if (type != 2 && type != 5) continue;
                int size = UShort(directory, at + 64); Require(size >= 2 && size <= 64 && size % 2 == 0);
                string name = Encoding.Unicode.GetString(directory, at, size - 2);
                long length = version == 3 ? UInt(directory, at + 120) : BitConverter.ToInt64(directory, at + 120);
                var entry = new Entry { Start = UInt(directory, at + 116), Size = length };
                if (type == 5) { Require(root == null); root = entry; rootChild = UInt(directory, at + 76); }
            }
            // Only the root storage's sibling tree defines WordDocument/0Table/1Table.
            // Embedded objects may legitimately reuse stream names in their own storages.
            var pending = new Stack<uint>(); var directorySeen = new HashSet<uint>(); pending.Push(rootChild);
            while (pending.Count > 0)
            {
                uint id = pending.Pop(); if (id == uint.MaxValue) continue;
                Require(id < directory.Length / 128 && directorySeen.Add(id)); int at = (int)id * 128;
                pending.Push(UInt(directory, at + 68)); pending.Push(UInt(directory, at + 72));
                if (directory[at + 66] != 2) continue;
                int nameLength = UShort(directory, at + 64); Require(nameLength >= 2 && nameLength <= 64 && nameLength % 2 == 0);
                string name = Encoding.Unicode.GetString(directory, at, nameLength - 2); Require(!entries.ContainsKey(name));
                entries.Add(name, new Entry { Start = UInt(directory, at + 116), Size = version == 3 ? UInt(directory, at + 120) : BitConverter.ToInt64(directory, at + 120) });
            }
            Require(root != null && root.Size >= 0 && root.Size <= bytes.Length);
            miniStream = root.Size == 0 ? new byte[0] : Chain(root.Start, fat, false).Take((int)root.Size).ToArray();
            byte[] allocationBytes = UInt(bytes, 64) == 0 ? new byte[0] : Chain(UInt(bytes, 60), fat, false);
            miniFat = new List<uint>(); for (int i = 0; i < allocationBytes.Length; i += 4) miniFat.Add(UInt(allocationBytes, i));
        }
        internal string MainText()
        {
            byte[] word = Stream("WordDocument");
            Require(UShort(word, 0) == 0xA5EC && UShort(word, 2) >= 0x00C1);
            ushort flags = UShort(word, 10); Require((flags & 0x8100) == 0); // Encrypted/obfuscated output cannot establish content identity.
            byte[] table = Stream((flags & 0x0200) == 0 ? "0Table" : "1Table");
            int cursor = 32; cursor = checked(cursor + 2 + UShort(word, cursor) * 2);
            int longCount = UShort(word, cursor); cursor += 2; Require(longCount >= 4);
            uint characters = UInt(word, cursor + 12); Require(characters > 0 && characters <= 16 * 1024 * 1024);
            cursor = checked(cursor + longCount * 4);
            int pairs = UShort(word, cursor); cursor += 2; Require(pairs > 33);
            uint clxOffset = UInt(word, cursor + 33 * 8), clxSize = UInt(word, cursor + 33 * 8 + 4);
            Require((long)clxOffset + clxSize <= table.Length && clxSize >= 5);
            int at = (int)clxOffset, end = checked(at + (int)clxSize);
            while (at < end && table[at] == 1) { at = checked(at + 3 + UShort(table, at + 1)); Require(at < end); }
            Require(at + 5 <= end && table[at] == 2);
            uint length = UInt(table, at + 1); at += 5;
            Require(length >= 4 && (length - 4) % 12 == 0 && (long)at + length <= end);
            int pieces = (int)((length - 4) / 12), records = checked(at + (pieces + 1) * 4);
            Require(UInt(table, at) == 0);
            var text = new StringBuilder();
            for (int i = 0; i < pieces; i++)
            {
                uint first = UInt(table, at + i * 4), last = UInt(table, at + (i + 1) * 4);
                Require(last >= first); if (first >= characters) break;
                int count = (int)(Math.Min(last, characters) - first);
                uint position = UInt(table, records + i * 8 + 2);
                bool compressed = (position & 0x40000000) != 0; position &= 0x3FFFFFFF;
                long offset = compressed ? position / 2 : position;
                int byteCount = checked(count * (compressed ? 1 : 2));
                Require(offset + byteCount <= word.Length);
                text.Append((compressed ? Encoding.GetEncoding(1252) : Encoding.Unicode).GetString(word, (int)offset, byteCount));
            }
            Require(text.Length == characters && text[text.Length - 1] == '\r');
            return text.ToString();
        }
    }
}
