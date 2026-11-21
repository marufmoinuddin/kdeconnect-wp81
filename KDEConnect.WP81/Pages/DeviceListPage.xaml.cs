using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Popups;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    /// <summary>
    /// View-model for a device shown in the list. Mirrors DeviceRecord but adds
    /// UI-friendly computed properties (status text, color, capability badges).
    /// </summary>
    public sealed class DeviceListItem
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public bool IsPaired { get; set; }
        public bool IsConnected { get; set; }
        public bool IsConnecting { get; set; }
        public string LastSeenAddress { get; set; }
        public int LastSeenTcpPort { get; set; }
        public List<string> IncomingCapabilities { get; set; }
        public List<string> OutgoingCapabilities { get; set; }
        public byte[] PinnedCertificateDer { get; set; }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(DeviceName) ? DeviceId : DeviceName; }
        }

        public string StatusText
        {
            get
            {
                if (IsConnecting) return "Connecting...";
                if (IsConnected) return "Connected";
                if (IsPaired) return "Paired";
                return "Discovered";
            }
        }

        public Brush StatusColor
        {
            get
            {
                if (IsConnecting) return new SolidColorBrush(Windows.UI.Colors.Orange);
                if (IsConnected) return new SolidColorBrush(Windows.UI.Colors.Green);
                if (IsPaired) return new SolidColorBrush(Windows.UI.Colors.Blue);
                return new SolidColorBrush(Windows.UI.Colors.Gray);
            }
        }

        /// <summary>
        /// One string for the second line. Composing it here keeps the XAML to
        /// plain Text bindings - no converter, no brush, nothing that can fail
        /// while the template is being realised.
        /// </summary>
        public string Subtitle
        {
            get
            {
                string text = DeviceId;
                string status = StatusText;
                if (!string.IsNullOrEmpty(status))
                {
                    text += "  -  " + status;
                }
                string caps = CapabilitySummary();
                if (caps.Length > 0)
                {
                    text += "  -  " + caps;
                }
                return text;
            }
        }

        private string CapabilitySummary()
        {
            StringBuilder sb = new StringBuilder();
            Append(sb, IncomingCapabilities, "kdeconnect.ping", "ping");
            Append(sb, IncomingCapabilities, "kdeconnect.battery", "battery");
            Append(sb, IncomingCapabilities, "kdeconnect.mpris", "media");
            Append(sb, IncomingCapabilities, "kdeconnect.mousepad.request", "touchpad");
            Append(sb, IncomingCapabilities, "kdeconnect.systemvolume", "volume");
            Append(sb, IncomingCapabilities, "kdeconnect.share.request", "files");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, List<string> caps, string type, string label)
        {
            if (caps == null)
            {
                return;
            }
            for (int i = 0; i < caps.Count; i++)
            {
                if (string.Equals(caps[i], type, StringComparison.Ordinal))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(label);
                    return;
                }
            }
        }

        public bool HasPing { get { return IncomingCapabilities != null && IncomingCapabilities.Contains("kdeconnect.ping"); } }
        public bool HasBattery { get { return IncomingCapabilities != null && IncomingCapabilities.Contains("kdeconnect.battery"); } }
        public bool HasFileTransfer { get { return IncomingCapabilities != null && IncomingCapabilities.Contains("kdeconnect.file"); } }
        public bool HasMpris { get { return IncomingCapabilities != null && IncomingCapabilities.Contains("kdeconnect.mpris.request"); } }
        public bool HasMousepad { get { return IncomingCapabilities != null && IncomingCapabilities.Contains("kdeconnect.mousepad"); } }
    }

    /// <summary>Standard boolean-to-visibility converter for XAML bindings.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool && (bool)value) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }

    public sealed partial class DeviceListPage : Page
    {
        private readonly DeviceManager _deviceManager;
        private readonly ObservableCollection<DeviceListItem> _items = new ObservableCollection<DeviceListItem>();

        public DeviceListPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            CrashLog.Trace("list: ctor");
            _deviceManager = ((App)App.Current).DeviceManager;
            DeviceListView.ItemsSource = _items;

            if (_deviceManager == null)
            {
                // Startup failed; App.StartupError explains why.
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            _deviceManager.DeviceListChanged += OnDeviceListChanged;
            _deviceManager.OwnIdentityChanged += OnOwnIdentityChanged;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            CrashLog.Trace("list: loaded");
            try
            {
                App app = (App)App.Current;
                string diag = app.GetErrorSummary();
                if (!string.IsNullOrEmpty(diag))
                {
                    // Startup problems must be visible, not silent. This is how
                    // a bind failure or a page-load error reaches the operator.
                    MessageDialog dialog = new MessageDialog(diag, "Startup report");
                    await dialog.ShowAsync();
                }
            }
            catch (Exception)
            {
            }

            if (_deviceManager == null)
            {
                return;
            }

            await RefreshDeviceListAsync();
            UpdateOwnIdentityDisplay();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_deviceManager == null)
            {
                return;
            }
            _deviceManager.DeviceListChanged -= OnDeviceListChanged;
            _deviceManager.OwnIdentityChanged -= OnOwnIdentityChanged;
        }

        private void OnDeviceListChanged(object sender, EventArgs e)
        {
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                try
                {
                    Task ignored = RefreshDeviceListAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.Record("device list changed", ex);
                }
            }).AsTask().ConfigureAwait(false);
        }

        private void OnOwnIdentityChanged(object sender, EventArgs e)
        {
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                UpdateOwnIdentityDisplay();
            }).AsTask().ConfigureAwait(false);
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            await RefreshDeviceListAsync();
        }

        /// <summary>
        /// Manual entry. We send our identity to the typed address rather than
        /// dialling it, because dialling requires the peer's device id which is
        /// exactly what discovery failed to give us. The peer then connects to
        /// us and appears in the list.
        /// </summary>
        private async void OnAddDeviceClick(object sender, RoutedEventArgs e)
        {
            if (_deviceManager == null)
            {
                return;
            }

            TextBox input = new TextBox();
            input.PlaceholderText = "192.168.1.42";
            input.Margin = new Thickness(0, 8, 0, 0);

            StackPanel content = new StackPanel();
            TextBlock help = new TextBlock();
            help.Text = "Enter the IP address of a device running KDE Connect on this network. " +
                        "Use this when automatic discovery finds nothing.";
            help.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(help);
            content.Children.Add(input);

            ContentDialog dialog = new ContentDialog();
            dialog.Title = "Add device by IP";
            dialog.Content = content;
            dialog.PrimaryButtonText = "Announce to it";
            dialog.SecondaryButtonText = "Cancel";
            // DefaultButton / ContentDialogButton are Windows 10 additions and
            // are absent from the Phone 8.1 surface (CS1061 / CS0103).

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string address = input.Text == null ? string.Empty : input.Text.Trim();
            if (!Sanitize.IsIPv4(address))
            {
                MessageDialog bad = new MessageDialog(
                    "That is not a valid IPv4 address. Expected something like 192.168.1.42.",
                    "Add device by IP");
                await bad.ShowAsync();
                return;
            }

            bool sent = await _deviceManager.ProbeAddressAsync(address);
            if (!sent)
            {
                MessageDialog failed = new MessageDialog(
                    "Could not send to " + address + ". Check the phone is on the same network " +
                    "and that discovery is running.", "Add device by IP");
                await failed.ShowAsync();
                return;
            }

            MessageDialog ok = new MessageDialog(
                "Announced this phone to " + address + ". If that device is running KDE Connect " +
                "it should appear in the list within a few seconds.", "Add device by IP");
            await ok.ShowAsync();

            await RefreshDeviceListAsync();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        private void OnFilesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(FilesPage));
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AboutPage));
        }

        private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DiagnosticsPage));
        }

        private void OnDeviceTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return;
            }
            string deviceId = element.Tag as string;
            if (!string.IsNullOrEmpty(deviceId))
            {
                Frame.Navigate(typeof(DeviceDetailPage), deviceId);
            }
        }

        private async Task RefreshDeviceListAsync()
        {
            CrashLog.Trace("refresh: start");
            RefreshRing.IsActive = true;
            RefreshButton.IsEnabled = false;

            try
            {
                await _deviceManager.StartDiscoveryAsync();
                CrashLog.Trace("refresh: discovery listening");
                await _deviceManager.RefreshAsync();
                CrashLog.Trace("refresh: announced");
                await Task.Delay(1500); // give peers time to answer
                CrashLog.Trace("refresh: delay done");

                List<DeviceRecord> records = _deviceManager.GetAllDevices();
                CrashLog.Trace("refresh: devices=" + records.Count);
                _items.Clear();
                foreach (var r in records.OrderBy(r => r.DeviceName ?? r.DeviceId))
                {
                    _items.Add(new DeviceListItem
                    {
                        DeviceId = r.DeviceId,
                        DeviceName = r.DeviceName,
                        IsPaired = r.IsPaired,
                        IsConnected = _deviceManager.IsConnected(r.DeviceId),
                        IsConnecting = _deviceManager.IsConnecting(r.DeviceId),
                        LastSeenAddress = r.LastAddress,
                        LastSeenTcpPort = r.LastTcpPort,
                        IncomingCapabilities = r.IncomingCapabilities,
                        OutgoingCapabilities = r.OutgoingCapabilities,
                        PinnedCertificateDer = r.PinnedCertificateDer
                    });
                }

                EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                DeviceListView.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                CrashLog.Trace("refresh: bound " + _items.Count + " items");
            }
            catch (Exception ex)
            {
                // A failure here must be visible: this is where the app has been
                // dying, and an unhandled async-void neighbour would take the
                // process down with no message.
                CrashLog.Record("refresh", ex);
                StatusLine(ex);
            }
            finally
            {
                RefreshRing.IsActive = false;
                RefreshButton.IsEnabled = true;
                CrashLog.Trace("refresh: end");
            }
        }

        /// <summary>Puts a failure somewhere the operator can see it.</summary>
        private void StatusLine(Exception ex)
        {
            try
            {
                EmptyState.Visibility = Visibility.Visible;
                DeviceListView.Visibility = Visibility.Collapsed;
                OwnDeviceName.Text = "error: " + (ex == null ? "?" : ex.Message);
            }
            catch (Exception)
            {
            }
        }

        private void UpdateOwnIdentityDisplay()
        {
            var own = _deviceManager.OwnIdentity;
            if (own != null)
            {
                OwnDeviceName.Text = string.IsNullOrEmpty(own.DeviceName) ? own.DeviceId : own.DeviceName;
            }
        }
    }
}