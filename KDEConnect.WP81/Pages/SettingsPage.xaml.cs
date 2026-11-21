using System;
using System.Text;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Security;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private DeviceStore _deviceStore;
        private DeviceManager _deviceManager;

        public SettingsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _deviceStore = ((App)App.Current).DeviceStore;
            _deviceManager = ((App)App.Current).DeviceManager;

            var own = _deviceStore.LoadIdentity();
            if (own != null)
            {
                DeviceIdText.Text = own.DeviceId;
                DeviceNameBox.Text = own.DeviceName;
            }
        }

        private void OnDeviceNameChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e)
        {
            var own = _deviceStore.LoadIdentity();
            if (own != null)
            {
                own.DeviceName = DeviceNameBox.Text;
                _deviceStore.SaveIdentity(own);
            }
        }

        private void OnRegenerateIdentityClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                "Regenerating the identity will unpair all devices and create a new certificate. Continue?",
                "Confirm Regenerate");
            dialog.Commands.Add(new UICommand("Regenerate", async cmd =>
            {
                _deviceStore.Clear();
                var newId = Sanitize.GenerateDeviceId();
                var identity = CertificateProvider.CreateIdentity(newId);
                var newOwn = new DeviceStore.OwnIdentity
                {
                    DeviceId = newId,
                    DeviceName = "WP81",
                    CertificateDer = identity.CertificateDer,
                    PrivateKeyDer = identity.PrivateKeyDer
                };
                _deviceStore.SaveIdentity(newOwn);
                await new MessageDialog("Identity regenerated. Restart the app to apply.", "Done").ShowAsync();
            }));
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            dialog.ShowAsync();
        }

        private void OnClearAllPairingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                "This will remove all paired devices and their pinned certificates. Continue?",
                "Confirm Clear Pairings");
            dialog.Commands.Add(new UICommand("Clear", cmd =>
            {
                var peers = _deviceStore.LoadPeers();
                foreach (var p in peers)
                {
                    _deviceStore.RemovePeer(p.DeviceId);
                }
                _deviceManager.NotifyDeviceListChanged();
            }));
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            dialog.ShowAsync();
        }

        private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            ExportDiagnosticsButton.IsEnabled = false;
            DiagnosticsResultText.Text = "Generating...";

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("KDE Connect WP8.1 Diagnostics");
                sb.AppendLine("Generated: " + DateTime.UtcNow.ToString("u") + "Z");
                sb.AppendLine();

                var own = _deviceStore.LoadOrCreateIdentity(Sanitize.GenerateDeviceId(), "WP81");
                if (own != null)
                {
                    sb.AppendLine("Own Device:");
                    sb.AppendLine("  ID: " + Redactor.RedactDeviceId(own.DeviceId));
                    sb.AppendLine("  Name: " + (string.IsNullOrEmpty(own.DeviceName) ? "(none)" : own.DeviceName));
                    sb.AppendLine("  Cert SHA256: " + Redactor.CertificateFingerprint(own.CertificateDer, 8));
                    sb.AppendLine();
                }

                var peers = _deviceStore.LoadPeers();
                sb.AppendLine("Paired Devices (" + peers.Count + "):");
                foreach (var p in peers)
                {
                    sb.AppendLine("  " + Redactor.RedactDeviceId(p.DeviceId) + " - " + (p.IsPaired ? "paired" : "discovered"));
                    if (p.PinnedCertificateDer != null && p.PinnedCertificateDer.Length > 0)
                    {
                        sb.AppendLine("    Pin SHA256: " + Redactor.CertificateFingerprint(p.PinnedCertificateDer, 8));
                    }
                }
                sb.AppendLine();

                sb.AppendLine("Advertised Incoming Capabilities:");
                foreach (var c in _deviceManager.AdvertisedIncoming) sb.AppendLine("  " + c);
                sb.AppendLine();

                sb.AppendLine("Advertised Outgoing Capabilities:");
                foreach (var c in _deviceManager.AdvertisedOutgoing) sb.AppendLine("  " + c);

                string content = sb.ToString();
                string fileName = "kdeconnect-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt";

                // DocumentsLibrary needs the documentsLibrary capability, which
                // a Windows Phone Store app cannot declare (inventory line 227).
                // DownloadsFolder self-created files need no capability (S13).
                StorageFile file = await DownloadsFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteTextAsync(file, content);

                DiagnosticsResultText.Text = "Saved to Documents\\" + fileName;
            }
            catch (Exception ex)
            {
                DiagnosticsResultText.Text = "Error: " + ex.Message;
            }
            finally
            {
                ExportDiagnosticsButton.IsEnabled = true;
            }
        }
    }
}