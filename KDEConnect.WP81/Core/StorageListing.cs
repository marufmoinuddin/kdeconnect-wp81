using System;
using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    /// <summary>One row in a file listing. Deliberately storage-API free.</summary>
    public sealed class StorageEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public long SizeBytes { get; set; }
        public DateTimeOffset Modified { get; set; }

        /// <summary>Why this row could not be read, if it could not.</summary>
        public string Problem { get; set; }
    }

    /// <summary>A browsable root: SD card, app storage, a media library.</summary>
    public sealed class StorageRoot
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Presentation rules for file listings, kept portable so they can be tested
    /// without a device. Ordering and size formatting are the two things a file
    /// browser gets wrong in a way users notice immediately.
    /// </summary>
    public static class StorageListing
    {
        /// <summary>Folders first, then files; each group by name, case-insensitive.</summary>
        public static List<StorageEntry> Order(IEnumerable<StorageEntry> entries)
        {
            List<StorageEntry> list = new List<StorageEntry>();
            if (entries != null)
            {
                foreach (StorageEntry e in entries)
                {
                    if (e != null)
                    {
                        list.Add(e);
                    }
                }
            }

            list.Sort(delegate(StorageEntry a, StorageEntry b)
            {
                if (a.IsFolder != b.IsFolder)
                {
                    return a.IsFolder ? -1 : 1;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// Human size. Uses 1024-based units because this is storage, and rounds
        /// to one decimal only where it helps ("1.4 GB", not "1.437 GB").
        /// </summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0)
            {
                return "?";
            }
            if (bytes < 1024)
            {
                return bytes.ToString() + " B";
            }

            double value = bytes;
            string[] units = new string[] { "KB", "MB", "GB", "TB" };
            for (int i = 0; i < units.Length; i++)
            {
                value = value / 1024.0;
                if (value < 1024.0 || i == units.Length - 1)
                {
                    string format = value < 10.0 ? "F1" : "F0";
                    return value.ToString(format) + " " + units[i];
                }
            }
            return bytes.ToString() + " B";
        }

        /// <summary>The grey second line under a file or folder name.</summary>
        public static string Subtitle(StorageEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(entry.Problem))
            {
                return entry.Problem;
            }
            if (entry.IsFolder)
            {
                return "folder";
            }

            string size = FormatSize(entry.SizeBytes);
            if (entry.Modified == default(DateTimeOffset))
            {
                return size;
            }
            return size + "  -  " + entry.Modified.LocalDateTime.ToString("d MMM yyyy HH:mm");
        }

        /// <summary>
        /// A path that is safe to show. Storage paths on the phone can contain
        /// the card's volume GUID, which is noise in a header.
        /// </summary>
        public static string DisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "\\";
            }
            return path;
        }
    }
}
