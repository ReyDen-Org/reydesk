using System;

namespace ReyDesk.Windows.Media;

/// <summary>RFC6184 packetization with an explicit capture timestamp. SIPSorcery
/// 10.0.16's SendVideo only offers a post-send duration and cannot represent gaps
/// before the current frame; SendRtpRaw still handles SRTP, sequence numbers and RTCP.</summary>
internal static class H264RtpSender
{
    private const int MaximumPayload = 1200;

    public static void SendNal(byte[] nal, bool isLastNal, uint timestamp, int payloadType,
        Action<byte[], uint, int, int> send)
    {
        if (nal.Length <= MaximumPayload)
        {
            send(nal, timestamp, isLastNal ? 1 : 0, payloadType);
            return;
        }

        // FU-A excludes the original NAL header and prepends two RTP bytes.
        const int fragmentSize = MaximumPayload - 2;
        for (var offset = 1; offset < nal.Length; offset += fragmentSize)
        {
            var count = Math.Min(fragmentSize, nal.Length - offset);
            var last = offset + count == nal.Length;
            var payload = new byte[count + 2];
            payload[0] = (byte)((nal[0] & 0xe0) | 28); // F/NRI + FU-A type
            payload[1] = (byte)((nal[0] & 0x1f) | (offset == 1 ? 0x80 : 0) | (last ? 0x40 : 0));
            Buffer.BlockCopy(nal, offset, payload, 2, count);
            send(payload, timestamp, last && isLastNal ? 1 : 0, payloadType);
        }
    }
}
