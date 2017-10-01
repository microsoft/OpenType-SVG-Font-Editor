# OpenType SVG Font Editor

The OpenType SVG Font Editor is a Universal Windows Platform (UWP) app for
embedding SVG glyphs in an [OpenType](https://www.microsoft.com/en-us/Typography/OpenTypeSpecification.aspx)
font. It was designed to simplify the process of creating SVG-based icon fonts,
with web and app designers in mind. It can be considered a simpler, GUI-based
version of the [`addSVGtable`](https://github.com/adobe-type-tools/opentype-svg)
tool by Miguel Sousa of Adobe. Both of these tools use OpenType's ['svg ' table](https://www.microsoft.com/typography/otspec/svg.htm)
to include SVG content in the font file.

This project was originally developed by Microsoft interns Alice Wen, Anya
Hargil, and Julia Weaver. While we hope you find the tool useful, Microsoft
makes no guarantees about the quality of the app or the fonts it generates.

## Acquiring the app

You can download a recent build of the app [from the Windows Store](https://www.microsoft.com/store/apps/9nj7k9jx60p1)
or build the app yourself using the instructions below.

## Building the app

Compiling this project requires [Visual Studio 2017](https://www.visualstudio.com/vs)
or later and Windows 10 SDK version 10.0.15063.0 or later.

1. Download or clone the project repository.
2. Launch OTSVGEditor.sln.
3. Build the solution and launch the Editor project.

## Using the app

Running this app requires Windows 10 Creators Update or later.

The primary purpose of this app is to embed Scalable Vector Graphics (SVG)
assets over existing monochrome glyphs in a font using a straightforward 
drag-and-drop interface. The app does so by automatically creating and updating the
appropriate SVG-related OpenType tables in the font file and making appropriate
adjustments to the SVG content as required by the OpenType spec.

The app can also remove SVG glyphs from a font, as well as copy all the SVG
assets out of a font and into standalone .SVG files on disk.

### Adding SVG glyphs to a font

1. Launch the app.
2. Click "Select font file..." and browse to the font file you want to modify.
   Once the app has loaded the font, it displays the list of glyphs (Unicode
   codepoints) defined by the font in a grid on the right.
3. Click "Select SVG folder..." and browse to the directory containing the SVG
   assets you want to embed. Once the app has loaded the SVG assets, it displays
   them in a list on the left.
4. To embed a new SVG glyph, drag an SVG file from the list on the left onto a
   glyph on the right. The app updates the glyph preview to show the placed SVG
   glyph.
5. When you're finished, click "Save font as..." to save the modified font file
   to disk. (No changes are made to the original font file unless you save over
   it.) The resulting font file may be packaged with your app, installed on your
   system, or otherwise used anywhere OpenType SVG fonts are supported.

### Removing SVG glyphs from a font

1. Launch the app.
2. Click "Select font file..." and browse to the font file you want to modify.
   Once the app has loaded the font, it displays the list of glyphs (Unicode
   codepoints) defined by the font in a grid on the right.
3. Right-click the glyph whose SVG representation you want to remove from the
   font, and select "Delete SVG".
4. When you're finished, click "Save font as..." to save the modified font file
   to disk.

### Extracting all SVG assets from a font

1. Launch the app.
2. Click "Select font file..." and browse to the font file whose glyphs you want
   to extract.
3. Click "Export all SVGs..." and select a destination folder.
4. The app will scan the font file for SVG glyphs and save them as individual
   .SVG files to the specified folder.

## Limitations and known issues

* The app does not support creating new fonts "from scratch." You must start
  with a "base" font, and you may only embed SVG onto existing glyphs in that
  font.
* The app does not support editing font characteristics such as advance width,
  kerning, ligatures, color palettes, or cross-glyph SVG sharing.
* This app only parses cmap table formats 0, 4, 6, and 12.
* The app's glyph preview grid only renders SVG glyphs according to Windows'
  support. Other text renderers may give different results.

## License

This project is licensed under the MIT License.

## Code of Conduct

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct).
For more information, see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/)
or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any
additional questions or comments.
