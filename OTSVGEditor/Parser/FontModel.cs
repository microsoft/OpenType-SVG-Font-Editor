// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Notifications;

namespace OTParser
{
    public class FontModel
    {
        public ObservableCollection<GlyphModel> AllGlyphs { get; private set; }
        public string LocalFont { get; private set; }
        public string FontFileName { get; private set; }
        public string FamilyName { get; private set; }
        public TableRecord[] TableRecords { get; private set; }

        private byte[] source;
        private StorageFile temporaryFontFile;
        private OffsetTable offsetTable;
        private Parser parser;

        public FontModel()
        {
            parser = new Parser();
            AllGlyphs = new ObservableCollection<GlyphModel>();
        }

        public async Task ReloadDataAsync()
        {
            // After loading of a font file, reload data for each dragged SVG and dropped onto a glyph.
            await LoadDataAsync(temporaryFontFile);
        }

        public async Task LoadDataAsync(StorageFile file)
        {
            parser.Start = 0;
            parser.Current = 0;

            FontFileName = file.Name;

            // Read contents of file and return a buffer.
            IBuffer buffer = await FileIO.ReadBufferAsync(file);
            source = new byte[buffer.Length];

            // Put buffer byte info into source.
            using (DataReader reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(source);
            }

            offsetTable = parser.ParseOffsetTable(source);

            TableRecords = new TableRecord[offsetTable.numTables];

            for (int i = 0; i < TableRecords.Length; i++)
            {
                TableRecords[i] = parser.ReadTableRecord(source);
            }

            // Create a temporary list to contain the updated GlyphModel objects.
            List<GlyphModel> tempList = new List<GlyphModel>();

            foreach (TableRecord record in TableRecords)
            {
                if (record.tag == "cmap")
                {
                    // Only supports cmap tables of format 0 or format 4.
                    foreach (GlyphModel currentGlyph in parser.ParseCMap(record, source))
                    {
                        // If codepoint matches known whitespace, do not append to preview list.
                        uint codepoint = currentGlyph.CodePoint;
                        if (0x0 <= codepoint && codepoint <= 0x001F || 0x7F <= codepoint && codepoint <= 0xA0 || 0x2000 <= codepoint && codepoint <= 0x200F ||
                            codepoint == 0x202F || codepoint == 0x205F || codepoint == 0x3000 || codepoint == 0xFEFF ||codepoint ==0x20)
                        {
                            continue;
                        }
                        else
                        {
                            tempList.Add(currentGlyph);
                        }
                    }
                }
                else if (record.tag == "name")
                {
                    FamilyName = parser.GetFamilyName(record, source);

                    // If no FamilyName is found, do not load the font.
                    if (FamilyName == null)
                    {
                        return;
                    }
                }
            }

            // Create local font saved in temp folder.
            LocalFont = "ms-appdata:///temp/" + FontFileName + "#" + FamilyName;

            // Assign local font to each GlyphModel.
            foreach (GlyphModel glyph in tempList)
            {
                glyph.FontFamily = LocalFont;
            }
            
            // Repopulate the list of all GlyphModel objects.
            AllGlyphs.Clear();
            foreach (GlyphModel glyph in tempList)
            {
                AllGlyphs.Add(glyph);
            }
        }

