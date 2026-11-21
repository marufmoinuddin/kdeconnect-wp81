using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Security;
using KDEConnect.WP81.Plugins;
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

        /// <summary>Set when startup failed, so the UI can explain why.</summary>
        public string StartupError { get; private set; }

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                this.DebugSettings.EnableFrameRateCounter = true;
            }
#endif

            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.CacheSize = 1;
                Window.Current.Content = rootFrame;
            }

            // Services must exist before the first page is constructed: the
            // device list page resolves DeviceManager in its constructor.
            await InitializeServicesAsync();

            if (rootFrame.Content == null)
            {
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

                if (!rootFrame.Navigate(typeof(Pages.DeviceListPage), e.Arguments))
                {
                    throw new Exception("Failed to create initial page");
                }
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// Builds the service graph in dependency order. The identity is the
        /// root: nothing can be advertised or connected without it, and the
        /// certificate generation is slow enough that it must not run on the
        /// dispatcher thread.
        /// </summary>
        private async Task InitializeServicesAsync()
        {
            try
            {
                DeviceStore = new DeviceStore(new StorageBlobStore());

                DeviceStore.OwnIdentity own = DeviceStore.LoadIdentity();
                if (own == null)
                {
                    // First run, or an identity file we refuse to trust. The id
                    // minted here must then be reused forever, which is exactly
                    // what DeviceStore.LoadIdentity guarantees on later runs.
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
                PluginManager.RegisterPlugin(new PingPlugin());

                localInfo.IncomingCapabilities.AddRange(PluginManager.GetIncomingCapabilities());
                localInfo.OutgoingCapabilities.AddRange(PluginManager.GetOutgoingCapabilities());

                DeviceManager = new DeviceManager(DeviceStore, LinkManager, PluginManager);
                DeviceManager.SetOwnIdentity(own);

                await LinkManager.StartAsync();
                await DeviceManager.StartDiscoveryAsync();

                StartupError = null;
            }
            catch (Exception ex)
            {
                StartupError = ex.Message;
                if (DeviceManager != null)
                {
                    DeviceManager.StopDiscovery();
                }
                throw;
            }
        }

        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            var rootFrame = sender as Frame;
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
                StartupError = ex.Message;
            }
        }
    }
}
