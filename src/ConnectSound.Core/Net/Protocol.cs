using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectSound.Core.Audio;

namespace ConnectSound.Core.Net;

public static class Ports
{
    public const int Discovery = 47800;
    public const int Control = 47801;
    public const int Audio = 47802;
}

public enum DeviceKind { Computer, Phone }

public static class MessageTypes
{
    /// <summary>Host → everyone (UDP broadcast): "I'm here, connect to me".</summary>
    public const string Announce = "announce";
    /// <summary>Source → host: who I am and what I'm for.</summary>
    public const string Hello = "hello";
    /// <summary>Host → source: accepted, here is where to send audio.</summary>
    public const string Welcome = "welcome";
    /// <summary>Source → host: call started/ended, profile changed.</summary>
    public const string State = "state";
    /// <summary>Host → source: mixer settings the host chose for this device.</summary>
    public const string Mix = "mix";
    public const string Ping = "ping";
    public const string Bye = "bye";
}

/// <summary>
/// Every control message uses this one shape; unused fields stay null.
/// A flat envelope keeps the wire format readable and trimming-friendly.
/// </summary>
public sealed record ControlMessage
{
    public const string AppTag = "ConnectSound";
    public const int ProtocolVersion = 1;

    public string Type { get; init; } = "";
    public string? App { get; init; }
    public int? Protocol { get; init; }
    public string? DeviceId { get; init; }
    public string? Name { get; init; }
    public DeviceKind? Kind { get; init; }
    public int? ControlPort { get; init; }
    public int? AudioPort { get; init; }
    public uint? Token { get; init; }
    public SourceProfile? Profile { get; init; }
    public bool? InCall { get; init; }
    public float? Gain { get; init; }
    public bool? Muted { get; init; }
    public string? Reason { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ControlMessage))]
internal sealed partial class ProtocolJson : JsonSerializerContext;

public static class ControlCodec
{
    private const int MaxMessageBytes = 64 * 1024;

    public static byte[] Serialize(ControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.ControlMessage);

    public static ControlMessage? Deserialize(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize(json, ProtocolJson.Default.ControlMessage); }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes a 4-byte little-endian length prefix followed by the JSON body.</summary>
    public static async Task WriteAsync(Stream stream, ControlMessage message, CancellationToken ct)
    {
        var body = Serialize(message);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when the peer closed the connection cleanly.</summary>
    public static async Task<ControlMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEof(stream, header, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException($"Control message of {length} bytes rejected.");

        var body = new byte[length];
        if (!await ReadExactlyOrEof(stream, body, ct).ConfigureAwait(false)) return null;
        return Deserialize(body) ?? throw new InvalidDataException("Control message is not valid JSON.");
    }

    private static async Task<bool> ReadExactlyOrEof(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// UDP audio datagram: "CS" | version (1) | type (1) | token (4) | sequence (4) | Opus payload.
/// </summary>
public static class AudioPacket
{
    public const int HeaderSize = 12;
    public const byte Version = 1;
    public const byte TypeOpus = 1;

    public static int Write(Span<byte> datagram, uint token, uint sequence, ReadOnlySpan<byte> opus)
    {
        datagram[0] = (byte)'C';
        datagram[1] = (byte)'S';
        datagram[2] = Version;
        datagram[3] = TypeOpus;
        BinaryPrimitives.WriteUInt32LittleEndian(datagram[4..], token);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram[8..], sequence);
        opus.CopyTo(datagram[HeaderSize..]);
        return HeaderSize + opus.Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> datagram, out uint token, out uint sequence, out ReadOnlySpan<byte> opus)
    {
        token = sequence = 0;
        opus = default;
        if (datagram.Length <= HeaderSize || datagram[0] != 'C' || datagram[1] != 'S' || datagram[2] != Version || datagram[3] != TypeOpus)
            return false;

        token = BinaryPrimitives.ReadUInt32LittleEndian(datagram[4..]);
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]);
        opus = datagram[HeaderSize..];
        return true;
    }
}
