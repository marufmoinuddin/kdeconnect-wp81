using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Storage;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Last-resort crash record.
    ///
    /// A Windows Phone app that dies during startup takes its error message
    /// with it: there is no console to read and the process is gone before a
    /// dialog could be shown. Writing the exception to LocalFolder means the
    /// next launch can display what killed the previous one, which is the only
    /// practical way to diagnose a field crash without a debugger attached.
    ///
    /// Every method swallows its own failures: a logger that throws while
    /// reporting a crash is worse than no logger.
    /// </summary>
    public static class CrashLog
    {
        private const string FileName = "crash.log";
        private const int MaxBytes = 16 * 1024;

        public static void Record(string message)
        {
            string line = DateTime.UtcNow.ToString("u") + "Z  " + (message ?? "(null)") + Environment.NewLine;

            // Also visible in the Visual Studio Output window when debugging.
            System.Diagnostics.Debug.WriteLine("[KDEConnect] " + line.Trim());

            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = folder.CreateFileAsync(
                    FileName, CreationCollisionOption.OpenIfExists).AsTask().GetAwaiter().GetResult();

                string existing = string.Empty;
                try
                {
                    existing = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }

                string combined = existing + line;
                if (combined.Length > MaxBytes)
                {
                    // Keep the tail: the most recent crash is the interesting one.
                    combined = combined.Substring(combined.Length - MaxBytes);
                }

                FileIO.WriteTextAsync(file, combined).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        public static void Record(string context, Exception ex)
        {
            if (ex == null)
            {
                Record(context);
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(context);
            sb.Append(" -> ");
            sb.Append(ex.GetType().Name);
            sb.Append(": ");
            sb.Append(ex.Message);
            if (ex.InnerException != null)
            {
                sb.Append(" | inner ");
                sb.Append(ex.InnerException.GetType().Name);
                sb.Append(": ");
                sb.Append(ex.InnerException.Message);
            }
            Record(sb.ToString());
        }

        /// <summary>Reads the log, or returns null when nothing was ever recorded.</summary>
        public static string Read()
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = folder.GetFileAsync(FileName).AsTask().GetAwaiter().GetResult();
                if (file == null)
                {
                    return null;
                }
                string text = FileIO.ReadTextAsync(file).AsTask().GetAwaiter().GetResult();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Clear()
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile file = folder.GetFileAsync(FileName).AsTask().GetAwaiter().GetResult();
                if (file != null)
                {
                    file.DeleteAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
