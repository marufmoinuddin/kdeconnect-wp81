using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Plugins;

namespace KDEConnect.WP81
{
    public sealed partial class App : Application
    {
        private TransitionCollection _transitions;

        public DeviceManager DeviceManager { get; private set; }
        public LinkManager LinkManager { get; private set; }
        public PluginManager PluginManager { get; private set; }
        public DeviceStore DeviceStore { get; private set; }

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;

            InitializeServices();
        }

        private void InitializeServices()
        {
            var store = new StorageBlobStore();
            DeviceStore = new DeviceStore(store);

            TlsIdentity identity = null;
            var ownIdentity = DeviceStore.LoadOrCreateIdentity(Sanitize.GenerateDeviceId(), "WP81");
            if (ownIdentity != null)
            {
                identity = new TlsIdentity(ownIdentity.CertificateDer, ownIdentity.PrivateKeyDer);
            }

            var localInfo = new DeviceInfo(
                ownIdentity?.DeviceId ?? Sanitize.GenerateDeviceId(),
                ownIdentity?.DeviceName ?? "WP81",
                DeviceKind.Phone,
                ProtocolConstants.ProtocolVersion);
            localInfo.IncomingCapabilities.AddRange(Capabilities.IncomingList());
            localInfo.OutgoingCapabilities.AddRange(Capabilities.OutgoingList());

            LinkManager = new LinkManager(identity, localInfo);
            PluginManager = new PluginManager(LinkManager);
            PluginManager.RegisterPlugin(new PingPlugin());

            DeviceManager = new DeviceManager(DeviceStore, LinkManager, PluginManager);
            DeviceManager.OwnIdentityChanged += (s, e) => { /* UI will refresh via event */ };
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

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                }

                Window.Current.Content = rootFrame;
            }

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

            await InitializeAsync();

            Window.Current.Activate();
        }

        private async Task InitializeAsync()
        {
            await DeviceManager.InitializeOwnIdentityAsync("WP81");
            await LinkManager.StartListeningAsync();
            await DeviceManager.StartDiscoveryAsync();
        }

        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            var rootFrame = sender as Frame;
            rootFrame.ContentTransitions = _transitions ?? new TransitionCollection() { new NavigationThemeTransition() };
            rootFrame.Navigated -= RootFrame_FirstNavigated;
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            DeviceManager.StopDiscovery();
            LinkManager.StopListening();
            LinkManager.Dispose();

            deferral.Complete();
        }

        private async void OnResuming(object sender, object e)
        {
            await InitializeAsync();
        }
    }
}