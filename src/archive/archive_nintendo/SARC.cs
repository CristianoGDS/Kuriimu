using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cetera.Hash;
using Kontract.Interface;
using Kontract;
using Kontract.IO;

namespace archive_nintendo.SARC
{
    public sealed class SARC
    {
        public List<SarcArchiveFileInfo> Files;
        Stream _stream = null;

        ByteOrder byteOrder;
        public uint hashMultiplier;
        bool usesSFNT;

        public SARC(Stream input)
        {
            _stream = input;
            using (var br = new BinaryReaderX(input, true))
            {
                //Get ByteOrder
                br.BaseStream.Position = 0x6;
                byteOrder = (ByteOrder)br.ReadUInt16();
                br.BaseStream.Position = 0;
                br.ByteOrder = byteOrder;

                //Header
                var sarcHeader = br.ReadStruct<SARCHeader>();

                //FAT
                var sfatHeader = br.ReadStruct<SFATHeader>();
                var sfatEntries = br.ReadMultiple<SFATEntry>(sfatHeader.nodeCount).ToList();
                hashMultiplier = (uint)sfatHeader.hashMultiplier;

                //FNT
                var sfntHeader = br.ReadStruct<SFNTHeader>();
                var sfntOffset = br.BaseStream.Position;

                //is FNT used?
                usesSFNT = sfatEntries.Any(entry => (entry.SFNTOffsetFlag >> 16) == 0x100);

                Files = sfatEntries.Select(entry =>
                {
                    Support.extensions.TryGetValue(br.PeekString((uint)(sarcHeader.dataOffset + entry.dataStart), 4), out var getExt);
                    if (getExt == null) Support.extensions.TryGetValue(br.PeekString((uint)(sarcHeader.dataOffset + entry.dataEnd - 0x28), 4), out getExt);

                    br.BaseStream.Position = ((entry.SFNTOffsetFlag & 0xffff) << 2) + sfntOffset;
                    var filename = usesSFNT ? br.ReadCStringA() : $"0x{entry.nameHash:X8}" + ((getExt == null) ? ".bin" : getExt);
                    return new SarcArchiveFileInfo
                    {
                        FileName = filename,
                        FileData = new SubStream(input, sarcHeader.dataOffset + entry.dataStart, entry.dataEnd - entry.dataStart),
                        State = ArchiveFileState.Archived,
                        hash = entry.nameHash
                    };
                }).ToList();
            }
        }

        public void Save(Stream output, bool leaveOpen = false)
        {
            using (var bw = new BinaryWriterX(output, leaveOpen, byteOrder))
            {
                //Create SARCHeader
                var header = new SARCHeader
                {
                    byteOrder = byteOrder,
                    dataOffset = Files.Aggregate(
                        0x14 + 0xc + 0x8 + Files.Sum(afi => usesSFNT ? ((afi.FileName.Length + 4) & ~3) + 0x10 : 0x10),
                        (n, file) => Support.Pad(n, file.FileName, (byteOrder == ByteOrder.LittleEndian) ? System.CTR : System.WiiU))
                };

                //SFATHeader
                bw.BaseStream.Position = 0x14;
                bw.WriteStruct(new SFATHeader
                {
                    hashMultiplier = (int)hashMultiplier,
                    nodeCount = (short)Files.Count
                });

                //SFAT List + nameList
                int nameOffset = 0;
                int dataOffset = 0;
                foreach (var afi in Files)
                {
                    dataOffset = Support.Pad(dataOffset, afi.FileName, (byteOrder == ByteOrder.LittleEndian) ? System.CTR : System.WiiU);
                    var fileLen = (int)afi.FileData.Length;

                    var sfatEntry = new SFATEntry
                    {
                        nameHash = usesSFNT ? SimpleHash.Create(afi.FileName, hashMultiplier) : Convert.ToUInt32(afi.FileName.Substring(2, 8), 16),
                        SFNTOffsetFlag = (uint)(((usesSFNT ? 0x100 : 0) << 16) | (usesSFNT ? nameOffset / 4 : 0)),
                        dataStart = dataOffset,
                        dataEnd = dataOffset + fileLen
                    };
                    bw.WriteStruct(sfatEntry);
                    nameOffset = (nameOffset + afi.FileName.Length + 4) & ~3;
                    dataOffset = sfatEntry.dataEnd;
                }

                //SFNT
                bw.WriteStruct(new SFNTHeader());
                if (usesSFNT)
                    foreach (var afi in Files)
                    {
                        bw.WriteASCII(afi.FileName + "\0");
                        bw.BaseStream.Position = (bw.BaseStream.Position + 3) & ~3;
                    }

                //FileData
                bw.WriteAlignment(header.dataOffset);
                foreach (var afi in Files)
                {
                    bw.WriteAlignment(Support.Pad((int)bw.BaseStream.Length, afi.FileName, (byteOrder == ByteOrder.LittleEndian) ? System.CTR : System.WiiU));    //(unusual) padding scheme through filenames
                    afi.FileData.CopyTo(bw.BaseStream);
                }

                bw.BaseStream.Position = 0;
                header.fileSize = (int)bw.BaseStream.Length;
                bw.WriteStruct(header);
            }
        }

        public void Close()
        {
            _stream?.Close();
            _stream = null;
        }
    }
}
