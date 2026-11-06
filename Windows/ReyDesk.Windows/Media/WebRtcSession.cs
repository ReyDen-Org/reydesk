using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using ReyDesk.Windows.Input;
using ReyDesk.Windows.Signaling;
using ReyDesk.Windows.Rendezvous;
using ReyDesk.Windows.Settings;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using ReyDesk.Protocol.V1;
using Google.Protobuf;

namespace ReyDesk.Windows.Media;

/// <summary>
/// Wraps one SIPSorcery <see cref="RTCPeerConnection"/> for a single paired device.
/// Windows always offers (spec decision - it owns the video source) and always sends,
/// never receives, video (screen-share direction is fixed: Windows is captured,
/// Android views/controls); the three data channels are
/// negotiated with fixed IDs on both sides (see docs/protocol.md) so no DCEP handshake
/// race is possible, and are created before <c>createOffer()</c> since SIPSorcery only
/// emits the SDP `m=application` section if a data channel already exists. SDP/ICE are
/// relayed over the same <see cref="SignalingConnection"/> used for pairing - no
/// signaling server in local mode.
/// </summary>
internal sealed class WebRtcSession : IDisposable
{
    private readonly StreamPreferences _hostPreferences;
    private StreamPreferences _preferences;

    private readonly SignalingConnection _signaling;
    private readonly string _sessionId;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _controlChannel;
    private ScreenCapture? _capture;
    private MediaFoundation.H264Encoder? _encoder;
    private readonly object _videoLock = new();
    private bool _videoStarted;
    private int _disposed;
    private int _answerApplied;
    private volatile bool _videoEnabled = true;
    private uint? _rtpTimestampBase;
    private Action<byte[], int, int, long>? _captureHandler;
    private long _captureTimeOffsetHns;
    private double _lastVideoLogSeconds = -5;
    private readonly string _hostPreset;
    private AdaptiveQuality? _adaptive;
    private int _adaptPending;
    private readonly object _controlWriteLock = new();

    public event Action? ControlChannelOpen;
    public event Action<byte[]>? ControlMessageReceived;
    public event Action<RTCPeerConnectionState>? ConnectionStateChanged;
    public event Action<NegotiatedConfig>? NegotiatedConfigChanged;
    /// <summary>Raised when the selected ICE path (LAN / direct internet / relayed) is first known or changes.</summary>
    public event Action<PathKind>? PathChanged;

    private readonly IReadOnlyList<IceServerInfo> _iceServers;
    private readonly bool _remote;
    private PathKind _path = PathKind.PathUnspecified;
    public PathKind Path => _path;

    /// <param name="iceServers">LAN: empty (no external STUN traffic). Remote: STUN from the server config, TURN only when the
    /// owner allowed relay (RemoteService filters this).</param>
    /// <param name="remote">True for a rendezvous-pipe session: the path is detected from the nominated ICE pair.</param>
    public WebRtcSession(SignalingConnection signaling, string sessionId, StreamPreferences? preferences = null, string preset = "Custom",
        IReadOnlyList<IceServerInfo>? iceServers = null, bool remote = false)
    {
        _iceServers = iceServers ?? Array.Empty<IceServerInfo>();
        _remote = remote;
        if (!remote) _path = PathKind.PathLanDirect;
        _signaling = signaling;
        _sessionId = sessionId;
        _hostPreferences = preferences is null ? new StreamPreferences() :
            StreamPreferences.Normalize(preferences.MaxHeight, preferences.MaxFps, preferences.BitrateKbps);
        _preferences = _hostPreferences;
        _hostPreset = preset;
        _signaling.SessionAnswerReceived += OnRemoteAnswer;
        _signaling.IceCandidateReceived += OnRemoteIceCandidate;
    }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var configuration = new RTCConfiguration();
        if (_iceServers.Count > 0)
        {
            configuration.iceServers = new List<RTCIceServer>();
            foreach (var server in _iceServers)
                foreach (var url in server.Urls)
                    configuration.iceServers.Add(new RTCIceServer { urls = url, username = server.Username, credential = server.Credential });
        }
        Console.WriteLine($"[WebRtcSession {_sessionId}] ICE servers: {string.Join(' ', _iceServers.SelectMany(x => x.Urls))} remote={_remote}");
        var pc = new RTCPeerConnection(configuration);
        lock (_videoLock)
        {
            if (_disposed != 0) { pc.Close("session already disposed"); return; }
            _pc = pc;
        }

