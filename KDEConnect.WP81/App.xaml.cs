using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Plugins;
using KDEConnect.WP81.Security;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81
{
    public sealed partial class App : Application
    {
        private TransitionCollection _transitions;

        public DeviceStore DeviceStore { get; private set; }
        public DeviceManager DeviceManager { get; private set; }
        public LinkManager LinkManager { get; private set; }
        public PluginManager PluginManager { get; private set; }

        /// <summary>Name we present to peers; editable in Settings.</summary>
        public const string DefaultDeviceName = "WP81";

        /// <summary>Fatal startup failure; null when the app could start.</summary>
        public string StartupError { get; private set; }

        /// <summary>Non-fatal problems (a socket that could not bind, etc.).</summary>
        public List<string> StartupWarnings { get; private set; }

        /// <summary>Crash recorded by the previous run, if any.</summary>
        public string LastCrash { get; private set; }

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
            this.UnhandledException += OnUnhandledException;
            this.StartupWarnings = new List<string>();
        }

        /// <summary>
        /// Records anything that escapes the normal paths. The exception is
        /// persisted before the process dies so the NEXT launch can display it;
        /// this is the only way to diagnose a crash on a device with no
        /// console and no debugger attached.
        /// </summary>
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            CrashLog.Record("UnhandledException", e.Exception);
            StartupError = e.Exception != null ? e.Exception.Message : "unhandled error";
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = null;
            try
            {
                LastCrash = CrashLog.Read();

#if DEBUG
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    this.DebugSettings.EnableFrameRateCounter = true;
                }
#endif
                rootFrame = Window.Current.Content as Frame;
                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    rootFrame.CacheSize = 1;
                    Window.Current.Content = rootFrame;
                }

                // Services must exist before the first page is constructed: the
                // device list page resolves DeviceManager in its constructor.
                await InitializeServicesAsync();

                NavigateSafely(rootFrame, e.Arguments);
                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                // Nothing may escape an async void handler: an unhandled
                // exception there terminates the process with no message.
                CrashLog.Record("OnLaunched", ex);
                if (StartupError == null)
                {
                    StartupError = ex.Message;
                }
                ShowFallbackPage(rootFrame);
                Window.Current.Activate();
            }
        }

        private void NavigateSafely(Frame rootFrame, object arguments)
        {
            if (rootFrame == null)
            {
                return;
            }
            try
            {
                if (rootFrame.Content != null)
                {
                    return;
                }

                if (rootFrame.ContentTransitions != null)
                {
                    _transitions = new TransitionCollection();
                    foreach (var c in rootFrame.ContentTransitions)
                    {
                        _transitions.Add(c);
                    }
                }

                rootFrame.ContentTransitions = null;
                rootFrame.Navigated += RootFrame_FirstNavigated;

                if (!rootFrame.Navigate(typeof(Pages.DeviceListPage), arguments))
                {
                    throw new InvalidOperationException("Frame.Navigate returned false for DeviceListPage");
                }
            }
            catch (Exception ex)
            {
                // A page that fails to load (missing theme resource, bad
                // binding, XAML parse error) must not leave a black screen.
                CrashLog.Record("page load failed", ex);
                StartupError = ex.Message;
                ShowFallbackPage(rootFrame);
            }
        }

        /// <summary>
        /// A plain, code-built screen with no XAML resources and no bindings,
        /// so it cannot fail for the same reason the real page did.
        /// </summary>
        private void ShowFallbackPage(Frame failedFrame)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("KDE Connect could not start.");
                sb.AppendLine();
                sb.AppendLine(GetDiagnosticsSummary());

                TextBlock text = new TextBlock();
                text.Text = sb.ToString();
                text.TextWrapping = TextWrapping.Wrap;
                text.FontSize = 14;
                text.Margin = new Thickness(16);
                text.FontFamily = new Windows.UI.Xaml.Media.FontFamily("Consolas");

                ScrollViewer scroll = new ScrollViewer();
                scroll.Content = text;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

                Button retry = new Button();
                retry.Content = "Try again";
                retry.Margin = new Thickness(16, 0, 16, 16);
                // RequestRestartAsync does not exist on WP8.1; closing the app
                // is the supported way to get a clean relaunch by the user.
                retry.Click += delegate
                {
                    try
                    {
                        Application.Current.Exit();
                    }
                    catch (Exception)
                    {
                    }
                };

                StackPanel panel = new StackPanel();
                panel.Children.Add(scroll);
                panel.Children.Add(retry);

                Window.Current.Content = panel;
            }
            catch (Exception)
            {
                // If even this fails there is nothing left to try.
            }
        }

        /// <summary>Everything the UI should show about startup problems.</summary>
        public string GetDiagnosticsSummary()
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(StartupError))
            {
                sb.AppendLine("Error: " + StartupError);
                sb.AppendLine();
            }
            if (StartupWarnings != null && StartupWarnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < StartupWarnings.Count; i++)
                {
                    sb.AppendLine("  - " + StartupWarnings[i]);
                }
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(LastCrash))
            {
                sb.AppendLine("Previous run's crash log:");
                sb.AppendLine(LastCrash);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Builds the service graph in dependency order. The identity is the
        /// root: nothing can be advertised or connected without it, and the
        /// certificate generation is slow enough that it must not run on the
        /// dispatcher thread.
        ///
        /// Only the identity is fatal. A socket that will not bind is reported
        /// and skipped, so a blocked port degrades the app instead of killing it.
        /// </summary>
        private async Task InitializeServicesAsync()
        {
            DeviceStore.OwnIdentity own;
            try
            {
                DeviceStore = new DeviceStore(new StorageBlobStore());

                own = DeviceStore.LoadIdentity();
                if (own == null)
                {
                    string newId = Sanitize.GenerateDeviceId();
                    TlsIdentity minted = await Task.Run(delegate
                    {
                        return CertificateProvider.CreateIdentity(newId);
                    });

                    own = new DeviceStore.OwnIdentity();
                    own.DeviceId = newId;
                    own.DeviceName = DefaultDeviceName;
                    own.CertificateDer = minted.CertificateDer;
                    own.PrivateKeyDer = minted.PrivateKeyDer;
                    DeviceStore.SaveIdentity(own);
                }
            }
            catch (Exception ex)
            {
                // Without an identity nothing else is possible.
                CrashLog.Record("identity init", ex);
                StartupError = "Identity: " + ex.Message;
                return;
            }

            try
            {
                TlsIdentity identity = new TlsIdentity(
                    own.DeviceId, own.CertificateDer, own.PrivateKeyDer);

                DeviceInfo localInfo = new DeviceInfo(
                    own.DeviceId,
                    string.IsNullOrEmpty(own.DeviceName) ? DefaultDeviceName : own.DeviceName,
                    DeviceKind.Phone,
                    ProtocolConstants.ProtocolVersion);

                LinkManager = new LinkManager(identity, localInfo);
                PluginManager = new PluginManager(LinkManager);

                // Only implemented plugins contribute capabilities, so a peer is
                // never told about something this build cannot actually do.
                // Adding a plugin here is what makes its capability appear on the
                // wire - there is no separate capability table to keep in sync.
                PluginManager.RegisterPlugin(new PingPlugin());
                PluginManager.RegisterPlugin(new BatteryPlugin(new WinRtBatterySource()));
                PluginManager.RegisterPlugin(new ClipboardPlugin());
                PluginManager.RegisterPlugin(new MprisPlugin());
                PluginManager.RegisterPlugin(new MousepadPlugin());
                PluginManager.RegisterPlugin(new PresenterPlugin());
                PluginManager.RegisterPlugin(new SystemVolumePlugin());
                PluginManager.RegisterPlugin(new FindMyPhonePlugin());

                localInfo.IncomingCapabilities.AddRange(PluginManager.GetIncomingCapabilities());
                localInfo.OutgoingCapabilities.AddRange(PluginManager.GetOutgoingCapabilities());

                DeviceManager = new DeviceManager(DeviceStore, LinkManager, PluginManager);
                DeviceManager.SetOwnIdentity(own);
            }
            catch (Exception ex)
            {
                CrashLog.Record("service init", ex);
                StartupError = "Services: " + ex.Message;
                return;
            }

            // Non-fatal: TCP 1716 may be blocked by the platform.
            try
            {
                await LinkManager.StartAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Record("tcp listener", ex);
                StartupWarnings.Add("TCP listener on 1716 failed (" + ex.Message +
                                    ") - inbound connections will not work.");
            }

            // Non-fatal: UDP 1716 broadcast/bind may be blocked or unsupported.
            try
            {
                await DeviceManager.StartDiscoveryAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Record("udp discovery", ex);
                StartupWarnings.Add("UDP discovery on 1716 failed (" + ex.Message +
                                    ") - use Add by IP instead.");
            }
        }

        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            var rootFrame = sender as Frame;
            if (rootFrame == null)
            {
                return;
            }
            rootFrame.ContentTransitions = _transitions ?? new TransitionCollection() { new NavigationThemeTransition() };
            rootFrame.Navigated -= RootFrame_FirstNavigated;
        }

        /// <summary>
        /// Windows Phone gives no background execution, so sockets die on
        /// suspend (docs/known-limitations.md L2). Everything is torn down
        /// cleanly and rebuilt on resume rather than left half-open.
        /// </summary>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (DeviceManager != null)
                {
                    DeviceManager.StopDiscovery();
                }
                if (LinkManager != null)
                {
                    LinkManager.Stop();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Record("suspend", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            try
            {
                if (LinkManager != null)
                {
                    await LinkManager.StartAsync();
                }
                if (DeviceManager != null)
                {
                    await DeviceManager.StartDiscoveryAsync();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Record("resume", ex);
                StartupWarnings.Add("Resume: " + ex.Message);
            }
        }
    }
}
