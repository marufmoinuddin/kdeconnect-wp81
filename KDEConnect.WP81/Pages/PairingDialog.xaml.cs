using Windows.UI.Xaml.Controls;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    /// <summary>
    /// Shows the shared verification code and lets the operator accept or
    /// reject the pairing. The comparison is the whole trust decision: if the
    /// two displays disagree, the peer is not who it claims to be.
    /// </summary>
    public sealed partial class PairingDialog : ContentDialog
    {
        private DeviceManager _deviceManager;
        private string _deviceId;

        public PairingDialog()
        {
            this.InitializeComponent();
        }

        public void Initialize(DeviceManager deviceManager, string deviceId, string verificationCode)
        {
            _deviceManager = deviceManager;
            _deviceId = deviceId;

            DeviceRecordView view = new DeviceRecordView();
            Core.DeviceRecord record = deviceManager != null ? deviceManager.GetDevice(deviceId) : null;
            if (record != null)
            {
                view.Name = string.IsNullOrEmpty(record.DeviceName) ? record.DeviceId : record.DeviceName;
            }
            else
            {
                view.Name = deviceId;
            }

            DeviceNameText.Text = view.Name;
            DeviceIdText.Text = deviceId ?? string.Empty;
            VerificationCodeText.Text = FormatCode(verificationCode);
        }

        /// <summary>Groups the eight hex digits as XXXX XXXX for comparison.</summary>
        private static string FormatCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return "--------";
            }
            if (code.Length != 8)
            {
                return code;
            }
            return code.Substring(0, 4) + " " + code.Substring(4, 4);
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_deviceManager != null && !string.IsNullOrEmpty(_deviceId))
            {
                _deviceManager.AcceptPairing(_deviceId);
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_deviceManager != null && !string.IsNullOrEmpty(_deviceId))
            {
                _deviceManager.RejectPairing(_deviceId);
            }
        }

        /// <summary>Tiny holder so the name lookup stays null-safe in C# 5.</summary>
        private sealed class DeviceRecordView
        {
            public string Name { get; set; }
        }
    }
}
