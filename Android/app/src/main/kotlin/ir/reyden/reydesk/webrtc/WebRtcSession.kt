package ir.reyden.reydesk.webrtc

import android.os.Handler
import android.os.Looper
import android.util.Log
import ir.reyden.reydesk.pairing.SignalingConnection
import ir.reyden.reydesk.protocol.v1.Envelope
import ir.reyden.reydesk.protocol.v1.KeyEvent
import ir.reyden.reydesk.protocol.v1.MouseButton
import ir.reyden.reydesk.protocol.v1.MouseButtonEvent
import ir.reyden.reydesk.protocol.v1.MouseMove
import ir.reyden.reydesk.protocol.v1.MouseScroll
import ir.reyden.reydesk.protocol.v1.SessionAnswer
import ir.reyden.reydesk.protocol.v1.SessionOffer
import ir.reyden.reydesk.protocol.v1.TextInput
import ir.reyden.reydesk.protocol.v1.NegotiatedConfig
import ir.reyden.reydesk.protocol.v1.PathKind
import ir.reyden.reydesk.protocol.v1.StreamStats
import ir.reyden.reydesk.protocol.v1.VideoCodecInUse
import ir.reyden.reydesk.settings.StreamPreferences
import android.os.SystemClock
import org.webrtc.DataChannel
import org.webrtc.IceCandidate
import org.webrtc.IceCandidateErrorEvent
import org.webrtc.MediaConstraints
import org.webrtc.MediaStream
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RTCStatsReport
import org.webrtc.RtpReceiver
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.VideoTrack
import java.nio.ByteBuffer
import java.time.Instant
import java.util.Locale
import ir.reyden.reydesk.protocol.v1.IceCandidate as ProtoIceCandidate

private const val Tag = "WebRtcSession"

/**
 * Wraps one org.webrtc PeerConnection, answering Windows's offer - Windows always
 * offers and always sends video (it owns the capture source; Android only ever
 * receives). Mirrors
 * Windows/ReyDesk.Windows/Media/WebRtcSession.cs. Data channels are negotiated with
 * fixed IDs on both sides (see docs/protocol.md), created as soon as the offer arrives.
 */
