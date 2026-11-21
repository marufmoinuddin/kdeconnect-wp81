using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// WinRT implementation of <see cref="IBlobStore"/> backed by
    /// <c>ApplicationData.Current.LocalFolder</c>. This is the only place in
    /// the app that knows about the WinRT storage API; Core stays portable.
    ///
    /// Threading: <see cref="IBlobStore"/> is synchronous while every WinRT
    /// storage call is async. Blocking a WinRT operation with
    /// <c>GetAwaiter().GetResult()</c> on the UI thread can deadlock, because
    /// the continuation needs the dispatcher that is being blocked. Each
    /// operation therefore runs on a pool thread via Task.Run, where there is
    /// no dispatcher to wait for.
    /// </summary>
    public sealed class StorageBlobStore : IBlobStore
    {
        private readonly StorageFolder _folder;

        public StorageBlobStore()
        {
            _folder = ApplicationData.Current.LocalFolder;
        }

        public byte[] Read(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            return RunSync(delegate
            {
                try
                {
                    IStorageFile file = _folder.GetFileAsync(name).AsTask().GetAwaiter().GetResult();
                    if (file == null)
                    {
                        return (byte[])null;
                    }
                    IBuffer buffer = FileIO.ReadBufferAsync(file).AsTask().GetAwaiter().GetResult();
                    if (buffer == null || buffer.Length == 0)
                    {
                        return new byte[0];
                    }

                    byte[] bytes = new byte[buffer.Length];
                    using (DataReader reader = DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }
                    return bytes;
                }
                catch (FileNotFoundException)
                {
                    return (byte[])null;
                }
                catch (Exception)
                {
                    // Any other storage error (corrupt file, access denied)
                    // surfaces as "not found", matching the MemoryBlobStore
                    // null contract; the caller then regenerates.
                    return (byte[])null;
                }
            });
        }

        public void Write(string name, byte[] data)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException("name");
            }
            if (data == null)
            {
                data = new byte[0];
            }

            byte[] payload = data;
            RunSync(delegate
            {
                try
                {
                    IStorageFile file = _folder.CreateFileAsync(
                        name, CreationCollisionOption.ReplaceExisting)
                        .AsTask().GetAwaiter().GetResult();
                    FileIO.WriteBytesAsync(file, payload).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // Reported, not swallowed: silently losing the identity
                    // would look like a spontaneous unpair on next launch.
                    throw new IOException("Failed to write " + name, ex);
                }
            });
        }

        public void Delete(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            RunSync(delegate
            {
                try
                {
                    IStorageFile file = _folder.GetFileAsync(name).AsTask().GetAwaiter().GetResult();
                    if (file != null)
                    {
                        file.DeleteAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                catch (FileNotFoundException)
                {
                    // Already gone - the desired end state.
                }
                catch (Exception)
                {
                }
            });
        }

        public bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return RunSync(delegate
            {
                try
                {
                    IStorageFile file = _folder.GetFileAsync(name).AsTask().GetAwaiter().GetResult();
                    return file != null;
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Runs a blocking body on a pool thread. See the class comment for why
        /// the blocking wait must not happen on the dispatcher thread.
        /// </summary>
        private static T RunSync<T>(Func<T> body)
        {
            return Task.Run(body).GetAwaiter().GetResult();
        }

        private static void RunSync(Action body)
        {
            Task.Run(body).GetAwaiter().GetResult();
        }
    }
}
