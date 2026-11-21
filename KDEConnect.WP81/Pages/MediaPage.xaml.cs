using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;
using KDEConnect.WP81.Core;
using KDEConnect.WP81.Plugins;
using KDEConnect.WP81.Services;

namespace KDEConnect.WP81.Pages
{
    /// <summary>
    /// Remote control for the desktop's media player.
    ///
    /// The page is driven entirely by state the desktop pushes; the phone has no
    /// idea what is playing unless the desktop says so.
    /// </summary>
    public sealed partial class MediaPage : Page
    {
        private DeviceManager _deviceManager;
        private MprisPlugin _mpris;
        private string _deviceId;
        private string _player;
        private bool _updatingSlider;
        private MprisState _state;

        public MediaPage()
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

            _mpris = _deviceManager.PluginManager.GetPlugin("mpris") as MprisPlugin;
            if (_mpris == null)
            {
                StatusText.Text = "Media plugin is not available in this build.";
                return;
            }

            _mpris.StateChanged += OnStateChanged;
            _mpris.RequestNowPlaying(_deviceId, null);
            _mpris.RequestVolume(_deviceId, null);

            StatusText.Text = _deviceManager.IsConnected(_deviceId)
                ? "Waiting for the desktop to report state..."
                : "Not connected - reconnect on the device page.";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_mpris != null)
            {
                _mpris.StateChanged -= OnStateChanged;
            }
            base.OnNavigatedFrom(e);
        }

        private void OnStateChanged(string deviceId, MprisState state)
        {
            if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal) || state == null)
            {
                return;
            }
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                Apply(state);
            }).AsTask().ConfigureAwait(false);
        }

        private void Apply(MprisState state)
        {
            _state = state;
            _player = state.Player;

            TitleText.Text = string.IsNullOrEmpty(state.Title) ? "(no title)" : state.Title;
            ArtistText.Text = state.Artist == null ? string.Empty : state.Artist;
            PositionText.Text = state.PositionLine;
            PlayerText.Text = _player == null ? string.Empty : _player;

            PlayPauseButton.Content = state.IsPlaying ? "\uE103" : "\uE102";

            _updatingSlider = true;
            try
            {
                if (state.Volume >= 0)
                {
                    VolumeSlider.Value = state.Volume;
                    VolumeText.Text = state.Volume.ToString() + "%";
                }
            }
            finally
            {
                _updatingSlider = false;
            }

            StatusText.Text = state.IsPlaying ? "Playing" : "Paused";
        }

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_mpris == null)
            {
                return;
            }
            string action = _state != null && _state.IsPlaying
                ? PluginPackets.MprisAction.Pause
                : PluginPackets.MprisAction.Play;
            _mpris.SendAction(_deviceId, _player, action);
        }

        private void OnNext(object sender, RoutedEventArgs e)
        {
            if (_mpris != null) _mpris.SendAction(_deviceId, _player, PluginPackets.MprisAction.Next);
        }

        private void OnPrevious(object sender, RoutedEventArgs e)
        {
            if (_mpris != null) _mpris.SendAction(_deviceId, _player, PluginPackets.MprisAction.Previous);
        }

        private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingSlider || _mpris == null)
            {
                return;
            }
            int volume = (int)e.NewValue;
            VolumeText.Text = volume.ToString() + "%";
            _mpris.SetVolume(_deviceId, _player, volume);
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (_mpris == null)
            {
                return;
            }
            _mpris.RequestNowPlaying(_deviceId, _player);
            _mpris.RequestVolume(_deviceId, _player);
            StatusText.Text = "Refresh requested.";
        }
    }
}
