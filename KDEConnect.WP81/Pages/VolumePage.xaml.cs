using System;
using System.Collections.Generic;
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
    /// Remote control for the desktop's audio outputs.
    ///
    /// Rows are built in code rather than bound, because VolumeSink is a plain
    /// data class with no change notification and a two-way-bound slider needs
    /// INotifyPropertyChanged to avoid fighting the user's thumb.
    /// </summary>
    public sealed partial class VolumePage : Page
    {
        private DeviceManager _deviceManager;
        private SystemVolumePlugin _volume;
        private string _deviceId;
        private bool _updating;

        public VolumePage()
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

            _volume = _deviceManager.PluginManager.GetPlugin("systemvolume") as SystemVolumePlugin;
            if (_volume == null)
            {
                StatusText.Text = "Volume plugin is not available in this build.";
                return;
            }

            _volume.SinksChanged += OnSinksChanged;
            _volume.RequestSinks(_deviceId);
            StatusText.Text = "Requested the sink list...";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_volume != null)
            {
                _volume.SinksChanged -= OnSinksChanged;
            }
            base.OnNavigatedFrom(e);
        }

        private void OnSinksChanged(string deviceId, List<VolumeSink> sinks)
        {
            if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal))
            {
                return;
            }
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                Rebuild(sinks);
            }).AsTask().ConfigureAwait(false);
        }

        private void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (_volume != null)
            {
                _volume.RequestSinks(_deviceId);
                StatusText.Text = "Requested the sink list...";
            }
        }

        private void Rebuild(List<VolumeSink> sinks)
        {
            SinkPanel.Children.Clear();

            if (sinks == null || sinks.Count == 0)
            {
                StatusText.Text = "The desktop reported no audio outputs.";
                return;
            }

            _updating = true;
            try
            {
                for (int i = 0; i < sinks.Count; i++)
                {
                    SinkPanel.Children.Add(BuildRow(sinks[i]));
                }
            }
            finally
            {
                _updating = false;
            }

            StatusText.Text = "Set the desktop volume from here.";
        }

        private FrameworkElement BuildRow(VolumeSink sink)
        {
            // Copy the name so the handlers close over a stable value.
            string sinkName = sink.Name;

            Border card = new Border();
            card.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            card.CornerRadius = new CornerRadius(4);
            card.Padding = new Thickness(12);
            card.Margin = new Thickness(0, 0, 0, 8);

            StackPanel panel = new StackPanel();

            TextBlock label = new TextBlock();
            label.Text = sink.DisplayName;
            label.TextWrapping = TextWrapping.Wrap;
            label.FontSize = 15;
            panel.Children.Add(label);

            Slider slider = new Slider();
            slider.Minimum = 0;
            slider.Maximum = 100;
            slider.StepFrequency = 1;
            slider.Value = sink.Volume < 0 ? 0 : sink.Volume;
            slider.Margin = new Thickness(0, 4, 0, 0);
            slider.ValueChanged += delegate(object s, RangeBaseValueChangedEventArgs args)
            {
                if (_updating || _volume == null)
                {
                    return;
                }
                _volume.SetVolume(_deviceId, sinkName, (int)args.NewValue);
            };
            panel.Children.Add(slider);

            CheckBox mute = new CheckBox();
            mute.Content = "Muted";
            mute.IsChecked = sink.Muted;
            mute.Margin = new Thickness(0, 4, 0, 0);
            mute.Checked += delegate
            {
                if (!_updating && _volume != null)
                {
                    _volume.SetMuted(_deviceId, sinkName, true);
                }
            };
            mute.Unchecked += delegate
            {
                if (!_updating && _volume != null)
                {
                    _volume.SetMuted(_deviceId, sinkName, false);
                }
            };
            panel.Children.Add(mute);

            card.Child = panel;
            return card;
        }
    }
}
