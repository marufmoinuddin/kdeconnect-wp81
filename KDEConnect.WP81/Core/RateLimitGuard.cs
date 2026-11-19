using System;
using System.Collections.Generic;

namespace KDEConnect.WP81.Core
{
    /// <summary>
    /// Reproduces the peer-side connection rate limits that both reference
    /// implementations apply, so this client can avoid tripping them.
    ///
    /// Desktop (lanlinkprovider.cpp:49, :582-621): a second event for the same
    /// key inside 500 ms is dropped; entries expire after the window.
    /// Android uses the same shape with a 1000 ms window
    /// (LanLinkProvider.kt:671, :199-223).
    /// </summary>
    public sealed class RateLimitGuard
    {
        private readonly int _windowMs;
        private readonly Dictionary<string, long> _last = new Dictionary<string, long>(StringComparer.Ordinal);

        public RateLimitGuard(int windowMs)
        {
            _windowMs = windowMs;
        }

        public int WindowMs { get { return _windowMs; } }

        /// <summary>
        /// Returns true when an event with this key would be dropped by a peer
        /// using this window. Does not record the event; call <see cref="Record"/>
        /// when the event is actually sent.
        /// </summary>
        public bool WouldDrop(string key, long nowMs)
        {
            if (key == null)
            {
                return false;
            }
            long last;
            if (_last.TryGetValue(key, out last) && last + _windowMs > nowMs)
            {
                return true;
            }
            return false;
        }

        public void Record(string key, long nowMs)
        {
            if (key == null)
            {
                return;
            }
            if (_last.Count > 1024)
            {
                Prune(nowMs);
            }
            _last[key] = nowMs;
        }

        private void Prune(long nowMs)
        {
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, long> kv in _last)
            {
                if (kv.Value + _windowMs <= nowMs)
                {
                    expired.Add(kv.Key);
                }
            }
            for (int i = 0; i < expired.Count; i++)
            {
                _last.Remove(expired[i]);
            }
        }

        public void Clear()
        {
            _last.Clear();
        }
    }
}
