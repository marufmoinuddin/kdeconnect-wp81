using System;
using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Player state pushed by a desktop in a <c>kdeconnect.mpris</c> packet.
    ///
    /// Field names follow the reference (mprisremoteplayer.cpp /
    /// mprisremoteplugin.cpp:32-83); docs/protocol.md §7 is the table this was
    /// written from. Everything is a plain field so the parsing can be unit
    /// tested on the host, away from any WinRT type.
    /// </summary>
    public sealed class MprisState
    {
        public MprisState()
        {
            PlayerList = new List<string>();
        }

        public string Player { get; set; }
        public List<string> PlayerList { get; private set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string AlbumArtUrl { get; set; }
        public bool IsPlaying { get; set; }

        /// <summary>Track length in milliseconds.</summary>
        public long Length { get; set; }

        /// <summary>Playback position in milliseconds.</summary>
        public long Position { get; set; }

        public double Rate { get; set; }
        public int Volume { get; set; }

        public bool CanPlay { get; set; }
        public bool CanPause { get; set; }
        public bool CanGoNext { get; set; }
        public bool CanGoPrevious { get; set; }
        public bool CanSeek { get; set; }

        /// <summary>"Track title — artist", or a placeholder when nothing plays.</summary>
        public string DisplayLine
        {
            get
            {
                string title = Title == null ? string.Empty : Title.Trim();
                string artist = Artist == null ? string.Empty : Artist.Trim();
                if (title.Length == 0 && artist.Length == 0)
                {
                    return "(no media)";
                }
                if (artist.Length == 0)
                {
                    return title;
                }
                if (title.Length == 0)
                {
                    return artist;
                }
                return title + " — " + artist;
            }
        }

        /// <summary>Position and length as mm:ss / mm:ss, for the now-playing row.</summary>
        public string PositionLine
        {
            get
            {
                if (Length <= 0)
                {
                    return string.Empty;
                }
                return FormatMilliseconds(Position) + " / " + FormatMilliseconds(Length);
            }
        }

        public static string FormatMilliseconds(long ms)
        {
            if (ms < 0)
            {
                ms = 0;
            }
            long totalSeconds = ms / 1000;
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;
            return minutes.ToString() + ":" + (seconds < 10 ? "0" : string.Empty) + seconds.ToString();
        }

        /// <summary>
        /// Reads a <c>kdeconnect.mpris</c> packet. Returns null when the packet
        /// is the wrong type or carries no recognisable field at all, so a
        /// stray packet cannot make the UI render an empty player.
        /// </summary>
        public static MprisState FromPacket(NetworkPacket packet)
        {
            if (packet == null || packet.Type != PluginPackets.MprisState)
            {
                return null;
            }

            MprisState state = new MprisState();
            state.Player = packet.GetString("player", null);
            state.Title = packet.GetString("title", null);
            state.Artist = packet.GetString("artist", null);
            state.Album = packet.GetString("album", null);
            state.AlbumArtUrl = packet.GetString("albumArtUrl", null);
            state.IsPlaying = packet.GetBool("isPlaying", false);
            state.Length = packet.GetLong("length", 0);
            state.Position = packet.GetLong("pos", 0);
            state.Rate = packet.GetDouble("rate", 1.0);
            state.Volume = packet.GetInt("volume", -1);
            state.CanPlay = packet.GetBool("canPlay", false);
            state.CanPause = packet.GetBool("canPause", false);
            state.CanGoNext = packet.GetBool("canGoNext", false);
            state.CanGoPrevious = packet.GetBool("canGoPrevious", false);
            state.CanSeek = packet.GetBool("canSeek", false);

            List<string> players = packet.GetStringList("playerList");
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    if (!string.IsNullOrEmpty(players[i]))
                    {
                        state.PlayerList.Add(players[i]);
                    }
                }
            }

            bool hasAnything =
                (state.Player != null && state.Player.Length > 0) ||
                (state.Title != null && state.Title.Length > 0) ||
                (state.Artist != null && state.Artist.Length > 0) ||
                state.Length > 0 ||
                state.PlayerList.Count > 0 ||
                packet.Has("isPlaying") ||
                packet.Has("canPlay");

            return hasAnything ? state : null;
        }
    }
}
