﻿using DLSS_Swapper.Data;
using DLSS_Swapper.Extensions;
using DLSS_Swapper.UserControls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MvvmHelpers;
using MvvmHelpers.Commands;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Authentication.OnlineId;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DLSS_Swapper.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class LibraryPage : Page, INotifyPropertyChanged
    {
        public AsyncCommand RefreshCommand { get; }
        public AsyncCommand ExportAllCommand { get; }
        public AsyncCommand ImportCommand { get; }
        public AsyncCommand<DLSSRecord> DeleteRecordCommand { get; }
        public AsyncCommand<DLSSRecord> DownloadRecordCommand { get; }
        public AsyncCommand<DLSSRecord> CancelDownloadRecordCommand { get; }
        public AsyncCommand<DLSSRecord> ExportRecordCommand { get; }
        public AsyncCommand<DLSSRecord> ShowDownloadErrorCommand { get; }


        bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set
            {
                if (_isRefreshing != value)
                {
                    _isRefreshing = value;
                    RefreshCommand.RaiseCanExecuteChanged();
                }
            }
        }


        bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                if (_isExporting != value)
                {
                    _isExporting = value;
                    ExportAllCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ObservableRangeCollection<DLSSRecord> CurrentDLSSRecords { get; }

        public LibraryPage()
        {
            CurrentDLSSRecords = App.CurrentApp.MainWindow.CurrentDLSSRecords;

            RefreshCommand = new AsyncCommand(RefreshListAsync, _ => !IsRefreshing);
            ExportAllCommand = new AsyncCommand(ExportAllAsync, _ => !IsExporting);
            ImportCommand = new AsyncCommand(ImportAsync);
            DeleteRecordCommand = new AsyncCommand<DLSSRecord>(async (record) => await DeleteRecordAsync(record));
            DownloadRecordCommand = new AsyncCommand<DLSSRecord>(async (record) => await DownloadRecordAsync(record));
            CancelDownloadRecordCommand = new AsyncCommand<DLSSRecord>(async (record) => await CancelDownloadRecordAsync(record));
            ExportRecordCommand = new AsyncCommand<DLSSRecord>(async (record) => await ExportRecordAsync(record));
            ShowDownloadErrorCommand = new AsyncCommand<DLSSRecord>(async (record) => await ShowDownloadErrorAsync(record));

            this.InitializeComponent();

            DataContext = this;
        }


        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        async Task RefreshListAsync()
        {
            IsRefreshing = true;

#if WINDOWS_STORE
            await Task.Delay(500);
#else
            var didUpdate = await App.CurrentApp.MainWindow.UpdateDLSSRecordsAsync();
            if (didUpdate)
            {
                App.CurrentApp.MainWindow.FilterDLSSRecords();
                App.CurrentApp.LoadLocalRecords();
                //await App.CurrentApp.LoadLocalRecordsAsync();
            }
            else
            {
                var errorDialog = new EasyContentDialog(XamlRoot)
                {
                    Title = "Error",
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Content = "Unable to update DLSS records.",
                };
                await errorDialog.ShowAsync();
            }
#endif
            IsRefreshing = false;
        }

        async Task ExportAllAsync()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentApp.MainWindow);
                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("Zip archive", new List<string>() { ".zip" });
                savePicker.SuggestedFileName = $"nvngx_dlss.zip";
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
                var saveFile = await savePicker.PickSaveFileAsync();

                // User cancelled.
                if (saveFile == null)
                {
                    return;
                }

                using (var fileStream = File.Create(saveFile.Path))
                {
                    using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                    {
                        var allDlssRecords = new List<DLSSRecord>();
                        allDlssRecords.AddRange(App.CurrentApp.DLSSRecords.Stable);
                        allDlssRecords.AddRange(App.CurrentApp.DLSSRecords.Experimental);

                        foreach (var dlssRecord in allDlssRecords)
                        {
                            if (dlssRecord.LocalRecord?.IsDownloaded == true)
                            {
                                var fullExpectedPath = Path.Combine(Storage.GetStorageFolder(), dlssRecord.LocalRecord.ExpectedPath);
                                var internalZipDir = dlssRecord.Version.ToString();
                                if (String.IsNullOrEmpty(dlssRecord.AdditionalLabel) == false)
                                {
                                    internalZipDir += " " + dlssRecord.AdditionalLabel;
                                }
                                zipArchive.CreateEntryFromFile(fullExpectedPath, Path.Combine(internalZipDir, Path.GetFileName(fullExpectedPath)));
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Logger.Error(err.Message);

                // If the fullExpectedPath does not exist, or there was an error writing it.
                var dialog = new EasyContentDialog(XamlRoot)
                {
                    Title = "Error",
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Content = "Could not export DLSS dll.",
                };
                await dialog.ShowAsync();
            }
        }


        async Task ImportDLL(string filename)
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filename);

            // Can't import DLSS v3 dlls at this time.
            if (versionInfo.FileDescription.Contains("DLSS-G", StringComparison.InvariantCultureIgnoreCase))
            {
                return;
            }

            // Can't import if it isn't a DLSS dll file.
            if (versionInfo.FileDescription.Contains("DLSS", StringComparison.InvariantCultureIgnoreCase) == false)
            {
                return;
            }

            var isTrusted = WinTrust.VerifyEmbeddedSignature(filename);

            // Don't do anything with untrusted dlls.
            if (Settings.Instance.AllowUntrusted == false && isTrusted == false)
            {
                return;
            }

            /* OLD CODE
            // Check if the dll passes windows cert validation. If it doesn't and user has not allowed untrusted then error.
            if (Settings.Instance.AllowUntrusted == false && dlssRecord.IsSignatureValid == false)
            {
                // If it already exists prompt the user if they want to overwrite it.
                dialog = new EasyContentDialog(XamlRoot)
                {
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Title = "Import Failed",
                    Content = $"The dll you imported ({openFile.Path}) is not signed with a valid certificate. If you believe this is a mistake and you want to import anyway enable \"Allow Untrusted\" in DLSS Swapper settings.\n\nONLY enable this setting if you trust where you got the dll from.",
                };
                response = await dialog.ShowAsync();
                return;
            }
            */

            var dllHash = versionInfo.GetMD5Hash();

            var existingImportedDlls = App.CurrentApp.ImportedDLSSRecords.Where(x => String.Equals(x.MD5Hash, dllHash, StringComparison.InvariantCultureIgnoreCase));
            // If the dll is already imported don't import it again.
            if (existingImportedDlls.Any())
            {
                return;
            }

            // If the dll exists 
            /*
            if (App.CurrentApp.DLSSRecords.DLLExists(dllHash, true))
            {
                return;
            }
            */



            var fileInfo = new FileInfo(filename);
            var zipFilename = $"{versionInfo.GetFormattedFileVersion()}_{dllHash}.zip";
            var finalZipPath = Path.Combine(Storage.GetStorageFolder(), "imported_dlss_zip", zipFilename);
            Storage.CreateDirectoryForFileIfNotExists(finalZipPath);
            

            // The plan here was to check if importing is equivilant of downloading the file and if so consider it the downloaded file.
            // The zip hash (and zip filesize) does not match if we create the zip here so it seems odd to have that as a value.
            // We could potentially just consider the ziphash only used for downloading and not on disk validation.
            /*
            var existingDLSSRecord = App.CurrentApp.DLSSRecords.GetRecordFromHash(dllHash);
            if (existingDLSSRecord != null)
            {
                var tempExtractPath = Path.Combine(Storage.GetTemp(), "import");
                Storage.CreateDirectoryIfNotExists(tempExtractPath);
                var tempZipFile = Path.Combine(tempExtractPath, Path.GetFileNameWithoutExtension(filename)) + ".zip";


                using (var zipFile = File.Open(tempZipFile, FileMode.Create))
                {
                    using (var zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Create, true))
                    {
                        zipArchive.CreateEntryFromFile(filename, Path.GetFileName("nvngx_dlss.dll"));
                    }

                    zipFile.Position = 0;
                    var size = zipFile.Length;
                    // Once again, MD5 should never be used to check if a file has been tampered with.
                    // We are simply using it to check the integrity of the downloaded/extracted file.
                    using (var md5 = MD5.Create())
                    {
                        var hash = md5.ComputeHash(zipFile);
                        var zipHash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                    }
                }
            }
            */

            var tempExtractPath = Path.Combine(Storage.GetTemp(), "import");
            Storage.CreateDirectoryIfNotExists(tempExtractPath);

            var tempZipFile = Path.Combine(tempExtractPath, zipFilename);

            var dlssRecord = new DLSSRecord()
            {
                Version = versionInfo.GetFormattedFileVersion(),
                VersionNumber = versionInfo.GetFileVersionNumber(),
                MD5Hash = dllHash,
                FileSize = fileInfo.Length,
                ZipFileSize = 0,
                ZipMD5Hash = String.Empty,
                IsSignatureValid = isTrusted,
            };

            using (var zipFile = File.Open(tempZipFile, FileMode.Create))
            {
                using (var zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Create, true))
                {
                    zipArchive.CreateEntryFromFile(filename, Path.GetFileName("nvngx_dlss.dll"));
                }

                zipFile.Position = 0;

                dlssRecord.ZipFileSize = zipFile.Length;
                // Once again, MD5 should never be used to check if a file has been tampered with.
                // We are simply using it to check the integrity of the downloaded/extracted file.
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(zipFile);
                    dlssRecord.ZipMD5Hash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }

            // Move new record to where it should live in DLSS Swapper app directory.
            File.Move(tempZipFile, finalZipPath, true);
            
            dlssRecord.LocalRecord = LocalRecord.FromExpectedPath(finalZipPath, true);

            // Add our new record.
            App.CurrentApp.ImportedDLSSRecords.Add(dlssRecord);
            var didSave = await Storage.SaveImportedDLSSRecordsJsonAsync();
            if (didSave == false)
            {

                return;
            }

            App.CurrentApp.MainWindow.FilterDLSSRecords();
        }

        async Task ImportAsync()
        {
            if (Settings.Instance.HasShownWarning == false)
            {
                var warningDialog = new EasyContentDialog(XamlRoot)
                {
                    Title = "Warning",
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Content = @"Replacing dlls on your computer can be dangerous.

Placing a malicious dll into a game is as bad as running Linking_park_-_nUmB_mp3.exe that you just downloaded from LimeWire.

Only import dlls from sources you trust.",
                };
                await warningDialog.ShowAsync();

                Settings.Instance.HasShownWarning = true;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentApp.MainWindow);
            var openPicker = new Windows.Storage.Pickers.FileOpenPicker();
            openPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".dll");
            openPicker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
            var openFile = await openPicker.PickSingleFileAsync();

            // User cancelled.
            if (openFile == null)
            {
                return;
            }


            var dialog = new EasyContentDialog(XamlRoot)
            {
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Title = "Reminder",
                Content = $"Only import DLLs from sources you trust.",
            };
            var response = await dialog.ShowAsync();
            if (response == ContentDialogResult.Primary)
            {
                if (File.Exists(openFile.Path) == false)
                {
                 
                    return;
                }


                // Used only if we import a zip
                var tempExtractPath = Path.Combine(Storage.GetTemp(), "import");
                
                try
                {
                    if (openFile.Path.EndsWith(".zip"))
                    {
                        using (var archive = ZipFile.OpenRead(openFile.Path))
                        {
                            var zippedDlls = archive.Entries.Where(x => x.Name.EndsWith(".dll")).ToArray();
                            if (zippedDlls.Length == 0)
                            {
                                throw new Exception("Zip did not contain any dlls..");
                            }

                            Storage.CreateDirectoryIfNotExists(tempExtractPath);

                            foreach (var zippedDll in zippedDlls)
                            {
                                var tempFile = Path.Combine(tempExtractPath, $"nvngx_dlss_{Guid.NewGuid().ToString("D")}.dll");
                                zippedDll.ExtractToFile(tempFile);
                                await ImportDLL(tempFile);

                                // Clean up temp file.
                                File.Delete(tempFile);
                            }
                        }
                    }
                    else
                    {
                        await ImportDLL(openFile.Path);
                    }
                }
                catch (Exception err)
                {
                    // Clean up tempExtractPath if it exists
                    if (Directory.Exists(tempExtractPath))
                    {
                        try
                        {
                            Directory.Delete(tempExtractPath, true);
                        }
                        catch (Exception err2)
                        {
                            Logger.Error(err2.Message);
                        }
                    }

                    Logger.Error(err.Message);


                    // TODO: Button to open error log
                    dialog = new EasyContentDialog(XamlRoot)
                    {
                        CloseButtonText = "Okay",
                        DefaultButton = ContentDialogButton.Close,
                        Title = "Error",
                        Content = $"Could not import record. Please see your error log for more information.",
                    };
                    await dialog.ShowAsync();
                }
            }
        }

        async Task DeleteRecordAsync(DLSSRecord record)
        {
            var dialog = new EasyContentDialog(XamlRoot)
            {
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = $"Delete DLSS v{record.Version}?",
            };
            var response = await dialog.ShowAsync();
            if (response == ContentDialogResult.Primary)
            {
                var didDelete = record.LocalRecord.Delete();
                if (didDelete)
                {
                    if (record.LocalRecord.IsImported)
                    {
                        App.CurrentApp.ImportedDLSSRecords.Remove(record);
                        Storage.SaveImportedDLSSRecordsJsonAsync().SafeFireAndForget();
                        App.CurrentApp.MainWindow.FilterDLSSRecords();
                    }
                    else
                    {
                        record.NotifyPropertyChanged(nameof(record.LocalRecord));
                    }
                }
                else
                {
                    var errorDialog = new EasyContentDialog(XamlRoot)
                    {
                        Title = "Error",
                        CloseButtonText = "Okay",
                        DefaultButton = ContentDialogButton.Close,
                        Content = "Unable to delete DLSS record.",
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        async Task DownloadRecordAsync(DLSSRecord record)
        {
            var result = await record?.DownloadAsync();
            if (result.Success == false && result.Cancelled == false)
            {
                var dialog = new EasyContentDialog(XamlRoot)
                {
                    Title = "Error",
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Content = result.Message,
                };
                await dialog.ShowAsync();
            }
        }

        async Task CancelDownloadRecordAsync(DLSSRecord record)
        {
            record?.CancelDownload();
            await Task.Delay(10);
        }

        async Task ExportRecordAsync(DLSSRecord record)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentApp.MainWindow);
                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("Zip archive", new List<string>() { ".zip" });
                savePicker.SuggestedFileName = $"nvngx_dlss_{record.Version}.zip";
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
                var saveFile = await savePicker.PickSaveFileAsync();

                if (saveFile != null)
                {
                    File.Copy(record.LocalRecord.ExpectedPath, saveFile.Path, true);
                }
            }
            catch (Exception err)
            {
                Logger.Error(err.Message);

                // If the fullExpectedPath does not exist, or there was an error writing it.
                var dialog = new EasyContentDialog(XamlRoot)
                {
                    Title = "Error",
                    CloseButtonText = "Okay",
                    DefaultButton = ContentDialogButton.Close,
                    Content = "Could not export DLSS dll.",
                };
                await dialog.ShowAsync();
            }
        }

        async Task ShowDownloadErrorAsync(DLSSRecord record)
        {
            var dialog = new EasyContentDialog(XamlRoot)
            { 
                Title = "Error",
                CloseButtonText = "Okay",
                Content = record.LocalRecord.DownloadErrorMessage,
            };
            await dialog.ShowAsync();
        }

        async void MainGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0)
            {
                return;
            }

            MainGridView.SelectedIndex = -1;
            if (e.AddedItems[0] is DLSSRecord dlssRecord)
            {
                var dialog = new EasyContentDialog(XamlRoot)
                {
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new DLSSRecordInfoControl(dlssRecord),
                };
                await dialog.ShowAsync();
            }
        }

        void MainGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // via: https://stackoverflow.com/a/41141249
            var columns = Math.Ceiling(MainGridView.ActualWidth / 400);
            ((ItemsWrapGrid)MainGridView.ItemsPanelRoot).ItemWidth = e.NewSize.Width / columns;
        }
    }
}
