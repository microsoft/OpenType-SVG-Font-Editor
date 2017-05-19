// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
namespace OTParser
{
    // This file keeps all table structs.

    public struct OffsetTable
    {
        public string tag;
        public ushort numTables;
        public ushort searchRange;
        public ushort entrySelector;
        public ushort rangeShift;
    }

    public struct TableRecord
    {
        public string tag;
        public uint tagInt;
        public uint checksum;

        // This is not actually present in OT fonts. 
        // It is just used to keep track of the location of the offsets in Table Records.
        // Used in ChangeTableRecOffsets to easily edit Table Record offsets
        public uint offsetOfOffset;

        public uint offset;
        public uint length;
    };

    public struct CmapHeader
    {
        public ushort version;
        public ushort numTables;
    };

    public struct  NameRecord
    {
        public ushort platformID;
        public ushort encodingID;
        public ushort languageID;
        public ushort nameID;
        public ushort length;
        public ushort offset;
    }

    public struct CmapEncodingRecord
    {
        public ushort platformID;
        public ushort encodingID;
        public uint offset;
    };

    public struct CmapFormat4Header
    {
        public ushort format;
        public ushort length;
        public ushort language;
        public ushort segCountX2;
        public ushort searchRange;
        public ushort entrySelector;
        public ushort rangeShift;
    }

    public struct CmapFormat6Header
    {
        public ushort format;
        public ushort length;
        public ushort language;
        public ushort firstCode;
        public ushort entryCount;
    }

    public struct CmapFormat12Header
    {
        public ushort format;
        public ushort reserved;
        public uint length;
        public uint language;
        public uint nGroups;
    }

    public struct SVGDocIdxEntry
    {
        public ushort startID;
        public ushort endID;
        public uint docOffset;
        public uint docLength;
    };
}