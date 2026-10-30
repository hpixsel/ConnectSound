using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;
using ConnectSound.Core.Platform;
using ConnectSound.UI.ViewModels;

namespace ConnectSound.Desktop;

/// <summary>
/// Developer options: <c>--demo</c> connects simulated phones to this host (silently),
/// <c>--screenshot file.png</c> renders the window after a moment and exits. Used for README images.
/// </summary>
internal sealed record DemoOptions(bool Enabled, string? DataDirectory, AppRole? Role, string? Screenshot, PixelSize? Size, bool Call)
{
    public static DemoOptions Parse(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        var size = Value("--size")?.Split('x') is [var w, var h] ? new PixelSize(int.Parse(w), int.Parse(h)) : (PixelSize?)null;
        var role = Value("--role") switch
        {
            "host" => AppRole.Host,
            "source" => AppRole.Source,
            "chooser" => AppRole.Undecided,
            _ => (AppRole?)null,
        };
        return new DemoOptions(args.Contains("--demo"), Value("--data"), role, Value("--screenshot"), size, args.Contains("--call"));
    }

    public void PrepareSettings(string dataDirectory)
    {
        if (Role is not { } role) return;
        var settings = AppSettings.Load(dataDirectory);
        settings.Role = role;
        settings.DeviceName ??= "Laptop w salonie";
        settings.SourceGains = new() { ["demo-tablet"] = 0.62f, ["demo-work"] = 1.18f };
        settings.Save();
    }

    public void Attach(TopLevel top, MainViewModel vm)
    {
        if (Size is { } size && top is Window window)
        {
            window.Width = size.Width;
            window.Height = size.Height;
        }

        if (Enabled && vm.Page is HostViewModel demoHost) demoHost.Addresses = "192.168.1.20";

        if (Enabled && vm.Page is HostViewModel)
        {
            _ = Connect("demo-music", "Pixel 8", SourceProfile.Media, Synth.Music, inCall: false);
            _ = Connect("demo-work", "Telefon służbowy", SourceProfile.Calls, Synth.Speech, inCall: Call);
            _ = Connect("demo-tablet", "Tablet", SourceProfile.Media, Synth.Music, inCall: false, mute: !Call);
        }

        if (Enabled && vm.Page is SourceViewModel)
        {
            var announcer = new HostAnnouncer(new DeviceIdentity("demo-host", "Laptop w salonie", DeviceKind.Computer), Ports.Control);
            announcer.Start();
            var other = new HostAnnouncer(new DeviceIdentity("demo-host-2", "Pixel 8 Pro", DeviceKind.Phone), Ports.Control);
            other.Start();
        }

        if (Screenshot is { } path)
        {
            DispatcherTimer.RunOnce(() =>
            {
                var scale = top.RenderScaling;
                var pixels = new PixelSize((int)(top.Bounds.Width * scale), (int)(top.Bounds.Height * scale));
                using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                bitmap.Render(top);
                bitmap.Save(path);
                ConnectSound.UI.DesktopShell.Exit();
            }, TimeSpan.FromSeconds(4));
        }

        async Task Connect(string id, string name, SourceProfile profile, Func<int, float, float> synth, bool inCall, bool mute = false)
        {
            await Task.Delay(500);
            var client = new SourceClient(new DeviceIdentity(id, name, DeviceKind.Phone), new SynthCapture(synth));
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Ports.Control), profile, inCall, CancellationToken.None);
            if (mute)
                Dispatcher.UIThread.Post(() =>
                {
                    if (vm.Page is HostViewModel host && host.Channels.FirstOrDefault(c => c.Source.Identity.Id == id) is { } channel)
                        channel.Muted = true;
                }, DispatcherPriority.Background);
        }
    }

    /// <summary>Output that keeps the mixer running without making a sound.</summary>
    public sealed class SilentOutput : IAudioOutput
    {
        public string? Problem => null;
        private Timer? _timer;
        private readonly float[] _buffer = new float[AudioFormat.FrameSamples];

        public void Start(ISampleProvider48k provider) =>
            _timer = new Timer(_ => { lock (_buffer) provider.Read(_buffer); }, null, 0, AudioFormat.FrameDurationMs);

        public void Stop() => _timer?.Dispose();
        public void Dispose() => Stop();
    }

    private sealed class SynthCapture(Func<int, float, float> synth) : IAudioCapture
    {
        private Timer? _timer;
        private int _n;
        private readonly float _seed = Random.Shared.NextSingle() * 10;
        public event SamplesHandler? SamplesCaptured;

        public void Start() => _timer = new Timer(_ =>
        {
            var block = new float[AudioFormat.FrameSamples];
            for (var i = 0; i < AudioFormat.FrameSamplesPerChannel; i++)
                block[2 * i] = block[2 * i + 1] = synth(_n++, _seed);
            SamplesCaptured?.Invoke(block);
        }, null, 0, AudioFormat.FrameDurationMs);

        public void Stop() => _timer?.Dispose();
        public void Dispose() => Stop();
    }

    private static class Synth
    {
        /// <summary>A pulsing chord so the meters move like they would with music.</summary>
        public static float Music(int n, float seed)
        {
            var t = n / 48_000f + seed;
            var beat = 0.55f + 0.45f * MathF.Pow(MathF.Max(0, MathF.Cos(MathF.PI * 2 * t * 2.1f)), 6);
            return beat * 0.32f * (MathF.Sin(2 * MathF.PI * 220 * t) + 0.6f * MathF.Sin(2 * MathF.PI * 277 * t) + 0.4f * MathF.Sin(2 * MathF.PI * 330 * t)) / 2;
        }

        /// <summary>Syllable-like bursts.</summary>
        public static float Speech(int n, float seed)
        {
            var t = n / 48_000f + seed;
            var syllable = MathF.Max(0, MathF.Sin(2 * MathF.PI * t * 3.3f)) * (0.6f + 0.4f * MathF.Sin(2 * MathF.PI * t * 0.7f));
            return 0.45f * syllable * MathF.Sin(2 * MathF.PI * 180 * t + 3 * MathF.Sin(2 * MathF.PI * 5 * t));
        }
    }
}
