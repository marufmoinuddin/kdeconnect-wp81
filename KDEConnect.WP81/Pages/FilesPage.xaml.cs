using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace KDEConnect.WP81.Pages
{
    /// <summary>
    /// Browses the phone's storage, including the SD card.
    /// </summary>
    /// <remarks>
    /// The SD card appears because the package declares removableStorage and
    /// because KnownFolders.RemovableDevices is probed at runtime. If the card is
    /// still missing, this page says which of the two failed instead of showing
    /// an empty list - an empty list is indistinguishable from "no card fitted".
    /// </remarks>
    public sealed partial class FilesPage : Page
    {
        private readonly StorageBrowser _browser = new StorageBrowser();
        private readonly ObservableCollection<FileRow> _rows = new ObservableCollection<FileRow>();
        private readonly Dictionary<string, FileRow> _byPath = new Dictionary<string, FileRow>();
        private readonly List<StorageFolder> _stack = new List<StorageFolder>();

        public FilesPage()
        {
            InitializeComponent();
            FileListView.ItemsSource = _rows;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ShowRoots();
        }

        // ---- rows ----------------------------------------------------------

        private sealed class FileRow
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public bool IsFolder { get; set; }
            public string Subtitle { get; set; }
        }

        private void AddRow(FileRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.Path))
            {
                return;
            }
            _rows.Add(row);
            _byPath[row.Path] = row;
        }

        private void ShowBusy(bool busy)
        {
            BusyRing.IsActive = busy;
            UpButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
        }

        // ---- roots ---------------------------------------------------------

        private async void ShowRoots()
        {
            ShowBusy(true);
            _stack.Clear();
            _rows.Clear();
            _byPath.Clear();
            PathText.Text = "Storage";
            StatusText.Text = "Reading storage...";

            try
            {
                StorageRootsResult result = await _browser.GetRootsAsync();

                for (int i = 0; i < result.Roots.Count; i++)
                {
                    StorageRoot root = result.Roots[i];
                    AddRow(new FileRow
                    {
                        Name = root.Name,
                        Path = root.Path,
                        IsFolder = true,
                        Subtitle = root.Note
                    });
                }

                if (result.Notes.Count == 0)
                {
                    StatusText.Text = "Tap a location to browse.";
                }
                else
                {
                    StatusText.Text = string.Join(Environment.NewLine, result.Notes.ToArray());
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not read storage: " + ex.Message;
            }
            finally
            {
                ShowBusy(false);
            }
        }

        // ---- folder listing ------------------------------------------------

        private async Task ShowFolderAsync(StorageFolder folder)
        {
            ShowBusy(true);
            _rows.Clear();
            _byPath.Clear();
            PathText.Text = StorageListing.DisplayPath(folder.Path);
            StatusText.Text = "";

            try
            {
                StorageListingResult listing = await _browser.ListAsync(folder);

                for (int i = 0; i < listing.Entries.Count; i++)
                {
                    StorageEntry entry = listing.Entries[i];
                    AddRow(new FileRow
                    {
                        Name = entry.Name,
                        Path = entry.Path,
                        IsFolder = entry.IsFolder,
                        Subtitle = StorageListing.Subtitle(entry)
                    });
                }

                if (!string.IsNullOrEmpty(listing.Problem))
                {
                    StatusText.Text = listing.Problem;
                }
                else if (_rows.Count == 0)
                {
                    StatusText.Text = "This folder is empty.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not list folder: " + ex.Message;
            }
            finally
            {
                ShowBusy(false);
            }
        }

        // ---- interaction ---------------------------------------------------

        private async void OnRowTapped(object sender, TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return;
            }

            string path = element.Tag as string;
            FileRow row;
            if (string.IsNullOrEmpty(path) || !_byPath.TryGetValue(path, out row))
            {
                return;
            }

            if (!row.IsFolder)
            {
                // Sending files is the next capability to wire up; say so rather
                // than silently doing nothing.
                StatusText.Text = "File transfer is not connected yet. Browsing only.";
                return;
            }

            try
            {
                StorageFolder folder = await _browser.OpenAsync(row.Path);
                if (folder == null)
                {
                    StatusText.Text = "Could not open " + row.Name + ".";
                    return;
                }
                _stack.Add(folder);
                await ShowFolderAsync(folder);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open " + row.Name + ": " + ex.Message;
            }
        }

        private async void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (_stack.Count == 0)
            {
                return;
            }

            _stack.RemoveAt(_stack.Count - 1);
            if (_stack.Count == 0)
            {
                ShowRoots();
                return;
            }
            await ShowFolderAsync(_stack[_stack.Count - 1]);
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (_stack.Count == 0)
            {
                ShowRoots();
                return;
            }
            Task ignored = ShowFolderAsync(_stack[_stack.Count - 1]);
        }

        private void OnRootsClick(object sender, RoutedEventArgs e)
        {
            ShowRoots();
        }

        private async void OnSdClick(object sender, RoutedEventArgs e)
        {
            ShowBusy(true);
            StatusText.Text = "Looking for the SD card...";
            try
            {
                StorageRootsResult result = await _browser.GetRootsAsync();
                for (int i = 0; i < result.Roots.Count; i++)
                {
                    StorageRoot root = result.Roots[i];
                    if (!string.Equals(root.Note, "removable storage", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    StorageFolder folder = await _browser.OpenAsync(root.Path);
                    if (folder == null)
                    {
                        break;
                    }
                    _stack.Clear();
                    _stack.Add(folder);
                    await ShowFolderAsync(folder);
                    return;
                }

                StatusText.Text = result.Notes.Count > 0
                    ? string.Join(Environment.NewLine, result.Notes.ToArray())
                    : "No SD card is mounted.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open the SD card: " + ex.Message;
            }
            finally
            {
                ShowBusy(false);
            }
        }
    }
}
