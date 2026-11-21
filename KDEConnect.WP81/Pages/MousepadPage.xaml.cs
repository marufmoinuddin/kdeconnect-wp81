using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Plugins;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    /// <summary>
    /// Touchpad: drag to move the pointer, buttons for clicks and scrolling,
    /// and a keyboard field that forwards single keys.
    ///
    /// Movement is sent as relative deltas, so the page never needs to know the
    /// desktop's screen size - which is also why there is nothing to calibrate.
    /// </summary>
    public sealed partial class MousepadPage : Page
    {
        private const double Sensitivity = 1.6;

        private DeviceManager _deviceManager;
        private MousepadPlugin _mousepad;
        private string _deviceId;

        public MousepadPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _deviceManager = ((App)App.Current).DeviceManager;
            _deviceId = e.Parameter as string;

            if (_deviceManager == null || string.IsNullOrEmpty(_deviceId))
            {
                StatusText.Text = "No device selected.";
                return;
            }

            _mousepad = _deviceManager.PluginManager.GetPlugin("mousepad") as MousepadPlugin;
            if (_mousepad == null)
            {
                StatusText.Text = "Touchpad plugin is not available in this build.";
                return;
            }

            DeviceRecord record = _deviceManager.GetDevice(_deviceId);
            string name = record != null ? record.DeviceName : _deviceId;
            HeaderText.Text = "Touchpad - " + (string.IsNullOrEmpty(name) ? _deviceId : name);

            _mousepad.KeyboardStateChanged += OnKeyboardStateChanged;
            StatusText.Text = _deviceManager.IsConnected(_deviceId)
                ? "Connected."
                : "Not connected - reconnect on the device page.";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.KeyboardStateChanged -= OnKeyboardStateChanged;
            }
            base.OnNavigatedFrom(e);
        }

        private void OnKeyboardStateChanged(string deviceId, bool state)
        {
            if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal))
            {
                return;
            }
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                StatusText.Text = state ? "Desktop keyboard enabled." : "Desktop keyboard disabled.";
            }).AsTask().ConfigureAwait(false);
        }

        private void OnPadManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (_mousepad == null)
            {
                return;
            }
            double dx = e.Delta.Translation.X * Sensitivity;
            double dy = e.Delta.Translation.Y * Sensitivity;
            if (dx == 0 && dy == 0)
            {
                return;
            }
            _mousepad.MoveBy(_deviceId, dx, dy);
        }

        private void OnPadTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.Click(_deviceId, PluginPackets.MouseButton.Left);
            }
        }

        private void OnPadDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.Click(_deviceId, PluginPackets.MouseButton.Double);
            }
        }

        private void OnLeftClick(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null) _mousepad.Click(_deviceId, PluginPackets.MouseButton.Left);
        }

        private void OnRightClick(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null) _mousepad.Click(_deviceId, PluginPackets.MouseButton.Right);
        }

        private void OnMiddleClick(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null) _mousepad.Click(_deviceId, PluginPackets.MouseButton.Middle);
        }

        private void OnScrollUp(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null) _mousepad.Scroll(_deviceId, 0, -1);
        }

        private void OnScrollDown(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null) _mousepad.Scroll(_deviceId, 0, 1);
        }

        /// <summary>
        /// KeyUp rather than KeyDown: holding a key repeat-fires KeyDown, which
        /// would flood the desktop with duplicates.
        /// </summary>
        private void OnKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (_mousepad == null)
            {
                return;
            }

            string text = VirtualKeyToText(e.Key);
            if (text != null)
            {
                _mousepad.SendKey(_deviceId, text, null, false, false, false, false);
                KeyBox.Text = string.Empty;
                e.Handled = true;
            }
        }

        private static string VirtualKeyToText(VirtualKey key)
        {
            int code = (int)key;

            // Letters and digits map to their ASCII values on this enum.
            if (code >= 65 && code <= 90)
            {
                return ((char)code).ToString().ToLowerInvariant();
            }
            if (code >= 48 && code <= 57)
            {
                return ((char)code).ToString();
            }

            switch (key)
            {
                case VirtualKey.Space: return " ";
                case VirtualKey.Enter: return null;      // handled as specialKey
                case VirtualKey.Back: return null;
                default: return null;
            }
        }

        private void OnEnterKey(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.SendKey(_deviceId, null, PluginPackets.SpecialKey.Enter, false, false, false, false);
            }
        }

        private void OnBackspaceKey(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.SendKey(_deviceId, null, PluginPackets.SpecialKey.Backspace, false, false, false, false);
            }
        }

        private void OnEscapeKey(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.SendKey(_deviceId, null, PluginPackets.SpecialKey.Escape, false, false, false, false);
            }
        }

        private void OnTabKey(object sender, RoutedEventArgs e)
        {
            if (_mousepad != null)
            {
                _mousepad.SendKey(_deviceId, null, PluginPackets.SpecialKey.Tab, false, false, false, false);
            }
        }
    }
}
