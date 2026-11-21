using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Field diagnostics.
    ///
    /// A Windows Phone app that dies during startup takes its error message with
    /// it: no console, and the process is gone before a dialog could appear.
    /// Two files in LocalFolder are the only durable evidence available:
    ///
    ///   crash.log    exceptions that escaped
    ///   startup.log  breadcrumbs, one line per startup step
    ///
    /// The breadcrumbs exist because "no crash log at all" is ambiguous: it can
    /// mean a native failure, or a termination for taking too long, or a
    /// deadlock inside this very writer. With breadcrumbs the last line names the
    /// step that was running when the app died.
    ///
    /// Every method swallows its own failures, and every wait is bounded.
    /// Blocking an unbounded <c>GetResult()</c> on the dispatcher thread can
    /// deadlock, and a logger that hangs while reporting a crash is worse than
    /// no logger at all.
    /// </summary>
    public static class CrashLog
    {
        private const string CrashFileName = "crash.log";
        private const string StartupFileName = "startup.log";
        private const int MaxChars = 16 * 1024;
        private const int WaitMs = 2000;

        // ---- crash records --------------------------------------------------

        public static void Record(string message)
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z  " +
                          (message ?? "(null)") + Environment.NewLine;
            System.Diagnostics.Debug.WriteLine("[KDEConnect] " + line.Trim());
            Append(CrashFileName, line);
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

        // ---- startup breadcrumbs --------------------------------------------

        /// <summary>
        /// Records that a startup step was reached. Cheap, bounded, never throws.
        /// The last breadcrumb written before a silent death is the answer to
        /// "where did it die".
        /// </summary>
        public static void Trace(string step)
        {
            string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + (step ?? "?") +
                          Environment.NewLine;
            System.Diagnostics.Debug.WriteLine("[startup] " + line.Trim());
            Append(StartupFileName, line);
        }

        public static string ReadStartupTrace()
        {
            return ReadAll(StartupFileName);
        }

        public static string Read()
        {
            return ReadAll(CrashFileName);
        }

        // ---- storage --------------------------------------------------------

        private static void Append(string fileName, string line)
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;

                Task<StorageFile> createTask = folder.CreateFileAsync(
                    fileName, CreationCollisionOption.OpenIfExists).AsTask();
                if (!createTask.Wait(WaitMs) || createTask.Result == null)
                {
                    return;
                }
                StorageFile file = createTask.Result;

                string existing = string.Empty;
                try
                {
                    Task<string> readTask = FileIO.ReadTextAsync(file).AsTask();
                    if (readTask.Wait(WaitMs))
                    {
                        existing = readTask.Result ?? string.Empty;
                    }
                }
                catch (Exception)
                {
                }

                string combined = existing + line;
                if (combined.Length > MaxChars)
                {
                    combined = combined.Substring(combined.Length - MaxChars);
                }

                Task writeTask = FileIO.WriteTextAsync(file, combined).AsTask();
                writeTask.Wait(WaitMs);
            }
            catch (Exception)
            {
            }
        }

        private static string ReadAll(string fileName)
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;

                Task<StorageFile> getTask = folder.GetFileAsync(fileName).AsTask();
                if (!getTask.Wait(WaitMs) || getTask.Result == null)
                {
                    return null;
                }

                Task<string> readTask = FileIO.ReadTextAsync(getTask.Result).AsTask();
                if (!readTask.Wait(WaitMs))
                {
                    return null;
                }
                string text = readTask.Result;
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Clear()
        {
            DeleteFile(CrashFileName);
            DeleteFile(StartupFileName);
        }

        private static void DeleteFile(string fileName)
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                Task<StorageFile> getTask = folder.GetFileAsync(fileName).AsTask();
                if (!getTask.Wait(WaitMs) || getTask.Result == null)
                {
                    return;
                }
                getTask.Result.DeleteAsync().AsTask().Wait(WaitMs);
            }
            catch (Exception)
            {
            }
        }
    }
}
