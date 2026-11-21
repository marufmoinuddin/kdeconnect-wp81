using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// WinRT implementation of <see cref="IBlobStore"/> backed by
    /// <c>ApplicationData.Current.LocalFolder</c>. This is the only place in
    /// the app that knows about the WinRT storage API; Core stays portable.
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
            try
            {
                IStorageFile file = _folder.GetFileAsync(name).AsTask().GetAwaiter().GetResult();
                if (file == null)
                {
                    return null;
                }
                IBuffer buffer = FileIO.ReadBufferAsync(file).AsTask().GetAwaiter().GetResult();
                if (buffer == null || buffer.Length == 0)
                {
                    return new byte[0];
                }
                byte[] bytes = new byte[buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(bytes);
                return bytes;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception)
            {
                // Any other storage error (corrupt file, access denied) surfaces
                // as "not found" to match the MemoryBlobStore null return
                // contract; the caller treats it as "regenerate / start fresh".
                return null;
            }
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

            try
            {
                IStorageFile file = _folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting).AsTask().GetAwaiter().GetResult();

                IBuffer buffer = data.AsBuffer();
                FileIO.WriteBufferAsync(file, buffer).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Storage failures on the phone are fatal for persistence.
                // We re-throw so the caller can decide whether to fall back
                // to in-memory operation rather than silently lose data.
                throw new IOException("Failed to write " + name, ex);
            }
        }

        public void Delete(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
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
                // Already gone — that's the desired end state.
            }
            catch (Exception)
            {
                // Ignore other errors; the file is effectively gone.
            }
        }

        public bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            try
            {
                IStorageFile file = _folder.GetFileAsync(name).AsTask().GetAwaiter().GetResult();
                return file != null;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}