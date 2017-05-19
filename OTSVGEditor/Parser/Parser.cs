// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Windows.Storage;

namespace OTParser
{
    // Class heavily used by FontModel to parse and edit the font file.
    // Parses OpenType font files in binary format. See https://www.microsoft.com/typography/otspec/otff.htm#otttables
    // for documentation on the file structure.
    public class Parser
    {
        private const int TagLength = 4;

        // Cannot use autoimplemented properties because C# does not support exposing the backing fields for ref parameters.

        // Start position to read.
        private uint start;
        public uint Start
        {
            get { return start; }
            set { start = value; }
        }

        // Current reading position.
        private uint current; 
        public uint Current
        {
            get { return current; }
            set { current = value; }
        }

        // Parses font's offset table and alters OffsetTable object appropriately.
        public OffsetTable ParseOffsetTable(byte[] fontContent)
        {
            OffsetTable offsetTable = new OffsetTable();

            uint offset = 0;
            offsetTable.tag =           ReadDataAndIncreaseIndex(fontContent, ref offset, TagLength);
            offsetTable.numTables =     ReadDataAndIncreaseIndex<ushort>(fontContent, ref offset, sizeof(ushort));
            offsetTable.searchRange =   ReadDataAndIncreaseIndex<ushort>(fontContent, ref offset, sizeof(ushort));
            offsetTable.entrySelector = ReadDataAndIncreaseIndex<ushort>(fontContent, ref offset, sizeof(ushort));
            offsetTable.rangeShift =    ReadDataAndIncreaseIndex<ushort>(fontContent, ref offset, sizeof(ushort));
            start = offset;

            return offsetTable;
        }

        // Parses font's table record and alters TableRecord object appropriately.
        // Also keeps track of the location of the offset for that table record.
        public TableRecord ReadTableRecord(byte[] source)
        {
            TableRecord record = new TableRecord();
            record.tagInt =   ReadData<uint>(source, ref start, sizeof(uint));
            record.tag =      ReadDataAndIncreaseIndex(source, ref start, TagLength);
            record.checksum = ReadDataAndIncreaseIndex<uint>(source, ref start, sizeof(uint));
            // Not a part of table records---just saved and used in ChangeTableRecOffsets
            // to change TableRecordOffsets easily.
            record.offsetOfOffset = start;
            record.offset = ReadDataAndIncreaseIndex<uint>(source, ref start, sizeof(uint));
            record.length = ReadDataAndIncreaseIndex<uint>(source, ref start, sizeof(uint));
            return record;
        }

        // Updates OffsetTable object and the offset table in font to reflect a table being added.
        public OffsetTable UpdateOffsetTable(OffsetTable offsetTable, ref byte[] source)
        {
            OffsetTable newOffsetTable = offsetTable;
            newOffsetTable.numTables += 1;
            byte[] newNumTables = GetBytesBigEndian(offsetTable.numTables);
            int offsetTableNumTablesOffset = TagLength; // Advance past the initial tag of the offset table.
            Array.Copy(newNumTables, 0, source, offsetTableNumTablesOffset, sizeof(ushort)); // Overwrite numTables with numTables + 1.
            
            // Calculate searchRange, entrySelector and rangeShift as outlined in the Offset Table section of the OpenType spec.
            ushort searchRange = 1, entrySelector = 0;
            while ((searchRange << 1) < offsetTable.numTables)
            {
                searchRange <<= 1;
                entrySelector++;
            }
            searchRange <<= 4; // multiply by 16
            newOffsetTable.searchRange = searchRange;
            newOffsetTable.entrySelector = entrySelector;
            newOffsetTable.rangeShift = (ushort)((offsetTable.numTables * 16) - offsetTable.searchRange);
            byte[] newSearchRange = GetBytesBigEndian(offsetTable.searchRange);
            byte[] newEntrySelector = GetBytesBigEndian(offsetTable.entrySelector);
            byte[] newRangeShift = GetBytesBigEndian(offsetTable.rangeShift);

            // Write the newly calculated values of searchRange, entrySelector and rangeShift to source.
            int sourceIndex = sizeof(uint) + sizeof(ushort);
            Array.Copy(newSearchRange, 0, source, sourceIndex, sizeof(ushort));
            sourceIndex += sizeof(ushort);
            Array.Copy(newEntrySelector, 0, source, sourceIndex, sizeof(ushort));
            sourceIndex += sizeof(ushort);
            Array.Copy(newRangeShift, 0, source, sourceIndex, sizeof(ushort));
            return newOffsetTable;
        }

