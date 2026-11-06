using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Signaling;

/// <summary>
/// One long-lived TCP connection to a paired Android device - carries the pairing
/// handshake (Pairing/PairingListener.cs) and, once paired, WebRTC SDP/ICE signaling
/// (Media/WebRtcSession.cs), all as length-delimited <see cref="Envelope"/> messages on
/// the same socket (see docs/protocol.md "Pairing & signaling transport" - there is no
/// separate signaling server in local mode). Unlike the pairing-only exchange this
/// replaces, the connection is kept open and read continuously rather than closed after
/// one request/response.
/// </summary>
internal sealed class SignalingConnection : IDisposable
{
    private readonly TcpClient? _client;
    private readonly IDisposable? _owner;
    private readonly System.IO.Stream _stream;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;
    private int _disposed;
    private int _closedRaised;
    public bool WasUnattendedReconnect { get; }
    private readonly byte[] _authenticatedPeerPublicKey;
    public byte[] AuthenticatedPeerPublicKey => _authenticatedPeerPublicKey.ToArray();

    public event Action<SessionOffer>? SessionOfferReceived;
    public event Action<SessionAnswer>? SessionAnswerReceived;
    public event Action<IceCandidate>? IceCandidateReceived;
    /// <summary>Raised once, when the read loop ends for any reason (peer disconnected,
    /// malformed data, etc.) or local Dispose closes the socket.</summary>
    public event Action? Closed;

    public SignalingConnection(TcpClient client, System.IO.Stream? authenticatedStream = null, bool wasUnattendedReconnect = false,
        byte[]? authenticatedPeerPublicKey = null)
    {
        _client = client;
        _stream = authenticatedStream ?? client.GetStream();
        WasUnattendedReconnect = wasUnattendedReconnect;
        _authenticatedPeerPublicKey = authenticatedPeerPublicKey?.ToArray() ?? Array.Empty<byte>();
    }

    /// <summary>Over an already authenticated stream (pinned TLS; the host side presents the server certificate) that is not a
    /// plain socket: a LAN stream or a rendezvous relay pipe. <paramref name="owner"/> closes the stream and its transport
    /// (PairedSession.Owner).</summary>
    public SignalingConnection(System.IO.Stream authenticatedStream, IDisposable owner, bool wasUnattendedReconnect,
        byte[] authenticatedPeerPublicKey)
    {
        _client = null;
        _owner = owner;
        _stream = authenticatedStream;
        WasUnattendedReconnect = wasUnattendedReconnect;
        _authenticatedPeerPublicKey = authenticatedPeerPublicKey.ToArray();
    }

    /// <summary>Starts reading Envelopes off the socket in the background until it closes.</summary>
    public void StartReadLoop()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReadLoop(token));
    }

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Clean EOF returns null; truncated/malformed input throws. Both
                // paths end ownership of the socket and its media session.
                var envelope = EnvelopeFraming.Read(_stream);
                if (envelope is null) break; // Clean EOF.
                Dispatch(envelope);
            }
        }
        catch (Exception)
        {
            // Connection dropped/malformed data - not worth crashing over, just close.
        }
        finally
        {
            Dispose();
        }
    }

    private void Dispatch(Envelope envelope)
    {
        switch (envelope.PayloadCase)
        {
            case Envelope.PayloadOneofCase.PairingRequest:
                throw new System.IO.InvalidDataException("Pairing cannot be repeated on an authenticated connection.");
            case Envelope.PayloadOneofCase.SessionOffer:
                SessionOfferReceived?.Invoke(envelope.SessionOffer);
                break;
            case Envelope.PayloadOneofCase.SessionAnswer:
                SessionAnswerReceived?.Invoke(envelope.SessionAnswer);
                break;
            case Envelope.PayloadOneofCase.IceCandidate:
                IceCandidateReceived?.Invoke(envelope.IceCandidate);
                break;
        }
    }

    /// <summary>Writes one Envelope to the socket. Safe to call from any thread -
    /// NetworkStream itself isn't safe for concurrent writers.</summary>
    public void Send(Envelope envelope)
    {
        lock (_writeLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { EnvelopeFraming.Write(_stream, envelope); }
            catch (Exception) when (Volatile.Read(ref _disposed) != 0)
            {
                Console.WriteLine("[SignalingConnection] pending write cancelled by local teardown");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts?.Cancel();
        _client?.Dispose();
        _stream.Dispose();
        _owner?.Dispose();
        // The read loop may be blocked in a socket read. Closing locally must tear
        // down the session immediately, rather than depending on its next callback.
        try
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke();
        }
        finally { _cts?.Dispose(); }
    }
}
