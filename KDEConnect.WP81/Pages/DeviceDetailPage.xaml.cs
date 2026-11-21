using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;
using KDEConnect.WP81.Plugins;

namespace KDEConnect.WP81.Pages
{
    public sealed partial class DeviceDetailPage : Page
    {
        private DeviceManager _deviceManager;
        private PluginManager _pluginManager;
        private string _deviceId;
        private DeviceRecord _record;

        private BatteryPlugin _batteryPlugin;
        private ClipboardPlugin _clipboardPlugin;
        private FindMyPhonePlugin _findMyPhonePlugin;

        public DeviceDetailPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _deviceManager = ((App)App.Current).DeviceManager;
            _pluginManager = ((App)App.Current).PluginManager;

            _deviceId = e.Parameter as string;

            if (string.IsNullOrEmpty(_deviceId))
            {
                Frame.Navigate(typeof(DeviceListPage));
                return;
            }

            _batteryPlugin = _pluginManager.GetPlugin("battery") as BatteryPlugin;
            if (_batteryPlugin != null)
            {
                _batteryPlugin.BatteryChanged += OnBatteryChanged;
                _batteryPlugin.RequestPeerBattery(_deviceId);
            }

            _clipboardPlugin = _pluginManager.GetPlugin("clipboard") as ClipboardPlugin;

            _findMyPhonePlugin = _pluginManager.GetPlugin("findmyphone") as FindMyPhonePlugin;
            if (_findMyPhonePlugin != null)
            {
                _findMyPhonePlugin.RingRequested += OnRingRequested;
            }

