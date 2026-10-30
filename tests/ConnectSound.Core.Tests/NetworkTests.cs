using System.Net;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;
using ConnectSound.Core.Updates;

namespace ConnectSound.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task Control_messages_survive_framing()
    {
        var stream = new MemoryStream();
        var sent = new ControlMessage { Type = MessageTypes.State, Profile = SourceProfile.Calls, InCall = true };

        await ControlCodec.WriteAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;
        var received = await ControlCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(sent, received);
        Assert.Null(await ControlCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Wire_format_uses_readable_names()
    {
        var json = System.Text.Encoding.UTF8.GetString(ControlCodec.Serialize(new ControlMessage { Type = "state", Profile = SourceProfile.Calls }));
        Assert.Equal("""{"type":"state","profile":"Calls"}""", json);
    }

    [Fact]
    public void Audio_packet_round_trips_and_rejects_garbage()
    {
        var datagram = new byte[64];
        var size = AudioPacket.Write(datagram, token: 0xDEADBEEF, sequence: 42, opus: [1, 2, 3]);

        Assert.True(AudioPacket.TryRead(datagram.AsSpan(0, size), out var token, out var seq, out var opus));
        Assert.Equal(0xDEADBEEF, token);
        Assert.Equal(42u, seq);
        Assert.Equal([1, 2, 3], opus.ToArray());

        Assert.False(AudioPacket.TryRead("XX\u0001\u0001abcdefghij"u8, out _, out _, out _));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.4", "0.4.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void Release_tags_parse(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), GitHubReleases.ParseVersion(tag));
}

/// <summary>End-to-end over loopback: a fake capture streams a tone into a host and the host's mixer plays it.</summary>
public class LoopbackSessionTests
{
    private sealed class ToneCapture : IAudioCapture
    {
        private Timer? _timer;
        private int _n;
        public event SamplesHandler? SamplesCaptured;

        public void Start() => _timer = new Timer(_ =>
        {
            var block = new float[AudioFormat.FrameSamples];
            for (var i = 0; i < AudioFormat.FrameSamplesPerChannel; i++)
                block[2 * i] = block[2 * i + 1] = 0.4f * MathF.Sin(2 * MathF.PI * 330 * _n++ / 48_000f);
            SamplesCaptured?.Invoke(block);
        }, null, 0, 20);

        public void Stop() => _timer?.Dispose();
        public void Dispose() => Stop();
    }

    private sealed class NullOutput : IAudioOutput
    {
        public string? Problem => null;
        public void Start(ISampleProvider48k provider) { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Source_streams_into_host_and_reports_calls()
    {
        await using var host = new HostSession(new DeviceIdentity("host", "Laptop", DeviceKind.Computer), new NullOutput(), controlPort: 0, audioPort: 0);
        host.Start();

        await using var source = new SourceClient(new DeviceIdentity("phone", "Pixel", DeviceKind.Phone), new ToneCapture());
        await source.ConnectAsync(new IPEndPoint(IPAddress.Loopback, host.ControlPort), SourceProfile.Calls, inCall: false, CancellationToken.None);

        Assert.Equal("Laptop", source.HostName);
        var remote = await Eventually(() => host.Sources.SingleOrDefault());
        Assert.Equal("Pixel", remote.Identity.Name);

        await Eventually(() => remote.Channel.Buffer.Count >= 3 ? remote : null);
        var output = new float[AudioFormat.FrameSamples];
        host.Mixer.Read(output);
        Assert.True(output.Max() > 0.2f, "the tone should come out of the mixer");

        await source.SetInCallAsync(true);
        await Eventually(() => remote.InCall ? remote : null);

        var disconnected = new TaskCompletionSource<DisconnectReason>();
        source.Disconnected += r => disconnected.TrySetResult(r);
        host.Disconnect(remote);
        Assert.Equal(DisconnectReason.HostRemovedUs, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<T> Eventually<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }
}