        // Removes SVG of the specified glyph ID from the SVG table of the font.
        public void RemoveSvgContent(ref TableRecord record, ref byte[] source, ushort glyphID, FontModel fontModel)
        {
            start = record.offset; // Start always refers to the offset to the file head.
            current = record.offset;

            // Read SVG main header.
            ushort version =         ReadDataAndIncreaseIndex<ushort>(source, ref current, sizeof(ushort));
            uint svgDocIndexOffset = ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));
            uint reserved =          ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));

            // Read document index.
            uint documentIndexOffset = start + svgDocIndexOffset;
            uint svgDocIndexAbsoluteOffset = start + svgDocIndexOffset;
            ushort numEntries = ReadDataAndIncreaseIndex<ushort>(source, ref documentIndexOffset, sizeof(ushort));

            uint currPadding = CalculatePadding(record.length);

            SVGDocIdxEntry[] docIdxEntries = new SVGDocIdxEntry[numEntries];
            for (int i = 0; i < docIdxEntries.Length; i++)
            {
                docIdxEntries[i] = ReadSvgDocIdxEntry(source, ref documentIndexOffset);
                // Checks that SVG Document Index Entry matches the one to remove.
                // Note: Currently only supports one-to-one SVG/glyph ID mapping.
                if (docIdxEntries[i].startID.Equals(glyphID))
                {
                    int svgLength = (int)docIdxEntries[i].docLength;

                    // diff calculates the change in length of the font file because of removing the SVG.
                    // diff = length of the SVG content to remove + length of the SVG document index entry
                    long diff = svgLength + Marshal.SizeOf<SVGDocIdxEntry>();

                    uint svgOffset = docIdxEntries[i].docOffset + record.offset + svgDocIndexOffset; // Offset to the beginning of the SVG content

                    // Edits the SVG table length stored in the table record for SVG to the current length - diff
                    byte[] recordLength = GetBytesBigEndian((uint)(record.length - diff));
                    Array.Copy(recordLength, 0, source, (int)record.offsetOfOffset + sizeof(uint), sizeof(uint));

                    uint newPadding = CalculatePadding((uint)(record.length - diff));
                    int paddingDiff = (int)currPadding - (int)newPadding;
                    // Change the table record offsets for tables following the removed SVG content
                    ChangeTableRecOffsets(fontModel.TableRecords, ref source, (diff + paddingDiff), start + svgDocIndexOffset);
                    // Changes all of the SVG offsets in the SVG Document Index Entries to reflect an upward shift of 12 as caused by the removal of
                    // the specified SVG Document Index Entry
                    ChangeSvgOffsets(fontModel.TableRecords, ref source, diff - svgLength, 0);
                    // Changes the SVG offsets in the SVG Document Index Entries for the SVG content that was written after the removed SVG content in the 
                    // font file to reflect an upward shift of the length of the removed SVG content
                    ChangeSvgOffsets(fontModel.TableRecords, ref source, diff - Marshal.SizeOf<SVGDocIdxEntry>(), docIdxEntries[i].docOffset);

                    uint numEntriesOffset = start + svgDocIndexOffset;

                    // Decrements the number specifying the number of SVG entries and edits font appropriately
                    byte[] newNumEntries = GetBytesBigEndian((ushort)(numEntries - 1));
                    Array.Copy(newNumEntries, 0, source, (int)numEntriesOffset, sizeof(ushort));

                    // Creates byte array for the corrected font and copies over font while removing SVG content and
                    // changing the padding of the SVG table appropriately

                    byte[] newSource = new byte[source.Length - (diff + paddingDiff)];

                    // Copies from the beginning of source to the end of the SVG Document Index Entry preceeding the one to be removed
                    int sourceIndex = 0;
                    int newSourceIndex = 0;
                    int length = (int)(documentIndexOffset - Marshal.SizeOf<SVGDocIdxEntry>());
                    Array.Copy(source, sourceIndex, newSource, newSourceIndex, length);

                    // Skips the DocIdxEntry to remove and copies from the beginning of the SVG DocIdxEntry following the removed one to the end of the SVG content preceeding the removed SVG content
                    sourceIndex    += (int)(documentIndexOffset);
                    newSourceIndex += length;
                    length          = (int)(svgDocIndexAbsoluteOffset + docIdxEntries[i].docOffset - documentIndexOffset);
                    Array.Copy(source, sourceIndex, newSource, newSourceIndex, length);

                    // Copies from the beginning of the SVG content following the SVG content to remove until the end of the SVG table
                    // Stops copying at this point in order to make sure the padding at the end of the SVG table is correct
                    sourceIndex    += length + (int)docIdxEntries[i].docLength;
                    newSourceIndex += length;
                    length          = (int)(record.offset + record.length - (svgDocIndexAbsoluteOffset + docIdxEntries[i].docOffset + docIdxEntries[i].docLength));
                    Array.Copy(source, sourceIndex, newSource, newSourceIndex, length);

                    // Copies from the beginning of the table following the SVG Table (after the entire SVG table and its padding) to the end of the font file
                    sourceIndex    = (int)(record.offset + record.length + currPadding);
                    newSourceIndex = (int)(record.offset + record.length - docIdxEntries[i].docLength - Marshal.SizeOf<SVGDocIdxEntry>() + newPadding);
                    length         = (int)(source.Length - (record.offset + record.length + currPadding));
                    Array.Copy(source, sourceIndex, newSource, newSourceIndex, length);

                    // Set the new source.
                    source = newSource;

                    // Alters the TableRecord object to reflect the new length of the table
                    record.length = (uint)(record.length - diff);

                    // Edits checksum of SVG table
                    byte[] checksum = GetBytesBigEndian(CalcTableChecksum(record.offset, record.length, ref source));
                    Array.Copy(checksum, 0, source, (int)(record.offsetOfOffset - sizeof(uint)), sizeof(uint));
                    record.checksum = BitConverter.ToUInt32(checksum, 0);
                    return;
                }
            }
        }

        // Adds the SVG defined by svgContent to the font file at the specified glyphID
        public void AssembleSvgContent(ref TableRecord record, ref byte[] source, byte[] svgContent, ushort glyphID, FontModel fontModel)
        {
            start = record.offset; // start always refer to the offset to the file head.
            current = record.offset;

            // Read SVG Main Header
            ushort version =         ReadDataAndIncreaseIndex<ushort>(source, ref current, sizeof(ushort));
            uint svgDocIndexOffset = ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));
            uint reserved =          ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));

            // Read document Index
            uint documentIndexOffset = start + svgDocIndexOffset;
            uint svgDocIndexAbsoluteOffset = start + svgDocIndexOffset;
            ushort numEntries = ReadDataAndIncreaseIndex<ushort>(source, ref documentIndexOffset, sizeof(ushort));

            int svgLength;
            int paddingDiff = 0;
            long diff;
            byte[] recordLength;
            byte[] newSource;
            byte[] checksum;
            uint newDocumentIndexOffset = 0;
            uint newPadding = 0;
            uint currPadding = CalculatePadding(record.length);
            bool firstPass = true;

            SVGDocIdxEntry[] docIdxEntries = new SVGDocIdxEntry[numEntries];
            for (int i = 0; i < docIdxEntries.Length; i++)
            {
                docIdxEntries[i] = ReadSvgDocIdxEntry(source, ref documentIndexOffset);
                // Case 0: takes into account the glyphID already having SVG content
                if (docIdxEntries[i].startID.Equals(glyphID))
                {
                    svgLength = svgContent.Length;
                    diff = docIdxEntries[i].docLength - svgLength;
                    uint svgOffset = docIdxEntries[i].docOffset + record.offset + svgDocIndexOffset;
                    recordLength = GetBytesBigEndian((uint)(record.length - diff));
                    // Changes the SVG table record length by the difference between the length of the old SVG content and the new SVG content
                    Array.Copy(recordLength, 0, source, (int)record.offsetOfOffset + sizeof(uint), sizeof(uint));
                    newPadding = CalculatePadding((uint)(record.length - diff));
                    paddingDiff = (int)currPadding - (int)newPadding;
                    byte[] length = GetBytesBigEndian((uint)svgLength);
                    // Changes the length of the SVG content contained in the SVG Document Index Entry to reflect the length of the new SVG content
                    Array.Copy(length, 0, source, (int)(documentIndexOffset - sizeof(uint)), sizeof(uint));
                    // Change the table record offsets for tables following the altered SVG content to reflect the change in length
                    ChangeTableRecOffsets(fontModel.TableRecords, ref source, (diff + paddingDiff), start + svgDocIndexOffset);
                    // Changes the SVG offsets in the SVG Document Index Entries for the SVG content that was written after the altered SVG content in the 
                    // font file to reflect a shift of the difference in length between the old and new SVG content
                    ChangeSvgOffsets(fontModel.TableRecords, ref source, diff, docIdxEntries[i].docOffset);
                    newSource = new byte[source.Length - (diff + paddingDiff)];
                    // Copies from the beginning of source to the beginning of the altered SVG content
                    Array.Copy(source, 0, newSource, 0, (int)svgOffset);
                    // Skips the length of the new SVG content in the newSource array and copies from the beginning of the SVG content following the altered SVG content
                    // to the end of the SVG Table
                    Array.Copy(source, (int)(svgOffset + docIdxEntries[i].docLength), newSource, (int)svgOffset + svgLength, (int)(record.offset + record.length - svgOffset - docIdxEntries[i].docLength));
                    // Makes sure the padding at the end of the SVG table in the newSource array is correct and copies from the beginning o
                    Array.Copy(source, (int)(record.offset + record.length + currPadding), newSource, (int)(svgLength + record.offset + record.length - docIdxEntries[i].docLength + newPadding), (int)(source.Length - (record.offset + record.length + currPadding)));
                    // Copies from the beginning of the table following the SVG Table (after the entire SVG table and it's padding) to the end of the font file
                    Array.Copy(svgContent, 0, newSource, (int)svgOffset, svgLength);
                    source = newSource;
                    record.length = (uint)(record.length - diff);
                    checksum = GetBytesBigEndian(CalcTableChecksum(record.offset, record.length, ref source));
                    Array.Copy(checksum, 0, source, (int)(record.offsetOfOffset - sizeof(uint)), sizeof(uint));
                    record.checksum = BitConverter.ToUInt32(checksum, 0);
                    return;
                }
                // Case 1: takes into account the glyphID not having SVG content and not being the last glyphID
                else if (docIdxEntries[i].startID > glyphID && firstPass)
                {
                    // newDocumentIndexOffset stores the location that the new SVG Document Index Entry would have to be added
                    newDocumentIndexOffset = documentIndexOffset - (uint)Marshal.SizeOf<SVGDocIdxEntry>();
                    firstPass = false;
                    break;
                }
            }
            // Case 2: takes into account the glyphID not having SVG content and being the last glyphID
            if (firstPass)
            {
                // newDocumentIndexOffset stores the location that the new SVG Document Index Entry would have to be added
                // In this case right after the last SVG Document Index Entry
                newDocumentIndexOffset = documentIndexOffset;
            }
            svgLength = svgContent.Length;
            // In cases 1 and 2 an entirely new SVG Document Index Entry must be added, so diff is the length of the SVG content plus the length of an 
            // SVG Document Index Entry
            // This is made negative because for consistency's sake diff is always subtracted
            diff = (svgLength + Marshal.SizeOf<SVGDocIdxEntry>()) * -1;
            recordLength = GetBytesBigEndian((uint)(record.length - diff));
            // Changes the SVG table record length by diff
            Array.Copy(recordLength, 0, source, (int)record.offsetOfOffset + sizeof(uint), sizeof(uint));
            newPadding = CalculatePadding((uint)(record.length - diff));
            paddingDiff = (int)currPadding - (int)newPadding;
            // Change the table record offsets for tables following the added SVG content to reflect the change in length
            ChangeTableRecOffsets(fontModel.TableRecords, ref source, (diff + paddingDiff), start + svgDocIndexOffset);
            // Brand new SVG content is being added to the very end of the SVG table, so the offsets contained in the SVG
            // Document Index Entries need only be changed by the length of the new SVG Document Index Entry that will be addded
            ChangeSvgOffsets(fontModel.TableRecords, ref source, diff + svgLength, 0);
            uint numEntriesOffset = start + svgDocIndexOffset;
            byte[] newNumEntries = GetBytesBigEndian((ushort)(numEntries + 1));
            // Increments the number of entries contained in the SVG document index table
            Array.Copy(newNumEntries, 0, source, (int)numEntriesOffset, sizeof(ushort));
            newSource = new byte[source.Length - (diff + paddingDiff)];
            // Copies from the begining of source to the end of the SVG Document Index Entry preceeding the one to be added
            Array.Copy(source, 0, newSource, 0, (int)newDocumentIndexOffset);
            // Assumes the start and end glyphIDs are the same because currently you can only add one SVG at a time
            byte[] startAndEndGlyphID = GetBytesBigEndian(glyphID);
            int newSourceCopyOffset = (int)newDocumentIndexOffset;
            // Copies the startGlyphID to the newSource array
            Array.Copy(startAndEndGlyphID, 0, newSource, newSourceCopyOffset, sizeof(ushort));
            newSourceCopyOffset += sizeof(ushort);
            // Copies the endGlyphID to the newSource array
            Array.Copy(startAndEndGlyphID, 0, newSource, newSourceCopyOffset, sizeof(ushort));
            newSourceCopyOffset += sizeof(ushort);
            byte[] svgDocOffset = GetBytesBigEndian((uint)(record.length - svgDocIndexOffset + Marshal.SizeOf<SVGDocIdxEntry>()));
            // Copies the svgDocOffset to the newSource array
            Array.Copy(svgDocOffset, 0, newSource, newSourceCopyOffset, sizeof(uint));
            newSourceCopyOffset += sizeof(uint);
            byte[] svgDocLength = GetBytesBigEndian((uint)svgLength);
            // Copies the svgDocLength to the newSource array
            Array.Copy(svgDocLength, 0, newSource, newSourceCopyOffset, sizeof(uint));
            newSourceCopyOffset += sizeof(uint);
            // Copies from the begining of the SVG Document Index Entry following the one added to the end of the SVG Table in source
            Array.Copy(source, (int)newDocumentIndexOffset, newSource, newSourceCopyOffset, (int)(record.length - svgDocIndexOffset + svgDocIndexAbsoluteOffset - newDocumentIndexOffset));
            newSourceCopyOffset += (int)(record.length - svgDocIndexOffset + svgDocIndexAbsoluteOffset - newDocumentIndexOffset);
            // Copies the new SVG content to newSource 
            Array.Copy(svgContent, 0, newSource, newSourceCopyOffset, svgLength);
            // Makes sure the padding on the SVG Table remains correct
            newSourceCopyOffset += (int)(svgLength + newPadding);
            // Copies from the beginning of the table following the SVG table to the end of the file
            Array.Copy(source, (int)svgDocIndexAbsoluteOffset + (int)(record.length - svgDocIndexOffset + currPadding), newSource, newSourceCopyOffset, source.Length - ((int)svgDocIndexAbsoluteOffset + (int)(record.length - svgDocIndexOffset + currPadding)));
            source = newSource;
            record.length = (uint)(record.length - diff);
            checksum = GetBytesBigEndian(CalcTableChecksum(record.offset, record.length, ref source));
            Array.Copy(checksum, 0, source, (int)(record.offsetOfOffset - sizeof(uint)), sizeof(uint));
            record.checksum = BitConverter.ToUInt32(checksum, 0);
        }

        // Edits the font to reflect shifts of length diff of some of the font tables that have offsets that  
        // point to locations after svgTableOffset
        public void ChangeTableRecOffsets(TableRecord[] tableRecords, ref byte[] source, long diff, uint svgTableOffset)
        {
            for (int i = 0; i < tableRecords.Length; i++)
            {
                if (tableRecords[i].offset > svgTableOffset)
                {
                    byte[] newOffset = GetBytesBigEndian((uint)(tableRecords[i].offset - diff));
                    Array.Copy(newOffset, 0, source, (int)tableRecords[i].offsetOfOffset, sizeof(uint));
                }
            }
        }

        // Loops through the SVG table to find SVG assets listed at offsets past svgOffsetfromSVGTab
        // If an SVG is found after that location, diff is subtracted from it's offset and the font file is changed accordingly
        private void ChangeSvgOffsets(TableRecord[] tableRecords, ref byte[] source, long diff, uint svgOffsetfromSVGTab)
        {
            foreach (TableRecord record in tableRecords)
            {
                if (record.tag == "SVG ")
                {
                    start = record.offset;
                    current = record.offset;

                    ushort version =         ReadDataAndIncreaseIndex<ushort>(source, ref current, sizeof(ushort));
                    uint svgDocIndexOffset = ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));
                    uint reserved =          ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));

                    uint documentIndexOffset = start + svgDocIndexOffset;
                    ushort numEntries = ReadDataAndIncreaseIndex<ushort>(source, ref documentIndexOffset, sizeof(ushort));

                    SVGDocIdxEntry[] docIdxEntries = new SVGDocIdxEntry[numEntries];
                    for (int j = 0; j < docIdxEntries.Length; j++)
                    {
                        docIdxEntries[j] = ReadSvgDocIdxEntry(source, ref documentIndexOffset);
                        if (docIdxEntries[j].docOffset > svgOffsetfromSVGTab)
                        {
                            byte[] newSvgOffset = GetBytesBigEndian((uint)(docIdxEntries[j].docOffset - diff));
                            Array.Copy(newSvgOffset, 0, source, (int)documentIndexOffset - (2 * sizeof(uint)), sizeof(uint));
                        }
                    }
                }
            }
        }

        // Reads through the Name Record Table and saves each of the Name Records in an array.
        // Loops through the nameRecord array to find a record with a nameID == 1 and a length > 0.
        // If such a nameRecord is found, the family name is read and returned. If no such
        // nameRecord is found, returns null.
        public string GetFamilyName(TableRecord record, byte[] source)
        {
            start = record.offset;
            current = record.offset;

            ushort format =       ReadDataAndIncreaseIndex<ushort>(source,  ref current, sizeof(ushort));
            ushort count =        ReadDataAndIncreaseIndex<ushort>(source,  ref current, sizeof(ushort));
            ushort stringOffset = ReadDataAndIncreaseIndex<ushort>(source,  ref current, sizeof(ushort));

            NameRecord[] nameRecords = new NameRecord[count];
            for (int i = 0; i < nameRecords.Length; i++)
            {
                nameRecords[i] = ReadNameRecordEntry(source, ref current);
            }
            for (int i = 0; i < nameRecords.Length; i++)
            {
                if (nameRecords[i].nameID == 1 && nameRecords[i].length > 0)
                {
                    uint index = current + nameRecords[i].offset;
                    return ReadDataAndIncreaseIndexForFamilyName(source, ref index, nameRecords[i].length);
                }
            }
            return null;
        }

        // Appropriately recalculates and returns the checksum of any given table
        private uint CalcTableChecksum(uint table, uint length, ref byte[] source)
        {
            Debug.Assert(length != 0);
            uint sum = 0;
            ulong nLongs = (length + 3) / 4;
            uint offset = table;
            for (uint i = 0; i < nLongs; i++)
            {
                uint startIndex = 0;
                byte[] data = new byte[4];
                if (offset + 4 < source.Length)
                {
                    Array.Copy(source, (int)offset, data, 0, 4);
                    Array.Reverse(data);
                    startIndex = BitConverter.ToUInt32(data, 0);
                    offset += 4;
                    sum += startIndex;
                }
            }
            return sum;
        }

        // Parses the format 0 cmap table
        // Refer to https://www.microsoft.com/typography/otspec/cmap.htm to learn about how this format is organized
        private void ParseFormat0(byte[] source, ref uint offset, List<GlyphModel> allChars)
        {
            ushort formatID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            Debug.Assert(formatID == 0);

            ushort length =   ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            ushort language = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));

            byte[] idArray = ReadByteArrayAndIncreaseIndex(256, source, ref offset, sizeof(byte) * 256);
            for (uint i = 0; i < idArray.Length; i++)
            {
                if (idArray[i] != 0)
                {
                    GlyphModel newChar = new GlyphModel();
                    newChar.CodePoint = i;
                    newChar.GlyphID = idArray[i];
                    byte[] byteI = BitConverter.GetBytes(i);
                    newChar.Definition = Encoding.UTF32.GetString(byteI);
                    if (!allChars.Contains(newChar))
                    {
                        allChars.Add(newChar);
                    }
                }
            }
        }

        // Reads through the header information of the cmap format 4 table
        private CmapFormat4Header ParseCmapFormat4Header(byte[] source, ref uint offset)
        {
            CmapFormat4Header header = new CmapFormat4Header();

            header.format =        ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            Debug.Assert(header.format == 4);

            header.length =        ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.language =      ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.segCountX2 =    ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.searchRange =   ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.entrySelector = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.rangeShift =    ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));

            return header;
        }

        // Parses the format 4 cmap table
        // Refer to https://www.microsoft.com/typography/otspec/cmap.htm to learn about how this format is organized
        private void ParseFormat4(byte[] source, ref uint offset, List<GlyphModel> allChars)
        {
            CmapFormat4Header header = ParseCmapFormat4Header(source, ref offset);

            int segCount = (int)header.segCountX2 / 2;

            ushort[] endCountArray   = ReadUshortArrayAndIncreaseIndex(segCount, source, ref offset, sizeof(ushort));
            ushort reservedPad       = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            ushort[] startCountArray = ReadUshortArrayAndIncreaseIndex(segCount, source, ref offset, sizeof(ushort));
            short[] idDeltaArray     = ReadShortArrayAndIncreaseIndex(segCount, source, ref offset, sizeof(short));
            uint idRangeTableOffset  = offset;
            ushort[] idRangeTable    = ReadUshortArrayAndIncreaseIndex(segCount, source, ref offset, sizeof(ushort));

            for (byte i = 0; i < startCountArray.Length - 1; i++)
            {
                if (idRangeTable[i] == 0)
                {
                    for (uint j = startCountArray[i]; j <= endCountArray[i]; j++)
                    {
                        GlyphModel newChar = new GlyphModel();
                        newChar.CodePoint = j;
                        newChar.GlyphID = (ushort)(j + idDeltaArray[i]);
                        byte[] byteJ = BitConverter.GetBytes(j);
                        newChar.Definition = Encoding.UTF32.GetString(byteJ);
                        if (!allChars.Contains(newChar))
                        {
                            allChars.Add(newChar);
                        }
                    }
                }
                else
                {
                    uint glyphIdOffset = idRangeTableOffset + idRangeTable[i] + (uint)(i * 2);
                    for (uint j = startCountArray[i]; j <= endCountArray[i]; j++)
                    {
                        ushort gID = ReadDataAndIncreaseIndex<ushort>(source, ref glyphIdOffset, sizeof(ushort));
                        GlyphModel newChar = new GlyphModel();
                        newChar.CodePoint = j;
                        newChar.GlyphID = gID;
                        byte[] byteJ = BitConverter.GetBytes(j);
                        newChar.Definition = Encoding.UTF32.GetString(byteJ);
                        if (!allChars.Contains(newChar))
                        {
                            allChars.Add(newChar);
                        }
                    }
                }
            }
        }

        // Reads through the header information of the cmap format 6 table
        private CmapFormat6Header ParseCmapFormat6Header(byte[] source, ref uint offset)
        {
            CmapFormat6Header header = new CmapFormat6Header();

            header.format =     ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            Debug.Assert(header.format == 6);

            header.length =     ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.language =   ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.firstCode =  ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.entryCount = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));

            return header;
        }

        // Parses the format 6 cmap table
        // Refer to https://www.microsoft.com/typography/otspec/cmap.htm to learn about how this format is organized
        private void ParseFormat6(byte[] source, ref uint offset, List<GlyphModel> allChars)
        {
            CmapFormat6Header header = ParseCmapFormat6Header(source, ref offset);

            for (uint i = 0; i < header.entryCount; i++)
            {
                ushort gID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
                if (gID != 0)
                {
                    GlyphModel newChar = new GlyphModel();
                    newChar.CodePoint = i;
                    newChar.GlyphID = gID;
                    byte[] byteI = BitConverter.GetBytes(i);
                    newChar.Definition = Encoding.UTF32.GetString(byteI);
                    if (!allChars.Contains(newChar))
                    {
                        allChars.Add(newChar);
                    }
                }
            }
        }

        // Reads through the header information of the cmap format 12 table
        private CmapFormat12Header ParseCmapFormat12Header(byte[] source, ref uint offset)
        {
            CmapFormat12Header header = new CmapFormat12Header();

            header.format =   ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            Debug.Assert(header.format == 12);

            header.reserved = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            header.length =   ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
            header.language = ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
            header.nGroups =  ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));

            return header;
        }

        // Parses the format 12 cmap table
        // Refer to https://www.microsoft.com/typography/otspec/cmap.htm to learn about how this format is organized
        private void ParseFormat12(byte[] source, ref uint offset, List<GlyphModel> allChars)
        {
            CmapFormat12Header header = ParseCmapFormat12Header(source, ref offset);

            for (int i = 0; i < header.nGroups; i++)
            {
                uint startCharCode = ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
                uint endCharCode =   ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
                uint startGlyphID =  ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));

                for (uint j = startCharCode; j <= endCharCode; j++)
                {
                    GlyphModel newChar = new GlyphModel();
                    newChar.CodePoint = j;
                    newChar.GlyphID = (ushort)startGlyphID;
                    startGlyphID++;
                    byte[] byteJ = BitConverter.GetBytes(j);
                    newChar.Definition = Encoding.UTF32.GetString(byteJ);
                    if (!allChars.Contains(newChar))
                    {
                        allChars.Add(newChar);
                    }
                }
            }
        }

        // Parse cmap table from the font
        // Only parses cmap entries with format IDs of 0, 4, 6, or 12 (covers the vast majority of cases)
        public List<GlyphModel> ParseCMap(TableRecord tableRecord, byte[] source)
        {
            CmapHeader header = new CmapHeader();
            List<GlyphModel> allChars = new List<GlyphModel>();
            start = tableRecord.offset;
            header.version = ReadDataAndIncreaseIndex<ushort>(source, ref start, sizeof(ushort));
            header.numTables = ReadDataAndIncreaseIndex<ushort>(source, ref start, sizeof(ushort));
            for (int i = 0; i < header.numTables; i++)
            {
                CmapEncodingRecord record = new CmapEncodingRecord();
                record.platformID = ReadDataAndIncreaseIndex<ushort>(source, ref start, sizeof(ushort));
                record.encodingID = ReadDataAndIncreaseIndex<ushort>(source, ref start, sizeof(ushort));
                record.offset =     ReadDataAndIncreaseIndex<uint>(source, ref start, sizeof(uint));
                uint prevPos = start;
                start = record.offset + tableRecord.offset;
                ushort formatID = ReadData<ushort>(source, ref start, sizeof(ushort));
                switch (formatID)
                {
                    case 0:
                        ParseFormat0(source, ref start, allChars);
                        break;
                    case 4:
                        ParseFormat4(source, ref start, allChars);
                        break;
                    case 6:
                        ParseFormat6(source, ref start, allChars);
                        break;
                    case 12:
                        ParseFormat12(source, ref start, allChars);
                        break;
                    default:
                        break;
                }
                start = prevPos;
            }
            return allChars;
        }

        // Change svgContent to show in SVG editor canvas when exported
        private async void WriteSvgContent(StorageFolder outputFolder, string filename, byte[] svgContent)
        {
            if (outputFolder != null)
            {
                filename += ".svg";
                StorageFile newfile = await outputFolder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                ExportChangeSvgOrigin(ref svgContent);
                string svgXML = System.Text.UTF8Encoding.UTF8.GetString(svgContent);
                await FileIO.WriteTextAsync(newfile, svgXML);
            }
        }

        private void ExportChangeSvgOrigin(ref byte[] svgContent)
        {
            byte[] originalSvgContent = svgContent;
            string newSvgString = "";

            // If it is a compressed SVG, throw a NotSupported exception.
            if (originalSvgContent[0] == 0x1F && originalSvgContent[1] == 0x8B)
            {
                throw new System.NotSupportedException("Compressed SVG content (SVGZ content) is not supported.");
            }

            string svgString = Encoding.UTF8.GetString(svgContent);
            XDocument xmlDoc = XDocument.Parse(svgString);

            foreach (XElement element in xmlDoc.Elements())
            {
                if (element.Name.LocalName == "svg")
                {
                    foreach (XAttribute attribute in element.Attributes())
                    {
                        // Change viewBox coordinates so font can render SVG correctly
                        if (attribute.Name.LocalName == "viewBox")
                        {
                            char[] delimiters = { ' ' };
                            string[] viewBoxCoords = attribute.Value.Split(delimiters);
                            newSvgString = viewBoxCoords[0] + " 0 " + viewBoxCoords[2] + " " + viewBoxCoords[3];
                            attribute.Value = newSvgString;
                            break;
                        }
                    }
                }
            }

            // Re-construct XML for SVG content
            StringBuilder stringBuilder = new StringBuilder();
            TextWriter textWriter = new StringWriter(stringBuilder);
            xmlDoc.Save(textWriter);
            string newXML = ReformatXml(xmlDoc.ToString());

            // Save svgContent into bytes for the font file
            svgContent = System.Text.Encoding.UTF8.GetBytes(newXML);
        }

        private string ReformatXml(string xmlString)
        {
            // Load the XmlDocument with the XML
            MemoryStream mStream = new MemoryStream();

            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Encoding = Encoding.UTF8;
            settings.Indent = false;
            settings.NewLineOnAttributes = false;

            XmlWriter writer = XmlWriter.Create(mStream, settings);

            XmlDocument document = new XmlDocument();
            document.LoadXml(xmlString);

            // Write the XML into a formatting XmlTextWriter
            document.WriteContentTo(writer);
            writer.Flush();
            mStream.Flush();

            // Have to rewind the MemoryStream in order to read its contents.
            mStream.Position = 0;

            // Read MemoryStream contents into a StreamReader.
            StreamReader reader = new StreamReader(mStream);

            // Extract the text from the StreamReader.
            string formattedXML = reader.ReadToEnd();

            return formattedXML;
        }

        // Reads through the SVG table copying over all of the SVG content into individual .svg files and writes those files into 
        // a folder of the name, outputFolder
        public int ExportSvgContent(TableRecord rt, byte[] source, StorageFolder outputFolder)
        {
            int numberSvgs = 0;
            start = rt.offset; // start always refer to the offset to the file head.
            current = rt.offset;

            // Read SVG Main Header
            ushort version =         ReadDataAndIncreaseIndex<ushort>(source, ref current, sizeof(ushort));
            uint svgDocIndexOffset = ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));
            uint reserved =          ReadDataAndIncreaseIndex<uint>(source, ref current, sizeof(uint));

            // Read document Index
            uint documentIndexOffset = start + svgDocIndexOffset;
            ushort numEntries = ReadDataAndIncreaseIndex<ushort>(source, ref documentIndexOffset, sizeof(ushort));

            SVGDocIdxEntry[] docIdxEntries = new SVGDocIdxEntry[numEntries];
            for (int i = 0; i < docIdxEntries.Length; i++)
            {
                docIdxEntries[i] = ReadSvgDocIdxEntry(source, ref documentIndexOffset);
                string filename = docIdxEntries[i].startID.ToString();
                byte[] svgContent = new byte[docIdxEntries[i].docLength];
                Array.Copy(source, (int)(docIdxEntries[i].docOffset + rt.offset + svgDocIndexOffset), svgContent, 0, svgContent.Length);
                WriteSvgContent(outputFolder, filename, svgContent);
                numberSvgs++;
            }
            return numberSvgs;
        }

        // Reads a piece of data of length dataSize from source starting at startIndex into dest

        private string ReadDataAndIncreaseIndex(byte[] source, ref uint startIndex, int dataSize)
        {
            byte[] data = new byte[dataSize];
            Array.Copy(source, (int)startIndex, data, 0, dataSize);
            startIndex += (uint)dataSize;
            return Encoding.UTF8.GetString(data);
        }

        private string ReadDataAndIncreaseIndexForFamilyName(byte[] source, ref uint startIndex, int dataSize)
        {
            byte[] data = new byte[dataSize];
            Array.Copy(source, (int)startIndex, data, 0, dataSize);
            startIndex += (uint)dataSize;
            if (data[0] == 0)
            {
                return Encoding.BigEndianUnicode.GetString(data);
            }
            else
            {
                return Encoding.UTF8.GetString(data);
            }
        }

        private byte[] ReadByteArrayAndIncreaseIndex(int numBytes, byte[] source, ref uint startIndex, int dataSize)
        {
            byte[] dest = new byte[numBytes];
            Array.Copy(source, (int)startIndex, dest, 0, dataSize);
            startIndex += (uint)dataSize;
            return dest;
        }

        private ushort[] ReadUshortArrayAndIncreaseIndex(int numUshorts, byte[] source, ref uint startIndex, int dataSize)
        {
            ushort[] dest = new ushort[numUshorts];
            Debug.Assert(dataSize == sizeof(ushort));
            for (int i = 0; i < dest.Length; i++)
            {
                dest[i] = ReadDataAndIncreaseIndex<ushort>(source, ref startIndex, dataSize);
            }
            return dest;
        }

        private short[] ReadShortArrayAndIncreaseIndex(int numShorts, byte[] source, ref uint startIndex, int dataSize)
        {
            short[] dest = new short[numShorts];
            for (int i = 0; i < dest.Length; i++)
            {
                byte[] data = new byte[dataSize];
                Array.Copy(source, (int)startIndex, data, 0, dataSize);
                Array.Reverse(data);
                dest[i] = (short)(object)BitConverter.ToInt16(data, 0);
                startIndex += (uint)dataSize;
            }
            return dest;
        }

        private T ReadDataAndIncreaseIndex<T>(byte[] source, ref uint startIndex, int dataSize)
        {
            T returnValue = ReadData<T>(source, ref startIndex, dataSize);
            startIndex += (uint)dataSize;
            return returnValue;
        }

        private SVGDocIdxEntry ReadSvgDocIdxEntry(byte[] source, ref uint offset)
        {
            SVGDocIdxEntry entry = new SVGDocIdxEntry();

            uint currentOffset = offset;
            entry.startID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.endID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            Debug.Assert(entry.endID >= entry.startID);
            entry.docOffset = ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
            entry.docLength = ReadDataAndIncreaseIndex<uint>(source, ref offset, sizeof(uint));
            Debug.Assert((offset - currentOffset) == Marshal.SizeOf<SVGDocIdxEntry>());

            return entry;
        }

        private NameRecord ReadNameRecordEntry(byte[] source, ref uint offset)
        {
            NameRecord entry = new NameRecord();

            entry.platformID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.encodingID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.languageID = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.nameID     = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.length     = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));
            entry.offset     = ReadDataAndIncreaseIndex<ushort>(source, ref offset, sizeof(ushort));

            return entry;
        }

        private T ReadData<T>(byte[] source, ref uint startIndex, int dataSize)
        {
            byte[] data = new byte[dataSize];
            Array.Copy(source, (int)startIndex, data, 0, dataSize);
            Array.Reverse(data);
            if (dataSize == 2)
            {
                return (T)(object)BitConverter.ToUInt16(data, 0);
            }
            else if (dataSize == 4)
            {
                return (T)(object)BitConverter.ToUInt32(data, 0);
            }
            else
            {
                Debug.Assert(dataSize == 8);
                return (T)(object)BitConverter.ToUInt64(data, 0);
            }
        }

        private byte[] GetBytesBigEndian(ushort data)
        {
            byte[] dataArray = BitConverter.GetBytes(data);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(dataArray);
            }
            return dataArray;
        }

        private byte[] GetBytesBigEndian(uint data)
        {
            byte[] dataArray = BitConverter.GetBytes(data);
            Array.Reverse(dataArray);
            return dataArray;
        }

        private uint CalculatePadding(uint length)
        {
            return (4 - (length % 4)) % 4;
        }
    }
}