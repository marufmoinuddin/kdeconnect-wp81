using System;
using System.IO;
using System.Text;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    public sealed partial class DiagnosticsPage : Page
    {
        private DeviceStore _deviceStore;
        private DeviceManager _deviceManager;

        public DiagnosticsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _deviceStore = ((App)App.Current).DeviceStore;
            _deviceManager = ((App)App.Current).DeviceManager;

            RefreshLive();
        }

        private void OnRefreshLiveClick(object sender, RoutedEventArgs e)
        {
            RefreshLive();
        }

        private void RefreshLive()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Live Diagnostics - " + DateTime.UtcNow.ToString("u") + "Z");
            sb.AppendLine();

            var own = _deviceStore.LoadOrCreateIdentity(Sanitize.GenerateDeviceId(), "WP81");
            if (own != null)
            {
                sb.AppendLine("Own Device: " + Redactor.RedactDeviceId(own.DeviceId) + " (" + (own.DeviceName ?? "") + ")");
                sb.AppendLine("  Cert: " + Redactor.HashSha256(own.CertificateDer));
                sb.AppendLine("  Key:  " + Redactor.HashSha256(own.PrivateKeyDer));
            }
            else
            {
                sb.AppendLine("Own Device: (not initialized)");
            }
            sb.AppendLine();

            var peers = _deviceStore.LoadPeers();
            sb.AppendLine("Known Devices: " + peers.Count);
            foreach (var p in peers)
            {
                string status = p.IsPaired ? "paired" : "discovered";
                if (_deviceManager.IsConnected(p.DeviceId)) status += ", connected";
                if (_deviceManager.IsConnecting(p.DeviceId)) status += ", connecting";
                sb.AppendLine("  " + Redactor.RedactDeviceId(p.DeviceId) + " - " + status);
                if (p.PinnedCertificateDer != null && p.PinnedCertificateDer.Length > 0)
                    sb.AppendLine("    Pin: " + Redactor.HashSha256(p.PinnedCertificateDer));
            }
            sb.AppendLine();

            sb.AppendLine("Advertised Incoming:");
            foreach (var c in Capabilities.IncomingList()) sb.AppendLine("  " + c);
            sb.AppendLine();

            sb.AppendLine("Advertised Outgoing:");
            foreach (var c in Capabilities.OutgoingList()) sb.AppendLine("  " + c);

            LiveText.Text = sb.ToString();
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            ExportButton.IsEnabled = false;
            ExportResultText.Text = "Generating...";

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("KDE Connect WP8.1 Diagnostics Export");
                sb.AppendLine("Generated: " + DateTime.UtcNow.ToString("u") + "Z");
                sb.AppendLine();

                var own = _deviceStore.LoadOrCreateIdentity(Sanitize.GenerateDeviceId(), "WP81");
                if (own != null)
                {
                    sb.AppendLine("=== Own Identity ===");
                    sb.AppendLine("Device ID: " + Redactor.RedactDeviceId(own.DeviceId));
                    sb.AppendLine("Device Name: " + (string.IsNullOrEmpty(own.DeviceName) ? "(none)" : own.DeviceName));
                    sb.AppendLine("Certificate SHA256: " + Redactor.HashSha256(own.CertificateDer));
                    sb.AppendLine("Private Key SHA256: " + Redactor.HashSha256(own.PrivateKeyDer));
                    sb.AppendLine();
                }

                var peers = _deviceStore.LoadPeers();
                sb.AppendLine("=== Known Devices (" + peers.Count + ") ===");
                foreach (var p in peers)
                {
                    sb.AppendLine();
                    sb.AppendLine("Device ID: " + Redactor.RedactDeviceId(p.DeviceId));
                    sb.AppendLine("Name: " + (p.DeviceName ?? "(none)"));
                    sb.AppendLine("Type: " + p.DeviceType);
                    sb.AppendLine("Protocol Version: " + p.ProtocolVersion);
                    sb.AppendLine("Paired: " + p.IsPaired);
                    sb.AppendLine("Connected: " + _deviceManager.IsConnected(p.DeviceId));
                    sb.AppendLine("Last Seen: " + p.LastSeenUtc.ToString("u") + "Z");
                    sb.AppendLine("Last Address: " + (p.LastSeenAddress ?? "unknown"));
                    sb.AppendLine("Last TCP Port: " + p.LastSeenTcpPort);
                    if (p.PinnedCertificateDer != null && p.PinnedCertificateDer.Length > 0)
                        sb.AppendLine("Pinned Cert SHA256: " + Redactor.HashSha256(p.PinnedCertificateDer));
                    sb.AppendLine("Incoming Caps: " + string.Join(", ", p.IncomingCapabilities ?? new System.Collections.Generic.List<string>()));
                    sb.AppendLine("Outgoing Caps: " + string.Join(", ", p.OutgoingCapabilities ?? new System.Collections.Generic.List<string>()));
                }
                sb.AppendLine();

                sb.AppendLine("=== Advertised Capabilities ===");
                sb.AppendLine("Incoming: " + string.Join(", ", Capabilities.IncomingList()));
                sb.AppendLine("Outgoing: " + string.Join(", ", Capabilities.OutgoingList()));

                string content = sb.ToString();
                string fileName = "kdeconnect-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt";

                StorageFile file = await KnownFolders.DocumentsLibrary.CreateFileAsync(
                    fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteTextAsync(file, content);

                ExportResultText.Text = "Saved to Documents\\" + fileName;
            }
            catch (Exception ex)
            {
                ExportResultText.Text = "Error: " + ex.Message;
            }
            finally
            {
                ExportButton.IsEnabled = true;
            }
        }
    }
}