class WebRtcSession(factory: PeerConnectionFactory, private val signaling: SignalingConnection,
    private val preferences: StreamPreferences = StreamPreferences(), private val controlOnly: Boolean = false,
    /** LAN: empty (no external STUN traffic). Remote: STUN, plus TURN only when the user allowed relay. */
    iceServers: List<PeerConnection.IceServer> = emptyList(), private val remote: Boolean = false) {
    private var sessionId: String = ""
    private var controlChannel: DataChannel? = null
    private var inputChannel: DataChannel? = null
    private var inputDiscreteChannel: DataChannel? = null
    private val diagnosticsHandler = Handler(Looper.getMainLooper())
    private val diagnosticsLock = Any()
    @Volatile private var closed = false
    @Volatile private var connected = false
    private var statsPending = false
    private var previousVideoStats = emptyMap<String, Map<String, Any>>()
    private var previousStatsAtMs: Long? = null
    private val diagnosticsPoll = object : Runnable {
        override fun run() {
            synchronized(diagnosticsLock) {
                if (closed) return
                if (connected && !statsPending) {
                    statsPending = true
                    peerConnection.getStats { report ->
                        // Native callbacks run on WebRTC's signaling thread. Never
                        // block it on close(), which may be waiting for that thread.
                        diagnosticsHandler.post {
                            synchronized(diagnosticsLock) {
                                statsPending = false
                                if (!closed) logReceiverStats(report)
                            }
                        }
                    }
                }
                if (!connected && !statsPending) {
                    statsPending = true
                    peerConnection.getStats { report ->
                        diagnosticsHandler.post {
                            synchronized(diagnosticsLock) {
                                statsPending = false
                                if (!closed) logPairStats(report)
                            }
                        }
                    }
                }
                diagnosticsHandler.postDelayed(this, 2000)
            }
        }
    }

    /** Pre-connection ICE diagnostics: each candidate pair with its check counters. */
    private fun logPairStats(report: RTCStatsReport) {
        val stats = report.statsMap
        fun cand(id: Any?) = (id as? String)?.let(stats::get)?.members?.let { "${it["candidateType"]}/${it["protocol"]} ${it["address"] ?: it["ip"]}:${it["port"]}" }
        stats.values.filter { it.type == "candidate-pair" }.forEach {
            val m = it.members
            Log.d(Tag, "pair ${cand(m["localCandidateId"])} -> ${cand(m["remoteCandidateId"])} state=${m["state"]} sent=${m["requestsSent"]} recvResp=${m["responsesReceived"]} recvReq=${m["requestsReceived"]}")
        }
    }

    var onControlChannelOpen: (() -> Unit)? = null
    var onControlMessage: ((ByteArray) -> Unit)? = null
    var onConnectionStateChanged: ((PeerConnection.PeerConnectionState) -> Unit)? = null
    var onRemoteVideoTrack: ((VideoTrack) -> Unit)? = null
    var onMetrics: ((ConnectionMetrics) -> Unit)? = null
    /** Raised when the selected ICE pair's kind changes (from WebRTC stats, never assumed). */
    var onPathChanged: ((PathKind) -> Unit)? = null
    private var lastPath = PathKind.PATH_UNSPECIFIED

    private val peerConnection: PeerConnection = requireNotNull(
        factory.createPeerConnection(
            PeerConnection.RTCConfiguration(iceServers.also { Log.d(Tag, "ICE servers: ${it.flatMap { s -> s.urls }} remote=$remote") }).apply {
                sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
                if (remote) {
                    tcpCandidatePolicy = PeerConnection.TcpCandidatePolicy.ENABLED
                    continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
                    iceTransportsType = PeerConnection.IceTransportsType.ALL
                }
            },
            object : PeerConnection.Observer {
                override fun onIceCandidate(candidate: IceCandidate) {
                    Log.d(Tag, "local candidate: ${candidate.sdp}")
                    signaling.send(
                        envelopeOf(
                            iceCandidate = ProtoIceCandidate.newBuilder()
                                .setSessionId(sessionId)
                                .setCandidate(candidate.sdp)
                                .setSdpMid(candidate.sdpMid ?: "")
                                .setSdpMLineIndex(candidate.sdpMLineIndex)
                                .build(),
                        ),
                    )
                }
                override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
                    Log.d(Tag, "connection state: $newState")
                    connected = newState == PeerConnection.PeerConnectionState.CONNECTED
                    onConnectionStateChanged?.invoke(newState)
                }
                override fun onIceConnectionChange(newState: PeerConnection.IceConnectionState) {
                    Log.d(Tag, "ICE state: $newState")
                }
                override fun onIceCandidateError(event: IceCandidateErrorEvent) {
                    Log.w(Tag, "ICE candidate error ${event.errorCode} ${event.errorText} url=${event.url} addr=${event.address}:${event.port}")
                }
                override fun onSignalingChange(newState: PeerConnection.SignalingState) {}
                override fun onIceConnectionReceivingChange(receiving: Boolean) {}
                override fun onIceGatheringChange(newState: PeerConnection.IceGatheringState) { Log.d(Tag, "ICE gathering: $newState") }
                override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>) {}
                override fun onAddStream(stream: MediaStream) {}
                override fun onRemoveStream(stream: MediaStream) {}
                override fun onDataChannel(dataChannel: DataChannel) {}
                override fun onRenegotiationNeeded() {}
                override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) {
                    val track = receiver.track()
                    if (track is VideoTrack) {
                        Log.d(Tag, "remote video track added")
                        onRemoteVideoTrack?.invoke(track)
                    }
                }
            },
        ),
    ) { "createPeerConnection returned null" }

    init {
        signaling.onSessionOffer = ::onRemoteOffer
        signaling.onIceCandidate = ::onRemoteIceCandidate
        diagnosticsHandler.postDelayed(diagnosticsPoll, 2000)
    }

    /** Receiver-only intervals, not end-to-end latency. Comparing these with the
     * host stage timings distinguishes network jitter from decode/render pressure. */
    private fun logReceiverStats(report: RTCStatsReport) {
        val stats = report.statsMap
        val selectedPairId = stats.values.firstOrNull {
            it.type == "transport" && it.members["selectedCandidatePairId"] is String
        }?.members?.get("selectedCandidatePairId") as? String
        val pair = selectedPairId?.let(stats::get) ?: stats.values.firstOrNull {
            it.type == "candidate-pair" && it.members["state"] == "succeeded" &&
                (it.members["nominated"] == true || it.members["selected"] == true)
        }
        pair?.let { classifyPath(stats, it.members) }?.let { kind ->
            if (kind != lastPath) { lastPath = kind; onPathChanged?.invoke(kind) }
        }
        fun Map<String, Any>.number(name: String) = (this[name] as? Number)?.toDouble()
        fun metric(value: Double?) = value?.takeIf { it.isFinite() }
            ?.let { String.format(Locale.ROOT, "%.1f", it) } ?: "n/a"
        val rttMs = pair?.members?.number("currentRoundTripTime")?.times(1000)
        val inbound = stats.values.filter {
            it.type == "inbound-rtp" && (it.members["kind"] == "video" || it.members["mediaType"] == "video")
        }.take(4)
        val details = inbound.joinToString("; ") { video ->
            val now = video.members
            val before = previousVideoStats[video.id]
            fun interval(total: String, count: String) = receiverIntervalMeanMs(
                now.number(total), now.number(count), before?.number(total), before?.number(count),
            )
            "video=${video.id} received=${metric(now.number("framesReceived"))}" +
                " decoded=${metric(now.number("framesDecoded"))} dropped=${metric(now.number("framesDropped"))}" +
                " lostPackets=${metric(now.number("packetsLost"))} jitterMs=${metric(now.number("jitter")?.times(1000))}" +
                " bufferMs=${metric(interval("jitterBufferDelay", "jitterBufferEmittedCount"))}" +
                " decodeMs=${metric(interval("totalDecodeTime", "framesDecoded"))}" +
                " processingMs=${metric(interval("totalProcessingDelay", "framesDecoded"))}" +
                " freezes=${metric(now.number("freezeCount"))}"
        }
        val sampledAt = SystemClock.elapsedRealtime()
        val seconds = previousStatsAtMs?.let { (sampledAt - it) / 1000.0 }
        val video = inbound.firstOrNull()
        val current = video?.members
        val previous = video?.id?.let(previousVideoStats::get)
        fun valid(value: Double?) = value?.takeIf { it.isFinite() && it >= 0 }
        val fps = receiverCounterRate(current?.number("framesDecoded"), previous?.number("framesDecoded"), seconds)
        val bitrate = receiverCounterRate(current?.number("bytesReceived"), previous?.number("bytesReceived"), seconds)?.times(0.008)
        val loss = receiverPacketLossPercent(current?.number("packetsLost"), current?.number("packetsReceived"),
            previous?.number("packetsLost"), previous?.number("packetsReceived"))
        val metrics = ConnectionMetrics(fps = fps, bitrateKbps = bitrate, roundTripTimeMs = valid(rttMs),
            packetLossPercent = loss, jitterMs = valid(current?.number("jitter")?.times(1000)),
            width = valid(current?.number("frameWidth"))?.toInt() ?: 0,
            height = valid(current?.number("frameHeight"))?.toInt() ?: 0,
            framesDropped = valid(current?.number("framesDropped"))?.toLong() ?: 0)
        onMetrics?.invoke(metrics)
        if (controlChannel?.state() == DataChannel.State.OPEN) {
            val message = StreamStats.newBuilder().setCodecInUse(VideoCodecInUse.VIDEO_CODEC_IN_USE_H264)
                .setFpsAvailable(fps != null).setFps(fps?.toFloat() ?: 0f)
                .setBitrateAvailable(bitrate != null).setBitrateKbps(bitrate?.toInt() ?: 0)
                .setRoundTripTimeAvailable(metrics.roundTripTimeMs != null).setRoundTripTimeMs(metrics.roundTripTimeMs?.toInt() ?: 0)
                .setPacketLossAvailable(loss != null).setPacketLossPercent(loss?.toFloat() ?: 0f)
                .setJitterAvailable(metrics.jitterMs != null).setJitterMs(metrics.jitterMs?.toFloat() ?: 0f)
                .setWidthPx(metrics.width).setHeightPx(metrics.height).setFramesDropped(metrics.framesDropped.toInt()).build()
            sendControlMessage(Envelope.newBuilder().setProtocolVersion(1).setSentAtUnixMs(Instant.now().toEpochMilli())
                .setStreamStats(message).build().toByteArray())
        }
        previousStatsAtMs = sampledAt
        previousVideoStats = inbound.associate { it.id to it.members.toMap() }
        Log.d(Tag, "[receiver] rttMs=${metric(rttMs)} ${details.ifEmpty { "video=n/a" }}")
    }

    /** host/host on private ranges = LAN; srflx/prflx = direct internet; relay on either side = relayed. */
    private fun classifyPath(stats: Map<String, org.webrtc.RTCStats>, pair: Map<String, Any>): PathKind? {
        val local = (pair["localCandidateId"] as? String)?.let(stats::get)?.members ?: return null
        val remoteC = (pair["remoteCandidateId"] as? String)?.let(stats::get)?.members ?: return null
        val types = setOf(local["candidateType"] as? String, remoteC["candidateType"] as? String)
        return when {
            "relay" in types -> PathKind.PATH_RELAYED
            "srflx" in types || "prflx" in types -> PathKind.PATH_REMOTE_DIRECT
            types == setOf("host") -> if (isPrivate(local["address"] ?: local["ip"]) && isPrivate(remoteC["address"] ?: remoteC["ip"]))
                PathKind.PATH_LAN_DIRECT else PathKind.PATH_REMOTE_DIRECT
            else -> null
        }
    }

    private fun isPrivate(address: Any?): Boolean {
        val text = (address as? String)?.takeIf { it.isNotEmpty() } ?: return false
        if (text.endsWith(".local")) return true
        // Literals only: getByName would do a DNS lookup on the main thread for a hostname.
        if (!text.all { it.isDigit() || it == '.' || it == ':' || it in 'a'..'f' || it in 'A'..'F' || it == '%' }) return false
        return runCatching {
            val a = java.net.InetAddress.getByName(text)
            a.isSiteLocalAddress || a.isLinkLocalAddress || a.isLoopbackAddress ||
                (a.address.size == 16 && (a.address[0].toInt() and 0xFE) == 0xFC)
        }.getOrDefault(false)
    }

    private fun onRemoteOffer(offer: SessionOffer) {
        if (sessionId.isNotEmpty() || offer.sessionId.isBlank() || offer.sdp.length > 60000) return
        sessionId = offer.sessionId

        // Fixed IDs, negotiated=true on both sides - see docs/protocol.md transport table.
        controlChannel = peerConnection.createDataChannel("control", DataChannel.Init().apply { negotiated = true; id = 0; ordered = true })
        controlChannel?.registerObserver(object : DataChannel.Observer {
            override fun onBufferedAmountChange(previousAmount: Long) {}
            override fun onStateChange() {
                Log.d(Tag, "control channel state: ${controlChannel?.state()}")
                if (controlChannel?.state() == DataChannel.State.OPEN) onControlChannelOpen?.invoke()
            }
            override fun onMessage(buffer: DataChannel.Buffer) {
                if (closed || buffer.data.remaining() > 65536) return
                val bytes = ByteArray(buffer.data.remaining())
                buffer.data.get(bytes)
                val envelope = try { Envelope.parseFrom(bytes) } catch (_: Exception) { return }
                if (envelope.protocolVersion != 1 || envelope.sessionId != sessionId) return
                onControlMessage?.invoke(bytes)
            }
        })

        inputChannel = peerConnection.createDataChannel("input", DataChannel.Init().apply { negotiated = true; id = 1; ordered = false; maxRetransmits = 0 })
        inputDiscreteChannel = peerConnection.createDataChannel("input-discrete", DataChannel.Init().apply { negotiated = true; id = 2; ordered = true })

        peerConnection.setRemoteDescription(
            object : SdpObserverAdapter() {
                override fun onSetSuccess() {
                    peerConnection.createAnswer(
                        object : SdpObserverAdapter() {
                            override fun onCreateSuccess(description: SessionDescription) {
                                peerConnection.setLocalDescription(
                                    object : SdpObserverAdapter() {
                                        override fun onSetSuccess() {
                                            signaling.send(
                                                envelopeOf(
                                                    sessionAnswer = SessionAnswer.newBuilder()
                                                        .setSessionId(sessionId)
                                                        .setSdp(description.description)
                                                        .setControlOnly(controlOnly)
                                                        .setQualityPreset(preferences.preset)
                                                        .setRequestedConfig(NegotiatedConfig.newBuilder()
                                                            .setHeightPx(preferences.maxHeight).setFps(preferences.maxFps)
                                                            .setTargetBitrateKbps(preferences.bitrateKbps).build())
                                                        .build(),
                                                ),
                                            )
                                        }
                                    },
                                    description,
                                )
                            }
                        },
                        MediaConstraints(),
                    )
                }
            },
            SessionDescription(SessionDescription.Type.OFFER, offer.sdp),
        )
    }

    private fun onRemoteIceCandidate(ice: ProtoIceCandidate) {
        if (ice.sessionId != sessionId) return
        Log.d(Tag, "remote candidate: ${ice.candidate} mid='${ice.sdpMid}' idx=${ice.sdpMLineIndex}")
        // SDP candidate attributes start with "candidate:"; some peers omit it and libwebrtc then rejects the line.
        val line = ice.candidate.trim().let { if (it.startsWith("candidate:") || it.isEmpty()) it else "candidate:$it" }
        var ok = peerConnection.addIceCandidate(IceCandidate(ice.sdpMid, ice.sdpMLineIndex, line))
        if (!ok) {
            // Retry with the mid taken from the remote description (a peer may send an empty or foreign mid).
            val mids = Regex("a=mid:(\\S+)").findAll(peerConnection.remoteDescription?.description.orEmpty()).map { it.groupValues[1] }.toList()
            val mid = mids.getOrNull(ice.sdpMLineIndex.coerceAtLeast(0)) ?: mids.firstOrNull()
            if (mid != null) ok = peerConnection.addIceCandidate(IceCandidate(mid, ice.sdpMLineIndex.coerceAtLeast(0), line))
            Log.w(Tag, "addIceCandidate rejected first try; retry mid='$mid' mids=$mids ok=$ok")
        } else Log.d(Tag, "addIceCandidate ok")
    }

    fun sendControlMessage(data: ByteArray): Boolean {
        if (closed || sessionId.isBlank()) return false
        if (data.size > 65536) { signaling.close(); return false }
        val envelope = try { Envelope.parseFrom(data).toBuilder().setProtocolVersion(1).setSessionId(sessionId).build() } catch (_: Exception) { return false }
        val bytes = envelope.toByteArray()
        if (bytes.size > 65536 || (controlChannel?.bufferedAmount() ?: 0L) > 262144L) { signaling.close(); return false }
        if (controlChannel?.state() != DataChannel.State.OPEN) return false
        val sent = controlChannel?.send(DataChannel.Buffer(ByteBuffer.wrap(bytes), true)) == true
        if (!sent) signaling.close()
        return sent
    }

    fun sessionIdentity(): String = sessionId

    /** Pointer position, normalized [0,1] against Windows's captured display - sent on
     * the unordered/unreliable channel since a stale position is worthless. */
    fun sendMouseMove(x: Float, y: Float) {
        send(inputChannel, envelopeOf(mouseMove = MouseMove.newBuilder().setX(x).setY(y).build()))
    }

    fun sendMouseButton(button: MouseButton, isDown: Boolean, x: Float, y: Float) {
        send(
            inputDiscreteChannel,
            envelopeOf(
                mouseButton = MouseButtonEvent.newBuilder()
                    .setButton(button).setIsDown(isDown).setX(x).setY(y).build(),
            ),
        )
    }

    fun sendMouseScroll(deltaX: Float, deltaY: Float) {
        send(inputChannel, envelopeOf(mouseScroll = MouseScroll.newBuilder().setDeltaX(deltaX).setDeltaY(deltaY).build()))
    }

    /** Committed IME text - covers typing without needing per-key HID event mapping
     * from Android's own KeyEvent codes (see docs/protocol.md "Platform neutrality";
     * raw KeyEvent HID mapping is not implemented in this milestone). */
    fun sendTextInput(text: String) {
        send(inputDiscreteChannel, envelopeOf(textInput = TextInput.newBuilder().setText(text).build()))
    }

    /** Backspace is the one non-text key this milestone wires up as a real key event
     * (0x2A = USB HID "Keyboard DELETE (Backspace)") rather than committed text -
     * TextInput has no way to express "delete a character" (see Windows/ReyDesk.
     * Windows/Input/InputInjector.cs's HID->VK table). */
    fun sendBackspace() {
        send(inputDiscreteChannel, envelopeOf(keyEvent = KeyEvent.newBuilder().setHidUsageCode(0x2A).setIsDown(true).build()))
        send(inputDiscreteChannel, envelopeOf(keyEvent = KeyEvent.newBuilder().setHidUsageCode(0x2A).setIsDown(false).build()))
    }

    /** A real Enter keypress (0x28 = USB HID "Keyboard Return (ENTER)"), not committed
     * text - confirmed on real hardware that sending "\n" via TextInput does not
     * actually submit anything on the PC side (most Windows UI treats a Unicode
     * newline character as literal text, not as pressing Enter). */
    fun sendEnter() {
        send(inputDiscreteChannel, envelopeOf(keyEvent = KeyEvent.newBuilder().setHidUsageCode(0x28).setIsDown(true).build()))
        send(inputDiscreteChannel, envelopeOf(keyEvent = KeyEvent.newBuilder().setHidUsageCode(0x28).setIsDown(false).build()))
    }

    private fun send(channel: DataChannel?, envelope: Envelope) {
        if (closed || channel?.state() != DataChannel.State.OPEN) return
        val bytes = envelope.toByteArray()
        if (bytes.size <= 65536) channel.send(DataChannel.Buffer(ByteBuffer.wrap(bytes), true))
    }

    fun close() {
        synchronized(diagnosticsLock) {
            if (closed) return
            closed = true
            diagnosticsHandler.removeCallbacks(diagnosticsPoll)
            signaling.onSessionOffer = null
            signaling.onIceCandidate = null
            peerConnection.close()
        }
    }

    private fun envelopeOf(
        sessionAnswer: SessionAnswer? = null,
        iceCandidate: ProtoIceCandidate? = null,
        mouseMove: MouseMove? = null,
        mouseButton: MouseButtonEvent? = null,
        mouseScroll: MouseScroll? = null,
        textInput: TextInput? = null,
        keyEvent: KeyEvent? = null,
    ): Envelope =
        Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(sessionId)
            .setSentAtUnixMs(Instant.now().toEpochMilli())
            .also { builder ->
                sessionAnswer?.let { builder.setSessionAnswer(it) }
                iceCandidate?.let { builder.setIceCandidate(it) }
                mouseMove?.let { builder.setMouseMove(it) }
                mouseButton?.let { builder.setMouseButton(it) }
                mouseScroll?.let { builder.setMouseScroll(it) }
                textInput?.let { builder.setTextInput(it) }
                keyEvent?.let { builder.setKeyEvent(it) }
            }
            .build()
}

private abstract class SdpObserverAdapter : SdpObserver {
    override fun onCreateSuccess(description: SessionDescription) {}
    override fun onSetSuccess() {}
    override fun onCreateFailure(error: String) { Log.e(Tag, "SDP create failed: $error") }
    override fun onSetFailure(error: String) { Log.e(Tag, "SDP set failed: $error") }
}
