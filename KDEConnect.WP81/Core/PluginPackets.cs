using System;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Packet builders and parsers for the plugins.
    ///
    /// Everything here is pure: no sockets, no WinRT. That puts the wire format
    /// under host tests, which is the only part of a plugin that can be verified
    /// without a desktop to talk to. The plugin classes themselves stay thin.
    ///
    /// Field names come from docs/protocol.md §7, which was transcribed from the
    /// reference (kdeconnect-kde@69ce9c5c, kdeconnect-android@189525cd).
    /// </summary>
    public static class PluginPackets
    {
        // ---- packet types ---------------------------------------------------

        public const string Battery = "kdeconnect.battery";
        public const string BatteryRequest = "kdeconnect.battery.request";
        public const string Clipboard = "kdeconnect.clipboard";
        public const string ClipboardConnect = "kdeconnect.clipboard.connect";
        public const string MprisRequest = "kdeconnect.mpris.request";
        public const string MprisState = "kdeconnect.mpris";
        public const string MousepadRequest = "kdeconnect.mousepad.request";
        public const string MousepadKeyboardState = "kdeconnect.mousepad.keyboardstate";
        public const string Presenter = "kdeconnect.presenter";
        public const string SystemVolumeRequest = "kdeconnect.systemvolume.request";
        public const string SystemVolumeState = "kdeconnect.systemvolume";
        public const string ShareRequest = "kdeconnect.share.request";
        public const string ShareRequestUpdate = "kdeconnect.share.request.update";
        public const string FindMyPhoneRequest = "kdeconnect.findmyphone.request";

        // ---- battery --------------------------------------------------------

        /// <summary>
        /// Our battery level. <paramref name="charging"/> may be null when the
        /// platform cannot report it; the field is then omitted rather than
        /// sent as a guess, because false would mean "on battery".
        /// </summary>
        public static NetworkPacket BuildBattery(int currentCharge, bool? charging)
        {
            NetworkPacket np = new NetworkPacket(Battery);
            int charge = currentCharge < 0 ? 0 : (currentCharge > 100 ? 100 : currentCharge);
            np.SetInt("currentCharge", charge);
            if (charging.HasValue)
            {
                np.SetBool("isCharging", charging.Value);
            }
            return np;
        }

        public static NetworkPacket BuildBatteryRequest()
        {
            return new NetworkPacket(BatteryRequest);
        }

        public static BatteryState ParseBattery(NetworkPacket packet)
        {
            if (packet == null || packet.Type != Battery)
            {
                return null;
            }
            if (!packet.Has("currentCharge") && !packet.Has("isCharging"))
            {
                return null;
            }
            BatteryState state = new BatteryState();
            state.CurrentCharge = packet.GetInt("currentCharge", -1);
            state.IsCharging = packet.GetBool("isCharging", false);
            state.HasCharge = packet.Has("currentCharge");
            state.HasCharging = packet.Has("isCharging");
            return state;
        }

        // ---- clipboard (outgoing text only) ---------------------------------

        /// <summary>
        /// Text for the desktop clipboard. WP8.1 has no clipboard API at all
        /// (docs/known-limitations.md L1), so this carries text typed into our
        /// own UI - it is never read from the system clipboard.
        /// </summary>
        public static NetworkPacket BuildClipboard(string content, long timestampMs)
        {
            NetworkPacket np = new NetworkPacket(Clipboard);
            np.SetString("content", content == null ? string.Empty : content);
            np.SetLong("timestamp", timestampMs);
            return np;
        }

        // ---- media control --------------------------------------------------

        public static NetworkPacket BuildMprisAction(string player, string action)
        {
            NetworkPacket np = new NetworkPacket(MprisRequest);
            if (!string.IsNullOrEmpty(player))
            {
                np.SetString("player", player);
            }
            np.SetString("action", action);
            return np;
        }

        public static NetworkPacket BuildMprisRequestNowPlaying(string player)
        {
            NetworkPacket np = new NetworkPacket(MprisRequest);
            if (!string.IsNullOrEmpty(player))
            {
                np.SetString("player", player);
            }
            np.SetBool("requestNowPlaying", true);
            return np;
        }

        public static NetworkPacket BuildMprisRequestVolume(string player)
        {
            NetworkPacket np = new NetworkPacket(MprisRequest);
            if (!string.IsNullOrEmpty(player))
            {
                np.SetString("player", player);
            }
            np.SetBool("requestVolume", true);
            return np;
        }

        public static NetworkPacket BuildMprisSetVolume(string player, int volume)
        {
            NetworkPacket np = new NetworkPacket(MprisRequest);
            if (!string.IsNullOrEmpty(player))
            {
                np.SetString("player", player);
            }
            int clamped = volume < 0 ? 0 : (volume > 100 ? 100 : volume);
            np.SetInt("setVolume", clamped);
            return np;
        }

        /// <summary>Action names accepted by the desktop (mprisremoteplugin.cpp:96-140).</summary>
        public static class MprisAction
        {
            public const string Play = "Play";
            public const string Pause = "Pause";
            public const string PlayPause = "PlayPause";
            public const string Stop = "Stop";
            public const string Next = "Next";
            public const string Previous = "Previous";
        }

        // ---- touchpad -------------------------------------------------------

        public static NetworkPacket BuildMouseMove(double dx, double dy)
        {
            NetworkPacket np = new NetworkPacket(MousepadRequest);
            np.SetDouble("dx", dx);
            np.SetDouble("dy", dy);
            return np;
        }

        public static NetworkPacket BuildMouseScroll(double dx, double dy)
        {
            NetworkPacket np = new NetworkPacket(MousepadRequest);
            np.SetDouble("dx", dx);
            np.SetDouble("dy", dy);
            np.SetBool("scroll", true);
            return np;
        }

        /// <summary>button: singleclick, doubleclick, middleclick or rightclick.</summary>
        public static NetworkPacket BuildMouseClick(string button)
        {
            NetworkPacket np = new NetworkPacket(MousepadRequest);
            np.SetBool(button, true);
            return np;
        }

        public static class MouseButton
        {
            public const string Left = "singleclick";
            public const string Double = "doubleclick";
            public const string Middle = "middleclick";
            public const string Right = "rightclick";
        }

        /// <summary>
        /// A key press. <paramref name="key"/> is a single character; special
        /// keys (Enter, Backspace, ...) use <c>specialKey</c> instead, which the
        /// desktop maps through its own key table.
        /// </summary>
        public static NetworkPacket BuildKey(string key, string specialKey,
            bool alt, bool ctrl, bool shift, bool super)
        {
            NetworkPacket np = new NetworkPacket(MousepadRequest);
            if (!string.IsNullOrEmpty(key))
            {
                np.SetString("key", key);
            }
            if (!string.IsNullOrEmpty(specialKey))
            {
                np.SetString("specialKey", specialKey);
            }
            if (alt) { np.SetBool("alt", true); }
            if (ctrl) { np.SetBool("ctrl", true); }
            if (shift) { np.SetBool("shift", true); }
            if (super) { np.SetBool("super", true); }
            return np;
        }

        public static class SpecialKey
        {
            public const string Enter = "Enter";
            public const string Backspace = "Backspace";
            public const string Escape = "Escape";
            public const string Tab = "Tab";
            public const string Up = "Up";
            public const string Down = "Down";
            public const string Left = "Left";
            public const string Right = "Right";
            public const string Delete = "Delete";
        }

        // ---- presenter ------------------------------------------------------

        public static NetworkPacket BuildPresenterMove(float dx, float dy)
        {
            NetworkPacket np = new NetworkPacket(Presenter);
            np.SetDouble("dx", dx);
            np.SetDouble("dy", dy);
            return np;
        }

        public static NetworkPacket BuildPresenterStop()
        {
            NetworkPacket np = new NetworkPacket(Presenter);
            np.SetBool("stop", true);
            return np;
        }

        // ---- system volume --------------------------------------------------

        public static NetworkPacket BuildSystemVolumeRequest()
        {
            return new NetworkPacket(SystemVolumeRequest);
        }

        public static NetworkPacket BuildSystemVolumeSet(string sinkName, int volume)
        {
            NetworkPacket np = new NetworkPacket(SystemVolumeRequest);
            np.SetString("name", sinkName == null ? string.Empty : sinkName);
            int clamped = volume < 0 ? 0 : (volume > 100 ? 100 : volume);
            np.SetInt("volume", clamped);
            return np;
        }

        public static NetworkPacket BuildSystemVolumeMute(string sinkName, bool muted)
        {
            NetworkPacket np = new NetworkPacket(SystemVolumeRequest);
            np.SetString("name", sinkName == null ? string.Empty : sinkName);
            np.SetBool("muted", muted);
            return np;
        }

        // ---- share (text and url need no payload transfer) ------------------

        public static NetworkPacket BuildShareText(string text)
        {
            NetworkPacket np = new NetworkPacket(ShareRequest);
            np.SetString("text", text == null ? string.Empty : text);
            return np;
        }

        public static NetworkPacket BuildShareUrl(string url)
        {
            NetworkPacket np = new NetworkPacket(ShareRequest);
            np.SetString("url", url == null ? string.Empty : url);
            return np;
        }

        // ---- find my phone --------------------------------------------------

        public static NetworkPacket BuildFindMyPhone()
        {
            return new NetworkPacket(FindMyPhoneRequest);
        }

        /// <summary>clamp helper used by the slider-backed actions.</summary>
        public static int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }

    /// <summary>Peer battery as reported in a <c>kdeconnect.battery</c> packet.</summary>
    public sealed class BatteryState
    {
        public BatteryState()
        {
            CurrentCharge = -1;
        }

        public int CurrentCharge { get; set; }
        public bool IsCharging { get; set; }

        /// <summary>False when the peer omitted the field.</summary>
        public bool HasCharge { get; set; }

        /// <summary>False when the peer omitted the field (charging unknown).</summary>
        public bool HasCharging { get; set; }

        public string DisplayText
        {
            get
            {
                if (!HasCharge || CurrentCharge < 0)
                {
                    return "battery unknown";
                }
                string text = CurrentCharge.ToString() + "%";
                if (HasCharging && IsCharging)
                {
                    text += " (charging)";
                }
                return text;
            }
        }
    }
}
