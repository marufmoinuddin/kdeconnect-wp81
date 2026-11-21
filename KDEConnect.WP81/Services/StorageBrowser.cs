using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KDEConnect.WP81.Core;
using Windows.Storage;

namespace KDEConnect.WP81.Services
{
    /// <summary>What the browser could enumerate, and what it could not.</summary>
    public sealed class StorageRootsResult
    {
        public List<StorageRoot> Roots = new List<StorageRoot>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Enumerates the places a user can pick files from on this platform.
    /// </summary>
    /// <remarks>
    /// The SD card is the reason this class exists. On Windows Phone 8.1 the card
    /// is only visible through KnownFolders.RemovableDevices *and* only when the
    /// package declares the removableStorage capability; without the declaration
    /// every access throws UnauthorizedAccessException and the card looks absent.
    /// Third-party file managers get this right by declaring it, and their
    /// presence on the card is not evidence that any app can see it.
    ///
    /// Each source is probed independently: a media library the app has no
    /// capability for is skipped with a note rather than taking the page down.
    /// </remarks>
    public sealed class StorageBrowser
    {
        /// <summary>Never allocate more than this per directory listing.</summary>
        public const int MaxEntriesPerFolder = 500;

        /// <summary>Stop asking for file properties past this count.</summary>
        public const int SizeLookupLimit = 120;

        public async Task<StorageRootsResult> GetRootsAsync()
        {
            StorageRootsResult result = new StorageRootsResult();

            try
            {
                StorageFolder local = ApplicationData.Current.LocalFolder;
                result.Roots.Add(new StorageRoot
                {
                    Name = "App storage",
                    Path = local.Path,
                    Note = "this app's own folder"
                });
            }
            catch (Exception ex)
            {
                result.Notes.Add("App storage unavailable: " + ex.Message);
            }

            // The SD card. RemovableDevices is a container whose children are the
            // cards themselves.
            try
            {
                StorageFolder removable = KnownFolders.RemovableDevices;
                IReadOnlyList<StorageFolder> cards = await removable.GetFoldersAsync();
                if (cards == null || cards.Count == 0)
                {
                    result.Notes.Add("No SD card is mounted.");
                }
                else
                {
                    for (int i = 0; i < cards.Count; i++)
                    {
                        StorageFolder card = cards[i];
                        result.Roots.Add(new StorageRoot
                        {
                            Name = string.IsNullOrEmpty(card.DisplayName)
                                ? "SD card" : card.DisplayName,
                            Path = card.Path,
                            Note = "removable storage"
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                result.Notes.Add(
                    "SD card is not accessible: the package does not declare the " +
                    "removableStorage capability.");
            }
            catch (Exception ex)
            {
                result.Notes.Add("SD card unavailable: " + ex.Message);
            }

            await AddLibraryAsync(result, "Pictures", delegate
            {
                return KnownFolders.PicturesLibrary;
            });

            await AddLibraryAsync(result, "Videos", delegate
            {
                return KnownFolders.VideosLibrary;
            });

            await AddLibraryAsync(result, "Music", delegate
            {
                return KnownFolders.MusicLibrary;
            });

            return result;
        }

        private static async Task AddLibraryAsync(
            StorageRootsResult result, string label, Func<StorageFolder> open)
        {
            try
            {
                StorageFolder folder = open();
                if (folder == null)
                {
                    return;
                }

                // Touching the folder is what proves the capability, not the
                // object being non-null.
                string path = folder.Path;
                result.Roots.Add(new StorageRoot
                {
                    Name = label,
                    Path = path,
                    Note = "media library"
                });
            }
            catch (UnauthorizedAccessException)
            {
                result.Notes.Add(label + " library needs a capability this app does not declare.");
            }
            catch (Exception ex)
            {
                result.Notes.Add(label + " library unavailable: " + ex.Message);
            }
            await Task.FromResult(0);
        }

        /// <summary>Lists a folder, or explains why it could not be listed.</summary>
        public async Task<StorageListingResult> ListAsync(StorageFolder folder)
        {
            StorageListingResult result = new StorageListingResult();
            if (folder == null)
            {
                result.Problem = "No folder selected.";
                return result;
            }

            try
            {
                IReadOnlyList<StorageFolder> folders = await folder.GetFoldersAsync();
                if (folders != null)
                {
                    for (int i = 0; i < folders.Count && result.Entries.Count < MaxEntriesPerFolder; i++)
                    {
                        StorageFolder child = folders[i];
                        try
                        {
                            result.Entries.Add(new StorageEntry
                            {
                                Name = child.Name,
                                Path = child.Path,
                                IsFolder = true,
                                Modified = child.DateCreated
                            });
                        }
                        catch (Exception)
                        {
                            // A single protected folder must not end the listing.
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                result.Problem = "This folder is protected and cannot be listed.";
                return result;
            }
            catch (Exception ex)
            {
                result.Problem = "Could not list folders: " + ex.Message;
                return result;
            }

            try
            {
                IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
                if (files != null)
                {
                    bool wantSizes = files.Count <= SizeLookupLimit;
                    for (int i = 0; i < files.Count && result.Entries.Count < MaxEntriesPerFolder; i++)
                    {
                        StorageFile file = files[i];
                        StorageEntry entry = new StorageEntry();
                        try
                        {
                            entry.Name = file.Name;
                            entry.Path = file.Path;
                            entry.IsFolder = false;

                            if (wantSizes)
                            {
                                try
                                {
                                    var properties = await file.GetBasicPropertiesAsync();
                                    entry.SizeBytes = (long)properties.Size;
                                    entry.Modified = properties.DateModified;
                                }
                                catch (Exception)
                                {
                                    // Size is a nicety; the row still lists.
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            entry.Name = "(unreadable)";
                            entry.Problem = ex.Message;
                        }
                        result.Entries.Add(entry);
                    }

                    if (!wantSizes)
                    {
                        result.Problem = "Large folder: sizes are hidden for the first " +
                                         MaxEntriesPerFolder.ToString() + " items.";
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                result.Problem = "Files in this folder are protected.";
            }
            catch (Exception ex)
            {
                result.Problem = "Could not list files: " + ex.Message;
            }

            result.Entries = StorageListing.Order(result.Entries);
            return result;
        }

        /// <summary>Opens a folder from a path previously returned by a listing.</summary>
        public async Task<StorageFolder> OpenAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return await StorageFolder.GetFolderFromPathAsync(path);
        }
    }

    /// <summary>Entries plus anything the caller should say about them.</summary>
    public sealed class StorageListingResult
    {
        public List<StorageEntry> Entries = new List<StorageEntry>();
        public string Problem;
    }
}
