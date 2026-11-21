using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using KDEConnect.WP81.Core;

namespace KDEConnect.WP81.Pages
{
    public sealed partial class PairingDialog : ContentDialog
    {
        private PairingHandler _handler;
        private DeviceManager _deviceManager;
        private string _deviceId;
        private byte[] _ownPublicKeyDer;
        private byte[] _peerPublicKeyDer;
        private long _timestamp;

        public PairingDialog()
        {
            this.InitializeComponent();
        }

        public void Initialize(PairingHandler handler, DeviceManager deviceManager, string deviceId,
            byte[] ownPublicKeyDer, byte[] peerPublicKeyDer, long timestamp)
        {
            _handler = handler;
            _deviceManager = deviceManager;
            _deviceId = deviceId;
            _ownPublicKeyDer = ownPublicKeyDer;
            _peerPublicKeyDer = peerPublicKeyDer;
            _timestamp = timestamp;

            var record = _deviceManager.GetDevice(deviceId);
            if (record != null)
            {
                DeviceNameText.Text = string.IsNullOrEmpty(record.DeviceName) ? record.DeviceId : record.DeviceName;
                DeviceIdText.Text = record.DeviceId;
            }

            string code = PairingHandler.ComputeVerificationCode(_ownPublicKeyDer, _peerPublicKeyDer, _timestamp);
            VerificationCodeText.Text = FormatCode(code);
        }

        private string FormatCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 8) return code;
            return code.Substring(0, 4) + " " + code.Substring(4, 4);
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var outcome = PairingEvent.None;
            var packet = _handler.AcceptPairing(out outcome);
            if (packet != null && _deviceManager.IsConnected(_deviceId))
            {
                _deviceManager.LinkManager.SendPacket(_deviceId, packet);
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var outcome = PairingEvent.None;
            var packet = _handler.CancelPairing(out outcome);
            if (packet != null && _deviceManager.IsConnected(_deviceId))
            {
                _deviceManager.LinkManager.SendPacket(_deviceId, packet);
            }
        }
    }
}