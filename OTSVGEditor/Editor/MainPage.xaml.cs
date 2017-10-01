// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the MIT License. See LICENSE in the project root for license information.
using OTParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Editor
{
    /// <summary>
    /// The main page of our app where all functionality is implemented. In this page the UI shows the users font 
    /// and SVG assets. All modification of the font file is done on this page.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private FontModel fontFile;
        private ObservableCollection<SvgFileItem> svgFiles;
        private StorageFolder svgPreviewTempFolder;
        private IList<object> selectedSvgItems;

        public MainPage()
        {
            this.InitializeComponent();

            PickSVGFolderButton.Click += new RoutedEventHandler(PickFolderButton_Click);
            PickAFileButton.Click += new RoutedEventHandler(SelectFontFile_Click);
            SaveFileButton.Click += new RoutedEventHandler(SaveFileButton_Click);

            svgFiles = new ObservableCollection<SvgFileItem>();
            fontFile = new FontModel();
            selectedSvgItems = new List<object>();
        }

        private static void FindChildren<T>(List<T> results, DependencyObject startNode)
            where T : DependencyObject
        {
            int numChildren = VisualTreeHelper.GetChildrenCount(startNode);
            for (int i = 0; i < numChildren; i++)
            {
                DependencyObject current = VisualTreeHelper.GetChild(startNode, i);
                if ((current.GetType()).Equals(typeof(T)) || (current.GetType().GetTypeInfo().IsSubclassOf(typeof(T))))
                {
                    T asType = (T)current;
                    results.Add(asType);
                }
                FindChildren<T>(results, current);
            }
        }

        // When "Select font file..." button is clicked, the app prompts the user to pick a font file, and loads it.
        private async void SelectFontFile_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.List;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".otf");
            openPicker.FileTypeFilter.Add(".ttf");

            StorageFile file = await openPicker.PickSingleFileAsync();
            if (file != null)
            {
                // The app now has read access to the picked file.
                FontName.Text = file.Name;

                try
                {
                    // Copy the font file into the app's temp folder so we can manipulate it.
                    await file.CopyAsync(ApplicationData.Current.TemporaryFolder, file.Name, NameCollisionOption.ReplaceExisting);

                    // Parse the font file and build the list of glyphs it containss. The XAML UI 
                    // is bound to the FontFile.AllGlyphs list and will automatically update.
                    await fontFile.LoadDataAsync(file);
                }
                catch (Exception exc)
                {
                    await NotifyUserAsync("The app encountered an error while trying to load the selected font file. Please ensure that the font file is well-formed and that you have permission to access it.", exc);
                    return;
                }

                // Hide hint text and show new buttons once the font is successfully parsed.
                GlyphGridHint.Visibility = Visibility.Collapsed;
                ExportSVGButton.Visibility = Visibility.Visible;
                SaveFileButton.Visibility = Visibility.Visible;
            }
        }

        // When "Select SVG folder..." is clicked, the app prompts the user to select a folder
        // containing SVG assets, and loads them.
        private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Create a "preview" folder in the app's temp folder to simplify previewing SVG
                // files with the WebView control.
                svgPreviewTempFolder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("preview", CreationCollisionOption.OpenIfExists);
            }
            catch (Exception exc)
            {
                await NotifyUserAsync("The app encountered an error while trying to allocate temporary files.", exc);
                return;
            }

            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
            folderPicker.FileTypeFilter.Add(".svg");

            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                // Hide hint text and enable new UI.
                SvgListHint.Visibility = Visibility.Collapsed;
                SvgPreviewWrapper.Visibility = Visibility.Collapsed;
                SvgPreviewHintWrapper.Visibility = Visibility.Visible;

                // App now has access contents of the picked folder (including sub-folder contents).
                StorageApplicationPermissions.FutureAccessList.AddOrReplace("PickedFolderToken", folder);

                // Update status text.
                SvgFolderStatusTextBlock.Text = folder.Name;

                try
                {
                    // Get the SVG files in the selected folder.
                    IReadOnlyList<StorageFile> filesInFolder = await folder.GetFilesAsync();

                    // Build a new list of SvgFileItem objects.
                    svgFiles.Clear();

                    foreach (StorageFile file in filesInFolder)
                    {
                        if (file.FileType == ".svg")
                        {
                            // Copy the SVG file into temp storage.
                            await file.CopyAsync(svgPreviewTempFolder, file.Name, NameCollisionOption.ReplaceExisting);

                            // Request a file thumbnail from the OS.
                            BitmapImage bitmapImage = new BitmapImage();
                            bitmapImage.SetSource(await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.ListView));

                            // Add a new item to the model. XAML binding automatically updates the view.
                            svgFiles.Add(
                                new SvgFileItem()
                                {
                                    FileName = file.Name.ToString(),
                                    Thumbnail = bitmapImage,
                                    SVGPath = file.Path,
                                    SVGFile = file
                                }
                                );
                        }
                    }
                }
                catch (Exception exc)
                {
                    await NotifyUserAsync("The app encountered an error while trying to read the files in the selected folder. Please ensure you have permission to access the files.", exc);
                    return;
                }
            }
        }

        // When the "Save font as..." button is clicked, prompt the user to save the modified font.
        private async void SaveFileButton_Click(object sender, RoutedEventArgs e)
        {
            FileSavePicker savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("OpenType Font", new List<string>() { ".otf" });
            savePicker.FileTypeChoices.Add("TrueType Font", new List<string>() { ".ttf" });
            savePicker.SuggestedFileName = "NewFont";

            StorageFile saveFile = await savePicker.PickSaveFileAsync();
            if (saveFile != null)
            {
                try
                {
                    // Retrieve the byte representation of the font file and write it to disk.
                    byte[] finishedFont = fontFile.GetFontBytes();
                    await FileIO.WriteBytesAsync(saveFile, finishedFont);

                    // Clear temp files.
                    await DeleteTempFilesAsync(ApplicationData.Current.TemporaryFolder);
                }
                catch (Exception exc)
                {
                    await NotifyUserAsync("The app encountered an error while trying to write the font file to disk.", exc);
                    return;
                }
            }
        }

        private async Task DeleteTempFilesAsync(StorageFolder folder)
        {
            IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();

            foreach (StorageFile file in files)
            {
                if (file.Name != fontFile.FontFileName) // Don't erase the file we're actively using.
                {
                    // Ignore deletion failures. (If a UI element is still holding a file handle, 
                    // and the file can't be deleted, it'll get cleaned up next time around.)
                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.Default);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return;
                    }
                }
            }
        }

        // Drag and drop functionality.
        private void SVGlistView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            e.Data.RequestedOperation = DataPackageOperation.Copy; // allows item to be copied 
            selectedSvgItems = e.Items;
        }

        // Drag and drop: Occurs when user drags item over the GridView drop area.
        private void GridView_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Embed this SVG";
        }

        // Drag and drop: Identifies the targeted glyph and calls backend to embed the SVG.
        private async void GridView_Drop(object sender, DragEventArgs e)
        {
            try
            {
                GlyphModel glyph = null;
                if (e.OriginalSource is StackPanel)
                {
                    StackPanel target = e.OriginalSource as StackPanel;
                    if (target.DataContext is GlyphModel)
                    {
                        glyph = target.DataContext as GlyphModel;
                        foreach (SvgFileItem file in selectedSvgItems)
                        {
                            // Embed the selected SVG onto the selected glyph.
                            await fontFile.AddSvgAsync(glyph.GlyphID, file.SVGFile);

                            // Refresh the preview grid to reflect the modified font.
                            await ReloadPreviewAsync();
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                await NotifyUserAsync("The app encountered an error while trying to add SVG to the font.", exc);
                return;
            }
        }

        // When a glyph preview is right-clicked, instantiate a popup menu and allow the
        // user to delete that glyph's SVG glyph if present.
        public async void Glyph_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Prepare the context menu.
            PopupMenu menu = new PopupMenu();
            menu.Commands.Add(new UICommand("Delete SVG", async (command) =>
            {
                // Retrieve the selected Characer object.
                GlyphModel glyph = null;
                if (e.OriginalSource is TextBlock)
                {
                    TextBlock target = e.OriginalSource as TextBlock;
                    if (target.DataContext is GlyphModel)
                    {
                        glyph = target.DataContext as GlyphModel;

                        try
                        {
                            // Call the backend to remove the specified SVG.
                            await fontFile.RemoveSvgAsync(glyph.GlyphID);

                            // Refresh the preview grid to reflect the updated font.
                            await ReloadPreviewAsync();
                        }
                        catch (Exception exc)
                        {
                            await NotifyUserAsync("The app encountered an error while trying to remove SVG from the font.", exc);
                            return;
                        }
                    }
                }
            }));

            // Invoke the context menu.
            await menu.ShowForSelectionAsync(GetElementRect((FrameworkElement)sender));
        }

        // Helper for popup menu.
        public static Rect GetElementRect(FrameworkElement element)
        {
            GeneralTransform buttonTransform = element.TransformToVisual(null);
            Point point = buttonTransform.TransformPoint(new Point());
            return new Rect(point, new Size(element.ActualWidth, element.ActualHeight));
        }

        private async Task ReloadPreviewAsync()
        {
            // Store the scrolled offset of the preview grid so we can restore it.
            List<ScrollViewer> scrollViewerList = new List<ScrollViewer>();
            FindChildren<ScrollViewer>(scrollViewerList, GlyphGridView);
            double[] yPos = new double[scrollViewerList.Count];
            for (int i = 0; i < scrollViewerList.Count; i++)
            {
                yPos[i] = scrollViewerList[i].VerticalOffset;
            }
            
            // Reloading font data.
            await fontFile.ReloadDataAsync();

            // Restore scroll position of preview grid.
            for (int i = 0; i < scrollViewerList.Count; i++)
            {
                scrollViewerList[i].ChangeView(null, yPos[i], null, true);
            }
        }

        // When the "Export all SVG..." button is clicked, retrieve all SVG objects from the font
        // and store them to disk.
        private async void ExportAllSvgButtonClick(object sender, RoutedEventArgs e)
        {
            FolderPicker svgFolderPicker = new FolderPicker();
            svgFolderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
            svgFolderPicker.FileTypeFilter.Add("*");

            StorageFolder outputFolder = await svgFolderPicker.PickSingleFolderAsync();
            if (outputFolder != null)
            {
                try
                {
                    // Extract and save all SVG files into picked folder, replacing any duplicates.
                    fontFile.ExportSvg(outputFolder);
                }
                catch (Exception exc)
                {
                    await NotifyUserAsync("The app encountered an error while trying to export SVG files from the font. Please ensure the font is well-formed and that you have access to the destination folder.", exc);
                    return;
                }
            }
        }

        private async Task NotifyUserAsync(string errorMessage, Exception e)
        {
            string content = errorMessage + "\n\nException message: " + e.Message;
            MessageDialog message = new MessageDialog(content);
            message.Title = "Error";
            await message.ShowAsync();
        }

        // When an item in the list of SVG files is selected, show it in the preview WebView.
        private async void SVGlistView_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                Debug.Assert(e.ClickedItem is SvgFileItem);
                SvgFileItem selectedItem = e.ClickedItem as SvgFileItem;

                // Set the source of the preview Image to the SVG file.
                SVGPreviewImage.Source = new SvgImageSource(new Uri("ms-appdata:///temp/preview/" + selectedItem.FileName));

                selectedSvgItems.Clear();
                selectedSvgItems.Add(selectedItem);
            }
            catch (Exception exc)
            {
                await NotifyUserAsync("The app encountered an error while trying to preview the selected SVG file.", exc);
                return;
            }

            // Hide hint text and reveal the preview.
            SvgPreviewHintWrapper.Visibility = Visibility.Collapsed;
            SvgPreviewWrapper.Visibility = Visibility.Visible;
        }
    }
}
