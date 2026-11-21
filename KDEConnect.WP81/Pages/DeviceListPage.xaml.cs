using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
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
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                RefreshDeviceListAsync();
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

        private async void OnAddDeviceClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DeviceDetailPage), null); // null = new manual entry
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
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
            RefreshRing.IsActive = true;
            RefreshButton.IsEnabled = false;

            try
            {
                await _deviceManager.StartDiscoveryAsync();
                await _deviceManager.RefreshAsync();
                await Task.Delay(1500); // give peers time to answer

                List<DeviceRecord> records = _deviceManager.GetAllDevices();
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
            }
            finally
            {
                RefreshRing.IsActive = false;
                RefreshButton.IsEnabled = true;
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