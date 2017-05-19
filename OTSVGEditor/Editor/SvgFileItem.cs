// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace Editor
{
    // This class contains all information needed to enumerate and preview the SVG files in a directory.
    public class SvgFileItem
    {
        public StorageFile SVGFile { get; set; }
        public string FileName { get; set; }
        public  BitmapImage Thumbnail { get; set; }
        public string SVGPath { get; set; }
        public override string ToString()
        {
            return FileName;
        }
    }
}