        public async Task AddSvgAsync(ushort glyphID, StorageFile svgFile)
        {
            // Add SVG data into a buffer.
            IBuffer buffer = await FileIO.ReadBufferAsync(svgFile);
            byte[] svgContent = new byte[buffer.Length];

            // Read buffer into bytes in svgContent.
            using (DataReader reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(svgContent);
            }

            // Tweak the SVG: Add glyph ID and adjust the X/Y origin in accordance with OpenType spec.
            ChangeSvgOriginID(ref svgContent, glyphID);
            
            for (int i = 0; i < TableRecords.Length; i++)
            {
                if (TableRecords[i].tag == "SVG ")
                {
                    parser.AssembleSvgContent(ref TableRecords[i], ref source, svgContent, glyphID, this);
                    break;
                }
                // Checks if SVG table is not present
                else if (String.Compare(TableRecords[i].tag, "SVG ", StringComparison.Ordinal) > 0)
                {
                    // Changes offset table to reflect the addition of a new table
                    offsetTable = parser.UpdateOffsetTable(offsetTable, ref source);
                    // Changes the offsets contained in all of the table records in order to reflect the addition of a table record
                    parser.ChangeTableRecOffsets(TableRecords, ref source, ((sizeof(uint) * 4) * -1), 0);
                    byte[] newSource = new byte[source.Length + (sizeof(uint) * 6) + (sizeof(ushort) * 2)];
                    // Copies from the beginning of the source array to the end of the table record preceeding the location of the new SVG table record
                    Array.Copy(source, 0, newSource, 0, (int)(TableRecords[i].offsetOfOffset - (sizeof(uint) * 2)));
                    string tag = "SVG ";
                    byte[] svgTag = Encoding.ASCII.GetBytes((string)tag);
                    // Adds SVG tag for the table record
                    Array.Copy(svgTag, 0, newSource, (int)(TableRecords[i].offsetOfOffset - (sizeof(uint) * 2)), sizeof(uint));
                    byte[] checksum = BitConverter.GetBytes((uint)0);
                    Array.Reverse(checksum);
                    // Adds the checksum for the table record
                    Array.Copy(checksum, 0, newSource, (int)(TableRecords[i].offsetOfOffset - sizeof(uint)), sizeof(uint));
                    byte[] offset = BitConverter.GetBytes((uint)(source.Length + (sizeof(uint) * 4)));
                    Array.Reverse(offset);
                    // Adds the offset for the table record
                    Array.Copy(offset, 0, newSource, (int)(TableRecords[i].offsetOfOffset), sizeof(uint));
                    byte[] length = BitConverter.GetBytes((uint)((sizeof(ushort) + sizeof(uint)) * 2));
                    Array.Reverse(length);
                    // Adds the length for the table record
                    Array.Copy(length, 0, newSource, (int)(TableRecords[i].offsetOfOffset + sizeof(uint)), sizeof(uint));
                    // Copies from the beginning of the table record following the new SVG table record in the source array to the end of the source array
                    Array.Copy(source, (int)(TableRecords[i].offsetOfOffset - (sizeof(uint) * 2)), newSource, (int)(TableRecords[i].offsetOfOffset + (sizeof(uint) * 2)), (int)(source.Length - (TableRecords[i].offsetOfOffset - (sizeof(uint) * 2))));
                    byte[] version = BitConverter.GetBytes((ushort)0);
                    Array.Reverse(version);
                    // Adds the SVG table at the very end of the font file
                    // Copies the version number to the newSource array (0)
                    Array.Copy(version, 0, newSource, (int)(source.Length + (sizeof(uint) * 4)), sizeof(ushort));
                    byte[] svgDocIndexOffset = BitConverter.GetBytes((uint)(sizeof(ushort) + (sizeof(uint) * 2)));
                    Array.Reverse(svgDocIndexOffset);
                    // Copies the SVG Document Index Offset to the newSource array
                    Array.Copy(svgDocIndexOffset, 0, newSource, (int)(source.Length + (sizeof(uint) * 4) + sizeof(ushort)), sizeof(uint));
                    byte[] reserved = BitConverter.GetBytes((uint)0);
                    Array.Reverse(reserved);
                    // Copies reserved to th enewSource array
                    Array.Copy(reserved, 0, newSource, (int)(source.Length + (sizeof(uint) * 5) + sizeof(ushort)), sizeof(uint));
                    byte[] numEntries = BitConverter.GetBytes((ushort)0);
                    Array.Reverse(numEntries);
                    // Copies the number of SVG Document Index Entries to the newSource array (in this case just 0 because it is a new table)
                    Array.Copy(numEntries, 0, newSource, (int)(source.Length + (sizeof(uint) * 6) + sizeof(ushort)), sizeof(ushort));
                    TableRecord svg = new TableRecord();
                    svg.tag = "SVG ";
                    svg.checksum = 0;
                    svg.offsetOfOffset = TableRecords[i].offsetOfOffset;
                    svg.offset = (uint)(source.Length + (sizeof(uint) * 4));
                    svg.length = (uint)((sizeof(ushort) + sizeof(uint)) * 2);
                    source = newSource;
                    parser.Start = 12; // Start is 12 because the offset table is always before the table records and is always 12 bytes in length
                    parser.Current = 12;

                    TableRecords = new TableRecord[offsetTable.numTables];
                    for (int j = 0; j < TableRecords.Length; j++)
                    {
                        TableRecords[j] = parser.ReadTableRecord(source);
                    }
                    parser.AssembleSvgContent(ref svg, ref source, svgContent, glyphID, this);
                    break;
                }
            }

            // Create an updated font file in temp folder.
            StorageFolder tempFolder = ApplicationData.Current.TemporaryFolder;
            StorageFile newFontFile = await tempFolder.CreateFileAsync(FontFileName, CreationCollisionOption.GenerateUniqueName);
            await FileIO.WriteBytesAsync(newFontFile, source);

            // Hang on to the new font file.
            temporaryFontFile = newFontFile;
        }

