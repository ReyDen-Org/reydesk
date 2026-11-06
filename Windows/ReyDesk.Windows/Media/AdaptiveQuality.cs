using System;
using ReyDesk.Windows.Settings;

namespace ReyDesk.Windows.Media;

// Conservative Auto controller using receiver packet loss + ICE-pair network RTT,
// not an invented bandwidth estimate or end-to-end display latency. Preserve pixel
// detail, reduce frame rate first; hysteresis prevents capture/encoder thrashing.
internal sealed class AdaptiveQuality(StreamPreferences ceiling)
{
    private long _lastSample = -2000, _lastChange = -10000;
    private int _bad, _good, _tier;
    public StreamPreferences? Observe(double? loss, double? rtt, long nowMs)
    {
        if (nowMs - _lastSample < 2000) return null;
        _lastSample = nowMs;
        bool validLoss = loss is >= 0 and <= 100 && double.IsFinite(loss.Value);
        bool validRtt = rtt is >= 0 && double.IsFinite(rtt.Value);
        if (!validLoss && !validRtt) return null;
        bool bad = (validLoss && loss > 5) || (validRtt && rtt > 150);
        bool good = (!validLoss || loss < 1) && (!validRtt || rtt < 80);
        _bad = bad ? _bad + 1 : 0;
        _good = good ? _good + 1 : 0;
        int next = _tier;
        if (_bad >= 3) next = Math.Min(2, _tier + 1);
        else if (_good >= 6) next = Math.Max(0, _tier - 1);
        if (next == _tier || nowMs - _lastChange < 10000) return null;
        _tier = next; _lastChange = nowMs; _bad = _good = 0;
        return new StreamPreferences(ceiling.MaxHeight,
            Math.Min(ceiling.MaxFps, next == 0 ? 60 : next == 1 ? 30 : 15),
            Math.Min(ceiling.BitrateKbps, next == 0 ? ceiling.BitrateKbps : next == 1 ? 8000 : 4000));
    }
}
