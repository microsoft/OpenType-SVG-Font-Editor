// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
namespace OTParser
{
    // This class contains all information to track a single glyph of a font.
    public class GlyphModel
    {
        public uint CodePoint;
        public ushort GlyphID;
        public string Definition;
        public string FontFamily;

        public string CodePointHexString
        {
            get
            {
                string paddedHex = this.CodePoint.ToString("X4");
                return "U+" + paddedHex;
            }
        }

        public override bool Equals(object obj)
        {
            if (obj == null || !(obj.GetType().Equals(this.GetType())))
            {
                return false;
            }
            else if (((GlyphModel)obj).GlyphID.Equals(this.GlyphID))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}