        private void ChangeSvgOriginID(ref byte[] svgContent, ushort glyphID)
        {
            string svgString = System.Text.Encoding.UTF8.GetString(svgContent);
            XDocument xmlDoc = XDocument.Parse(svgString);

            foreach (XElement element in xmlDoc.Elements())
            {
                if (element.Name.LocalName == "svg")
                {
                    bool changedID = false;
                    foreach (XAttribute attribute in element.Attributes())
                    {
                        // Change viewBox coordinates so font can render SVG correctly.
                        if (attribute.Name.LocalName == "viewBox")
                        {
                            char[] delimiters = { ' ' };
                            string[] viewBoxCoords = attribute.Value.Split(delimiters);
                            string width = viewBoxCoords[2];
                            string height = viewBoxCoords[3];
                            string newSvgString = viewBoxCoords[0] + " " + height + " " + width + " " + height;
                            attribute.Value = newSvgString;
                        }
                        // Edit the ID to correspond with the glyph.
                        else if (attribute.Name.LocalName == "id")
                        {
                            string newID = "glyph" + glyphID;
                            attribute.Value = newID;
                            changedID = true;
                        }
                    }
                    // If there is no ID attribute, create one.
                    if (!changedID)
                    {
                        string newID = "glyph" + glyphID;
                        XAttribute xatt = new XAttribute("id", newID);
                        element.Add(xatt);
                    }
                    break;
                }
            }

            // Re-construct XML for SVG content.
            StringBuilder stringBuilder = new StringBuilder();
            TextWriter textWriter = new StringWriter(stringBuilder);
            xmlDoc.Save(textWriter);
            string newXML = ReformatXml(xmlDoc.ToString());

            // Save svgContent into bytes for the font file.
            svgContent = Encoding.UTF8.GetBytes(newXML);
        }

        private string ReformatXml(string xmlString)
        {
            // Load the XmlDocument with the XML.
            MemoryStream mStream = new MemoryStream();

            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Encoding = Encoding.UTF8;
            settings.Indent = false;
            settings.NewLineOnAttributes = false;

            XmlWriter writer = XmlWriter.Create(mStream, settings);

            XmlDocument document = new XmlDocument();
            document.LoadXml(xmlString);

            // Write the XML into a formatting XmlTextWriter.
            document.WriteContentTo(writer);
            writer.Flush();
            mStream.Flush();

            // Have to rewind the MemoryStream in order to read its contents.
            mStream.Position = 0;

            // Read MemoryStream contents into a StreamReader.
            StreamReader reader = new StreamReader(mStream);

            // Extract the text from the StreamReader.
            String formattedXML = reader.ReadToEnd();

            return formattedXML;
        }

        public async Task RemoveSvgAsync(ushort glyphID)
        {
            for (int i = 0; i < TableRecords.Length; i++)
            {
                if (TableRecords[i].tag == "SVG ")
                {
                    parser.RemoveSvgContent(ref TableRecords[i], ref source, glyphID, this);
                    break;
                }
            }

            // Create an updated font file in temp folder.
            StorageFolder tempFolder =  ApplicationData.Current.TemporaryFolder;
            StorageFile newFontFile = await tempFolder.CreateFileAsync(FontFileName, CreationCollisionOption.GenerateUniqueName);
            await FileIO.WriteBytesAsync(newFontFile, source);

            // Hang on to the new font file.
            temporaryFontFile = newFontFile;
        }

        public byte[] GetFontBytes()
        {
            return source;
        }

        public void ExportSvg(StorageFolder outputFolder)
        {
            if (TableRecords == null || outputFolder == null)
            {
                return;
            }

            // Export each SVG glyph by iterating over the table records.
            int numExportedSvgs = 0;
            foreach (TableRecord record in TableRecords)
            {
                if (record.tag == "SVG ")
                {
                    numExportedSvgs = parser.ExportSvgContent(record, source, outputFolder);
                }
            }

            // Show toast with number of successful SVG files saved.
            if (numExportedSvgs > 0)
            {
                // Make the toast.
                ToastTemplateType toastTemplate = ToastTemplateType.ToastText01;
                Windows.Data.Xml.Dom.XmlDocument xml = ToastNotificationManager.GetTemplateContent(toastTemplate);
                xml.DocumentElement.SetAttribute("launch", "Args");

                // Set up the toast text.
                string toastString = numExportedSvgs + " SVGs were successfully saved to " + outputFolder.Path;
                Windows.Data.Xml.Dom.XmlText toastText = xml.CreateTextNode(toastString);
                Windows.Data.Xml.Dom.XmlNodeList elements = xml.GetElementsByTagName("text");
                elements[0].AppendChild(toastText);

                // Show the toast.
                ToastNotification toast = new ToastNotification(xml);
                ToastNotifier toastNotifier = ToastNotificationManager.CreateToastNotifier();
                toastNotifier.Show(toast);
            }
        }
    }
}