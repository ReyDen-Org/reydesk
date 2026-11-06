using Google.Protobuf;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Signaling;

/// <summary>Bound allocation before protobuf parsing; no peer-controlled unbounded buffers.</summary>
internal static class EnvelopeFraming
{
    public const int MaximumBytes = 262144;

    public static Envelope? Read(Stream stream)
    {
        uint length = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                if (shift == 0) return null;
                throw new EndOfStreamException("Truncated frame length.");
            }
            if (shift == 28 && (value & 0xf0) != 0) throw new InvalidDataException("Invalid frame length.");
            length |= (uint)(value & 127) << shift;
            if ((value & 128) == 0)
            {
                if (length == 0 || length > MaximumBytes) throw new InvalidDataException("Signaling frame size rejected.");
                var bytes = new byte[(int)length];
                stream.ReadExactly(bytes);
                var envelope = Envelope.Parser.ParseFrom(bytes);
                if (envelope.ProtocolVersion != 1) throw new InvalidDataException("Unsupported ReyDesk protocol version.");
                return envelope;
            }
        }
        throw new InvalidDataException("Invalid frame length.");
    }

    public static void Write(Stream stream, Envelope envelope)
    {
        if (envelope.CalculateSize() > MaximumBytes) throw new InvalidDataException("Signaling frame too large.");
        envelope.WriteDelimitedTo(stream);
    }
}