        // Capture starts only after the answer/preferences and ICE/DTLS are ready.
        // The video m-line itself does not require a live capture source.
        var videoTrack = new MediaStreamTrack(
            new List<VideoFormat> { new(VideoCodecsEnum.H264, 100, parameters: "packetization-mode=1") },
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            Console.WriteLine($"[WebRtcSession {_sessionId}] local candidate: {candidate.type} {candidate.protocol} {candidate.address}:{candidate.port}");
            _signaling.Send(new Envelope
            {
                ProtocolVersion = 1,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IceCandidate = new IceCandidate
                {
                    SessionId = _sessionId,
                    // SDP candidate attributes start with "candidate:"; SIPSorcery omits it and libwebrtc rejects the line.
                    Candidate = candidate.candidate.StartsWith("candidate:", StringComparison.Ordinal) ? candidate.candidate : "candidate:" + candidate.candidate,
                    SdpMid = candidate.sdpMid ?? "",
                    SdpMLineIndex = candidate.sdpMLineIndex,
                },
            });
        };
        _pc.onconnectionstatechange += state =>
        {
            Console.WriteLine($"[WebRtcSession {_sessionId}] connection state: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                StartVideoPipeline();
                if (_remote) _ = Task.Run(DetectPathAsync);
            }
            else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                StopVideoPipeline();
            }
            ConnectionStateChanged?.Invoke(state);
        };
        _pc.oniceconnectionstatechange += state => Console.WriteLine($"[WebRtcSession {_sessionId}] ICE state: {state}");

        // Fixed IDs, negotiated=true on both sides - see docs/protocol.md transport table.
        _controlChannel = await _pc.createDataChannel("control", new RTCDataChannelInit { negotiated = true, id = 0, ordered = true });
        _controlChannel.onopen += () =>
        {
            Console.WriteLine($"[WebRtcSession {_sessionId}] control channel open");
            SendNegotiatedConfig();
            ControlChannelOpen?.Invoke();
        };
        _controlChannel.onmessage += (_, _, data) => ControlMessageReceived?.Invoke(data);

        // Fixed IDs, created now because SIPSorcery requires all negotiated channels to
        // exist before createOffer() - see the control channel comment above.
        var inputChannel = await _pc.createDataChannel("input", new RTCDataChannelInit { negotiated = true, id = 1, ordered = false, maxRetransmits = 0 });
        inputChannel.onmessage += (_, _, data) => DispatchInputMessage(data);
        var inputDiscreteChannel = await _pc.createDataChannel("input-discrete", new RTCDataChannelInit { negotiated = true, id = 2, ordered = true });
        inputDiscreteChannel.onmessage += (_, _, data) => DispatchInputMessage(data);

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);

        if (Volatile.Read(ref _disposed) != 0) return;
        _signaling.Send(new Envelope
        {
            ProtocolVersion = 1,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionOffer = new SessionOffer { SessionId = _sessionId, Sdp = offer.sdp },
        });
    }

    /// <summary>Polls the nominated candidate pair (same rules as Android WebRtcSession.classifyPath): relay on either side =
    /// relayed, srflx/prflx = direct internet, host/host on private ranges = LAN. Re-checked every 5 s for path changes.</summary>
    private async Task DetectPathAsync()
    {
        try
        {
            for (var i = 0; Volatile.Read(ref _disposed) == 0 && _pc?.connectionState == RTCPeerConnectionState.connected; i++)
            {
                var kind = ClassifyPath();
                if (kind is { } found && found != _path) { _path = found; PathChanged?.Invoke(found); }
                await Task.Delay(i < 20 ? 500 : 5000).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[WebRtcSession {_sessionId}] path detection stopped: {ex.GetType().Name}"); }
    }

    private PathKind? ClassifyPath()
    {
        var channel = _pc?.GetRtpChannel() as RtpIceChannel;
        var entry = channel?.NominatedEntry;
        if (entry is null) return null;
        var local = entry.LocalCandidate;
        var remoteCandidate = entry.RemoteCandidate;
        var types = new[] { local.type, remoteCandidate.type };
        if (types.Contains(RTCIceCandidateType.relay)) return PathKind.PathRelayed;
        if (types.Contains(RTCIceCandidateType.srflx) || types.Contains(RTCIceCandidateType.prflx)) return PathKind.PathRemoteDirect;
        if (types.All(t => t == RTCIceCandidateType.host))
            return IsPrivate(local.address) && IsPrivate(remoteCandidate.address) ? PathKind.PathLanDirect : PathKind.PathRemoteDirect;
        return null;
    }

    private static bool IsPrivate(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        if (address.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xFE) == 0xFC;
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public bool TrySendControl(Envelope envelope)
    {
        if (Volatile.Read(ref _disposed) != 0 || _controlChannel?.readyState != RTCDataChannelState.open) return false;
        envelope.ProtocolVersion = 1;
        envelope.SessionId = _sessionId;
        envelope.SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SendControlMessage(envelope.ToByteArray());
        return true;
    }
    public void SendControlMessage(byte[] data)
    {
        lock (_controlWriteLock)
        {
            if (_disposed != 0 || _controlChannel?.readyState != RTCDataChannelState.open) return;
            if (data.Length > 65536 || _controlChannel.bufferedAmount + (ulong)data.Length > 262144)
            {
                Console.WriteLine("[Companion] Control queue limit reached; disconnecting for clean resynchronization.");
                _ = Task.Run(_signaling.Dispose);
                return;
            }
            try { _controlChannel.send(data); }
            catch (Exception) { Console.WriteLine("[Companion] Control send failed; disconnecting."); _ = Task.Run(_signaling.Dispose); }
        }
    }

    public void ObserveConnectionQuality(StreamStats stats)
    {
        if (_adaptive is null || Interlocked.CompareExchange(ref _adaptPending, 1, 0) != 0) return;
        var next = _adaptive.Observe(stats.PacketLossAvailable ? stats.PacketLossPercent : null,
            stats.RoundTripTimeAvailable ? stats.RoundTripTimeMs : null, Environment.TickCount64);
        if (next is null) { Volatile.Write(ref _adaptPending, 0); return; }
        _ = Task.Run(() =>
        {
            try
            {
                StopVideoPipeline();
                lock (_videoLock)
                {
                    if (_disposed != 0) return;
                    _preferences = next;
                }
                SendNegotiatedConfig();
                if (_pc?.connectionState == RTCPeerConnectionState.connected) StartVideoPipeline();
            }
            finally { Volatile.Write(ref _adaptPending, 0); }
        });
    }
    public void SetDesktopEnabled(bool enabled)
    {
        _videoEnabled = enabled;
        if (!enabled) StopVideoPipeline();
        else if (_pc?.connectionState == RTCPeerConnectionState.connected) StartVideoPipeline();
    }

    /// <summary>Injects a remote input event carried on either the unordered "input"
    /// (mouse move/scroll) or reliable "input-discrete" (clicks/text) channel - see
    /// docs/protocol.md's data channel lanes table.</summary>
    private void DispatchInputMessage(byte[] data)
    {
        Envelope envelope;
        try
        {
            if (data.Length > 16384 || Volatile.Read(ref _disposed) != 0) return;
            envelope = Envelope.Parser.ParseFrom(data);
            if (envelope.ProtocolVersion != 1 || envelope.SessionId != _sessionId) return;
        }
        catch (Exception)
        {
            return; // malformed - not worth crashing the session over
        }

        switch (envelope.PayloadCase)
        {
            case Envelope.PayloadOneofCase.MouseMove:
                InputInjector.MoveTo(envelope.MouseMove.X, envelope.MouseMove.Y);
                break;
            case Envelope.PayloadOneofCase.MouseButton:
                InputInjector.SendMouseButtonEvent(envelope.MouseButton.Button, envelope.MouseButton.IsDown, envelope.MouseButton.X, envelope.MouseButton.Y);
                break;
            case Envelope.PayloadOneofCase.MouseScroll:
                InputInjector.Scroll(envelope.MouseScroll.DeltaX, envelope.MouseScroll.DeltaY);
                break;
            case Envelope.PayloadOneofCase.TextInput:
                InputInjector.TypeText(envelope.TextInput.Text);
                break;
            case Envelope.PayloadOneofCase.KeyEvent:
                InputInjector.SendKeyEvent(envelope.KeyEvent.HidUsageCode, envelope.KeyEvent.IsDown);
                break;
        }
    }

    /// <summary>Starts encoding captured frames and sending them once ICE/DTLS is up -
    /// starting earlier would just burn CPU encoding frames nobody can receive yet.</summary>
    private void StartVideoPipeline()
    {
        lock (_videoLock)
        {
            if (_disposed != 0 || _videoStarted || !_videoEnabled) return;
            _videoStarted = true;
        }
        try
        {
            var capture = ScreenCapture.StartForPrimaryMonitor(_preferences.MaxFps, _preferences.MaxHeight);
            lock (_videoLock)
            {
                if (_disposed != 0 || !_videoStarted) { DisposeCaptureDeferred(capture); return; }
                _capture = capture;
                // Create encoder on the first actual frame, not initial item size.
                _captureTimeOffsetHns = _sendClock.Elapsed.Ticks;
                _captureHandler = (data, width, height, time) => OnFrameCaptured(capture, data, width, height, time);
                capture.FrameCaptured += _captureHandler;
            }
        }
        catch (Exception ex)
        {
            lock (_videoLock) _videoStarted = false;
            Console.WriteLine($"[WebRtcSession {_sessionId}] capture start failed - no video will be sent: {ex}");
        }
    }

    private long _sentFrameCount;
    private readonly System.Diagnostics.Stopwatch _sendClock = System.Diagnostics.Stopwatch.StartNew();

    private void OnFrameCaptured(ScreenCapture source, byte[] nv12, int width, int height, long sampleTimeHns)
    {
        lock (_videoLock)
        {
            if (_disposed != 0 || !_videoStarted || _pc is null || !ReferenceEquals(source, _capture)) return;
            sampleTimeHns += _captureTimeOffsetHns;
            try
            {
                if (_encoder is null || _encoder.Width != width || _encoder.Height != height)
                {
                    _encoder?.Dispose();
                    _encoder = null;
                    _encoder = MediaFoundation.H264Encoder.Create(width, height, _preferences.MaxFps, _preferences.BitrateKbps);
                    Console.WriteLine($"[WebRtcSession {_sessionId}] H264Encoder created ({width}x{height} @ max {_preferences.MaxFps}fps, target {_preferences.BitrateKbps}kbps)");
                    SendNegotiatedConfig(width, height);
                }
                var encodeStart = Stopwatch.GetTimestamp();
                var accessUnits = _encoder.EncodeFrame(nv12, sampleTimeHns);
                var sendStart = Stopwatch.GetTimestamp();
                foreach (var accessUnit in accessUnits)
                {
                    // Use the explicit RTP timestamp API: track.Timestamp has an
                    // internal setter, and SendVideo advances it only after sending.
                    _rtpTimestampBase ??= _pc.VideoLocalTrack.Timestamp;
                    var timestamp = unchecked(_rtpTimestampBase.Value + VideoFrameTiming.ToRtpTimestamp(accessUnit.SampleTimeHns));
                    var payloadType = _pc.VideoStream.GetSendingFormat().ID;
                    foreach (var nal in H264Packetiser.ParseNals(accessUnit.Data))
                        H264RtpSender.SendNal(nal.NAL, nal.IsLast, timestamp, payloadType,
                            (payload, pts, marker, id) => _pc.SendRtpRaw(SDPMediaTypesEnum.video, payload, pts, marker, id));
                    _sentFrameCount++;
                }
                if (_sendClock.Elapsed.TotalSeconds - _lastVideoLogSeconds >= 5)
                {
                    _lastVideoLogSeconds = _sendClock.Elapsed.TotalSeconds;
                    var outputLagMs = accessUnits.Count == 0 ? double.NaN : (sampleTimeHns - accessUnits[^1].SampleTimeHns) / 10_000.0;
                    Console.WriteLine($"[WebRtcSession {_sessionId}] sent={_sentFrameCount} encode={Stopwatch.GetElapsedTime(encodeStart, sendStart).TotalMilliseconds:F1}ms send={Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds:F1}ms outputs={accessUnits.Count} pts={sampleTimeHns / 10_000.0:F1}ms mfPtsLag={outputLagMs:F1}ms");
                }
            }
            catch (Exception ex)
            {
                // A broken encoder fails identically on every single frame - retrying
                // forever just spams the log at 15/sec with no chance of self-recovering
                // (a real, observed problem on the first hardware test, not theoretical).
                // Treat it as fatal for this session's video, same as a Create() failure.
                Console.WriteLine($"[WebRtcSession {_sessionId}] frame encode/send failed, stopping video for this session: {ex}");
                StopVideoPipeline();
            }
        }
    }

    private void StopVideoPipeline()
    {
        ScreenCapture? capture;
        MediaFoundation.H264Encoder? encoder;
        lock (_videoLock)
        {
            _videoStarted = false;
            capture = _capture;
            _capture = null;
            if (capture is not null && _captureHandler is not null) capture.FrameCaptured -= _captureHandler;
            _captureHandler = null;
            encoder = _encoder;
            _encoder = null;
        }
        try { encoder?.Dispose(); }
        finally { if (capture is not null) DisposeCaptureDeferred(capture); }
    }

    private void DisposeCaptureDeferred(ScreenCapture capture)
    {
        // OnFrameCaptured can call this while the capture callback owns its frame
        // lock. Dispose on a worker after detaching, never from that WGC callback or
        // while holding the video lock (capture -> video is the normal lock order).
        _ = Task.Run(() =>
        {
            try { capture.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[WebRtcSession {_sessionId}] capture teardown failed: {ex}"); }
        });
    }

    private void OnRemoteAnswer(SessionAnswer answer)
    {
        if (answer.SessionId != _sessionId) return;
        // This session has a fixed capture/encoder configuration. A repeated answer
        // must not silently change its limits after capture has already started.
        if (Interlocked.Exchange(ref _answerApplied, 1) != 0) return;
        var request = answer.RequestedConfig;
        _videoEnabled = !answer.ControlOnly;
        _preferences = _hostPreferences.LimitRequest(
            (int)(request?.HeightPx ?? 0), (int)(request?.Fps ?? 0), (int)(request?.TargetBitrateKbps ?? 0));
        if (answer.QualityPreset == "Auto" || _hostPreset == "Auto") _adaptive = new AdaptiveQuality(_preferences);
        Console.WriteLine($"[WebRtcSession {_sessionId}] stream limits {_preferences.MaxHeight}p / {_preferences.MaxFps}fps / {_preferences.BitrateKbps}kbps");
        _pc?.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer.Sdp });
    }

    private void SendNegotiatedConfig(int width = 0, int height = 0)
    {
        var config = new NegotiatedConfig
        {
            VideoCodec = VideoCodec.H264,
            WidthPx = (uint)(width > 0 ? width : _capture?.EncodeWidth ?? 0),
            HeightPx = (uint)(height > 0 ? height : _capture?.EncodeHeight ?? 0),
            Fps = (uint)_preferences.MaxFps, TargetBitrateKbps = (uint)_preferences.BitrateKbps,
        };
        NegotiatedConfigChanged?.Invoke(config);
        if (_controlChannel?.readyState != RTCDataChannelState.open) return;
        SendControlMessage(new Envelope
        {
            ProtocolVersion = 1, SessionId = _sessionId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NegotiatedConfig = config,
        }.ToByteArray());
    }

    private void OnRemoteIceCandidate(IceCandidate ice)
    {
        if (ice.SessionId != _sessionId) return;
        Console.WriteLine($"[WebRtcSession {_sessionId}] remote candidate: {ice.Candidate}");
        _pc?.addIceCandidate(new RTCIceCandidateInit { candidate = ice.Candidate, sdpMid = ice.SdpMid, sdpMLineIndex = (ushort)ice.SdpMLineIndex });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _signaling.SessionAnswerReceived -= OnRemoteAnswer;
        _signaling.IceCandidateReceived -= OnRemoteIceCandidate;
        try { StopVideoPipeline(); }
        finally { _pc?.Close("session disposed"); }
    }
}