            await RefreshAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_batteryPlugin != null)
            {
                _batteryPlugin.BatteryChanged -= OnBatteryChanged;
            }
            if (_findMyPhonePlugin != null)
            {
                _findMyPhonePlugin.RingRequested -= OnRingRequested;
            }
            base.OnNavigatedFrom(e);
        }

        private void OnBatteryChanged(string deviceId, BatteryState state)
        {
            if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal) || state == null)
            {
                return;
            }
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                BatteryText.Text = state.DisplayText;
            }).AsTask().ConfigureAwait(false);
        }

        private void OnRingRequested(string deviceId)
        {
            // The phone has no background audio, so "find my phone" surfaces as
            // a full-screen alert rather than a ringtone.
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                MessageDialog dialog = new MessageDialog(
                    "The desktop asked this phone to make itself known.",
                    "Find my phone");
                dialog.ShowAsync();
            }).AsTask().ConfigureAwait(false);
        }

        private void OnTouchpadClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MousepadPage), _deviceId);
        }

        private void OnMediaClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MediaPage), _deviceId);
        }

        private void OnVolumeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VolumePage), _deviceId);
        }

        private void OnSendClipboardClick(object sender, RoutedEventArgs e)
        {
            if (_clipboardPlugin == null)
            {
                ClipboardStatus.Text = "Clipboard plugin not available.";
                return;
            }
            string text = ClipboardBox.Text;
            if (string.IsNullOrEmpty(text))
            {
                ClipboardStatus.Text = "Nothing to send.";
                return;
            }
            bool sent = _clipboardPlugin.SendText(_deviceId, text);
            ClipboardStatus.Text = sent ? "Sent to the desktop clipboard." : "Not connected.";
        }

        private async Task RefreshAsync()
        {
            _record = _deviceManager.GetDevice(_deviceId);
            if (_record == null)
            {
                await new MessageDialog("Device not found").ShowAsync();
                Frame.GoBack();
                return;
            }

            DeviceNameText.Text = string.IsNullOrEmpty(_record.DeviceName) ? _record.DeviceId : _record.DeviceName;
            DeviceIdText.Text = _record.DeviceId;

            bool isConnected = _deviceManager.IsConnected(_deviceId);
            bool isConnecting = _deviceManager.IsConnecting(_deviceId);
            bool isPaired = _record.IsPaired;

            StatusText.Text = isConnecting ? "Connecting..." : (isConnected ? "Connected" : (isPaired ? "Paired (disconnected)" : "Discovered"));
            AddressText.Text = _record.LastAddress != null && _record.LastAddress.Length > 0
                ? _record.LastAddress : "unknown";
            PortText.Text = _record.LastTcpPort > 0
                ? _record.LastTcpPort.ToString() : "unknown";
            ConnectionStatusText.Text = isConnected ? "Connected" : (isConnecting ? "Connecting..." : "Disconnected");
            PairedText.Text = isPaired ? "Yes" : "No";
            PinnedText.Text = _record.PinnedCertificateDer != null && _record.PinnedCertificateDer.Length > 0 ? "Yes" : "No";

            PairButton.Visibility = isPaired ? Visibility.Collapsed : Visibility.Visible;
            UnpairButton.Visibility = isPaired ? Visibility.Visible : Visibility.Collapsed;
            PingButton.IsEnabled = isConnected;

            IncomingCapsPanel.Children.Clear();
            IncomingCapsPanel.Children.Add(new TextBlock { Text = "Receives (Incoming):", FontSize = 12, Opacity = 0.7 });
            if (_record.IncomingCapabilities != null && _record.IncomingCapabilities.Count > 0)
            {
                foreach (var cap in _record.IncomingCapabilities.OrderBy(c => c))
                {
                    IncomingCapsPanel.Children.Add(new TextBlock { Text = "  • " + cap, FontSize = 15, Margin = new Thickness(12, 2, 0, 2) });
                }
            }
            else
            {
                IncomingCapsPanel.Children.Add(new TextBlock { Text = "  (none)", FontSize = 15, Opacity = 0.5, Margin = new Thickness(12, 2, 0, 2) });
            }

            OutgoingCapsPanel.Children.Clear();
            OutgoingCapsPanel.Children.Add(new TextBlock { Text = "Sends (Outgoing):", FontSize = 12, Opacity = 0.7 });
            if (_record.OutgoingCapabilities != null && _record.OutgoingCapabilities.Count > 0)
            {
                foreach (var cap in _record.OutgoingCapabilities.OrderBy(c => c))
                {
                    OutgoingCapsPanel.Children.Add(new TextBlock { Text = "  • " + cap, FontSize = 15, Margin = new Thickness(12, 2, 0, 2) });
                }
            }
            else
            {
                OutgoingCapsPanel.Children.Add(new TextBlock { Text = "  (none)", FontSize = 15, Opacity = 0.5, Margin = new Thickness(12, 2, 0, 2) });
            }
        }

        private async void OnPairClick(object sender, RoutedEventArgs e)
        {
            PairButton.IsEnabled = false;
            try
            {
                bool success = await _deviceManager.RequestPairingAsync(_deviceId);
                if (success)
                {
                    await ShowPairingDialogAsync();
                }
                else
                {
                    string reason = _deviceManager.GetLastError(_deviceId);
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "The device did not accept a connection.";
                    }
                    await new MessageDialog(
                        "Could not start pairing." + Environment.NewLine +
                        Environment.NewLine + reason + Environment.NewLine +
                        Environment.NewLine + "build " + KDEConnect.WP81.Core.BuildStamp.Value).ShowAsync();
                }
            }
            finally
            {
                PairButton.IsEnabled = true;
            }

            // Refresh AFTER the finally block: 'await' in a finally clause is
            // C# 6, and this project must compile as C# 5 under VS2013.
            await RefreshAsync();
        }

        private async Task ShowPairingDialogAsync()
        {
            string code = _deviceManager.GetVerificationCode(_deviceId);
            PairingDialog dialog = new PairingDialog();
            dialog.Initialize(_deviceManager, _deviceId, code);
            await dialog.ShowAsync();
            await RefreshAsync();
        }

        private async void OnUnpairClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog("Unpair this device? This will remove the trust certificate.", "Confirm Unpair");
            dialog.Commands.Add(new UICommand("Unpair", async cmd =>
            {
                _deviceManager.Unpair(_deviceId);
                await RefreshAsync();
            }));
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            await dialog.ShowAsync();
        }

        private async void OnPingClick(object sender, RoutedEventArgs e)
        {
            if (!_deviceManager.IsConnected(_deviceId))
            {
                PingResultText.Text = "Not connected";
                return;
            }

            PingButton.IsEnabled = false;
            PingResultText.Text = "Sending ping...";

            try
            {
                IPlugin plugin = _pluginManager.GetPlugin("ping");
                PingPlugin pingPlugin = plugin as PingPlugin;
                if (pingPlugin != null)
                {
                    // Use reflection or a direct method - for now just send via link manager
                    NetworkPacket ping = new NetworkPacket(ProtocolConstants.PacketTypePing);
                    ping.Id = DateTime.UtcNow.Ticks;
                    ping.SetString("message", "ping from WP81");

                    bool sent = _deviceManager.LinkManager.SendPacket(_deviceId, ping);
                    PingResultText.Text = sent
                        ? "Ping sent - the peer should show it."
                        : "Failed to send ping";
                }
                else
                {
                    PingResultText.Text = "Ping plugin not loaded";
                }
            }
            catch (Exception ex)
            {
                PingResultText.Text = "Error: " + ex.Message;
            }
            finally
            {
                PingButton.IsEnabled = true;
            }
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }
    }
}