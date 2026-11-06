namespace ReyDesk.Windows.Media;

internal enum VideoFrameDropReason { None, FutureTimestamp, Stale, Cadence }

/// <summary>Accepts fresh capture frames at a bounded rate without replaying missed slots.
/// All times are monotonic 100ns ticks (WGC SystemRelativeTime / QPC).</summary>
internal sealed class VideoFrameTiming(int fps)
{
    private readonly long _minimumIntervalHns = (10_000_000L + fps - 1) / fps;
    private const long MaximumFrameAgeHns = 2_000_000; // 200ms; stale desktops are not useful input feedback.
    private long? _originHns;
    private long _nextEligibleHns;
    public VideoFrameDropReason LastDropReason { get; private set; }

    public bool TryAccept(long sourceTimeHns, long nowHns, out long sampleTimeHns)
    {
        sampleTimeHns = 0;
        // WGC compositor presentation timestamps can lead callback QPC by ~11ms
        // on real hardware. Allow one capture interval while rejecting large clock
        // mismatches; retain the original source timestamp for pacing and media PTS.
        LastDropReason = sourceTimeHns - nowHns > _minimumIntervalHns ? VideoFrameDropReason.FutureTimestamp :
            nowHns - sourceTimeHns > MaximumFrameAgeHns ? VideoFrameDropReason.Stale :
            _originHns.HasValue && sourceTimeHns < _nextEligibleHns ? VideoFrameDropReason.Cadence : VideoFrameDropReason.None;
        if (LastDropReason != VideoFrameDropReason.None)
        {
            return false;
        }
        if (!_originHns.HasValue)
        {
            _originHns = sourceTimeHns;
            _nextEligibleHns = sourceTimeHns + _minimumIntervalHns;
        }
        else
        {
            // Keep cadence across display timestamp rounding. After a slow frame,
            // skip missed slots rather than immediately replaying them in a burst.
            _nextEligibleHns = Math.Max(_nextEligibleHns + _minimumIntervalHns,
                sourceTimeHns + _minimumIntervalHns / 2);
        }
        sampleTimeHns = sourceTimeHns - _originHns.Value;
        return true;
    }

    // Convert an absolute offset instead of accumulating rounded per-frame deltas.
    public static uint ToRtpTimestamp(long sampleTimeHns) => unchecked((uint)(sampleTimeHns * 9 / 1000));
}
