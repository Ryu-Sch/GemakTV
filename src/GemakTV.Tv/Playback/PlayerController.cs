using Android.Content;
using Android.Net;
using Android.OS;
using Android.Util;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Hls;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.UI;
using GemakTV.Domain;
using GemakTV.Domain.Playback;

namespace GemakTV.Tv.Playback;

public enum PlaybackPhase
{
    Idle,
    Tuning,
    Playing,
    Unavailable,
    NotSetUp,
    NoInternet,
}

public sealed record PlaybackStatus(PlaybackPhase Phase, Channel? Channel = null, int Attempt = 0, int Total = 0, TimeSpan RetryIn = default);

/// <summary>
/// Plays one channel and keeps it playing: tries the channel's streams in turn (the last one that worked first),
/// treats a stream that will not start or stalls as failed, waits for the internet when it is down, and retries
/// for as long as the channel is on screen. Uses a small buffer to suit 1 GB boxes.
/// </summary>
public sealed partial class PlayerController : Java.Lang.Object, IPlayerListener
{
    private const string Tag = "GemakTV";
    private const int StateBuffering = 2;
    private const int StateReady = 3;
    private const int StateEnded = 4;
    private const int ErrorBehindLiveWindow = 1002;
    private const string DefaultUserAgent = "Mozilla/5.0 (Linux; Android 6.0.1; GemakTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    private readonly Context _context;
    private readonly TvPreferences _prefs;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly NetworkWatcher _network;
    private IExoPlayer? _player;
    private Channel? _channel;
    private StreamFailover? _failover;
    private int _generation;
    private bool _started;

    public PlayerController(Context context, PlayerView view, TvPreferences prefs)
    {
        _context = context;
        _prefs = prefs;
        _player = CreatePlayer(context);
        _player.AddListener(this);
        view.Player = _player;
        _network = new NetworkWatcher(context, () => _handler.Post(OnNetworkBack));
    }

    public event Action<PlaybackStatus>? StatusChanged;

    public PlaybackStatus Status { get; private set; } = new(PlaybackPhase.Idle);

    public void Play(Channel channel)
    {
        _channel = channel;
        _failover = new StreamFailover(channel.Streams, _prefs.GetPreferredStream(channel.Id));
        if (!channel.IsPlayable)
        {
            StopPlayer();
            Emit(new PlaybackStatus(PlaybackPhase.NotSetUp, channel));
            return;
        }
        StartAttempt();
    }

    public void Stop()
    {
        _channel = null;
        StopPlayer();
        Emit(new PlaybackStatus(PlaybackPhase.Idle));
    }

    // --- Player events -------------------------------------------------------------------------------

    public void OnIsPlayingChanged(bool isPlaying)
    {
        if (!isPlaying || _channel is null || _failover?.Current is not { } stream) return;
        if (!_started)
        {
            _started = true;
            Log.Info(Tag, $"Playing {_channel.Name} from {stream.Url}");
            _prefs.SetPreferredStream(_channel.Id, stream.Url);
            // A fresh round that starts with the stream that works now.
            _failover = new StreamFailover(_channel.Streams, stream.Url);
        }
        Emit(new PlaybackStatus(PlaybackPhase.Playing, _channel));
    }

    public void OnPlaybackStateChanged(int playbackState)
    {
        if (_channel is null) return;
        switch (playbackState)
        {
            case StateBuffering when _started:
                // A short rebuffer is normal; a long one means the stream died without an error.
                var generation = _generation;
                _handler.PostDelayed(() =>
                {
                    if (generation == _generation && _player?.PlaybackState == StateBuffering)
                        Fail("stalled");
                }, (long)StallTimeout.TotalMilliseconds);
                break;
            case StateEnded:
                Fail("stream ended"); // live channels do not end; reconnect
                break;
            case StateReady:
                break;
        }
    }

    public void OnPlayerError(PlaybackException? error)
    {
        if (error?.ErrorCode == ErrorBehindLiveWindow && _player is not null)
        {
            // Fell too far behind the live edge (e.g. after a long rebuffer): jump back to live.
            _player.SeekToDefaultPosition();
            _player.Prepare();
            return;
        }
        Fail(error?.ErrorCodeName ?? error?.Message ?? "error");
    }

    // --- Failover --------------------------------------------------------------------------------------

    private void StartAttempt()
    {
        if (_player is null || _channel is null || _failover?.Current is not { } stream) return;
        var generation = ++_generation;
        _started = false;
        Emit(new PlaybackStatus(PlaybackPhase.Tuning, _channel, _failover.Attempt, _failover.StreamCount));
        Log.Info(Tag, $"Tuning {_channel.Name}: attempt {_failover.Attempt}/{_failover.StreamCount} round {_failover.Round} {stream.Url}");

        _player.Stop();
        _player.SetMediaSource(CreateSource(stream));
        _player.Prepare();
        _player.PlayWhenReady = true;

        _handler.PostDelayed(() =>
        {
            if (generation == _generation && !_started) Fail("did not start in time");
        }, (long)StartTimeout.TotalMilliseconds);
    }

    private void Fail(string reason)
    {
        if (_channel is null || _failover is null) return;
        Log.Warn(Tag, $"{_channel.Name}: {_failover.Current?.Url} failed ({reason})");

        if (!_network.IsConnected)
        {
            ++_generation;
            StopPlayer();
            Emit(new PlaybackStatus(PlaybackPhase.NoInternet, _channel));
            return; // OnNetworkBack restarts
        }

        if (_failover.MoveNext())
        {
            StartAttempt();
            return;
        }

        // Every stream failed this round: wait a little, then go round again.
        _failover.StartRound();
        var delay = StreamFailover.RetryDelay(_failover.Round);
        var generation = ++_generation;
        StopPlayer();
        Emit(new PlaybackStatus(PlaybackPhase.Unavailable, _channel, RetryIn: delay));
        _handler.PostDelayed(() =>
        {
            if (generation == _generation) StartAttempt();
        }, (long)delay.TotalMilliseconds);
    }

    private void OnNetworkBack()
    {
        if (_channel is null || _failover is null) return;
        if (Status.Phase is PlaybackPhase.NoInternet or PlaybackPhase.Unavailable)
        {
            Log.Info(Tag, "Network is back; retrying");
            _failover = new StreamFailover(_channel.Streams, _prefs.GetPreferredStream(_channel.Id));
            if (_channel.IsPlayable) StartAttempt();
        }
    }

    private IMediaSource CreateSource(StreamSource stream)
    {
        var http = new DefaultHttpDataSource.Factory()
            .SetUserAgent(stream.UserAgent ?? DefaultUserAgent)!
            .SetConnectTimeoutMs(10_000)!
            .SetReadTimeoutMs(15_000)!
            .SetAllowCrossProtocolRedirects(true)!;
        if (stream.Referrer is { } referrer)
        {
            var headers = new Dictionary<string, string> { ["Referer"] = referrer };
            if (System.Uri.TryCreate(referrer, UriKind.Absolute, out var origin))
                headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
            http.SetDefaultRequestProperties(headers);
        }

        var isHls = stream.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || stream.Url.Contains("m3u8", StringComparison.OrdinalIgnoreCase);
        var item = new MediaItem.Builder().SetUri(stream.Url)!;
        if (isHls) item.SetMimeType(MimeTypes.ApplicationM3u8);
        var mediaItem = item.Build()!;

        return isHls
            ? new HlsMediaSource.Factory(http).CreateMediaSource(mediaItem)!
            : new DefaultMediaSourceFactory(http).CreateMediaSource(mediaItem)!;
    }

    private static IExoPlayer CreatePlayer(Context context)
    {
        // Small buffers: 15-30 s and at most ~24 MB of media in memory.
        var loadControl = new DefaultLoadControl.Builder()
            .SetBufferDurationsMs(15_000, 30_000, 2_500, 5_000)!
            .SetTargetBufferBytes(24 * 1024 * 1024)!
            .SetPrioritizeTimeOverSizeThresholds(false)!
            .Build()!;
        return new ExoPlayerBuilder(context).SetLoadControl(loadControl)!.Build()!;
    }

    private void StopPlayer()
    {
        _started = false;
        try { _player?.Stop(); } catch (Java.Lang.Exception) { }
    }

    private void Emit(PlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ++_generation;
            _handler.RemoveCallbacksAndMessages(null);
            _network.Dispose();
            if (_player is not null)
            {
                _player.RemoveListener(this);
                _player.Release();
                _player = null;
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>Tells the controller when the internet comes back.</summary>
    private sealed class NetworkWatcher : ConnectivityManager.NetworkCallback
    {
        private readonly ConnectivityManager _connectivity;
        private readonly Action _onAvailable;

        public NetworkWatcher(Context context, Action onAvailable)
        {
            _connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
            _onAvailable = onAvailable;
            var request = new NetworkRequest.Builder().AddCapability(NetCapability.Internet)!.Build()!;
            _connectivity.RegisterNetworkCallback(request, this);
        }

#pragma warning disable CA1422 // ActiveNetworkInfo is the Android 6 API
        public bool IsConnected => _connectivity.ActiveNetworkInfo?.IsConnected == true;
#pragma warning restore CA1422

        public override void OnAvailable(Network network) => _onAvailable();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _connectivity.UnregisterNetworkCallback(this); } catch (Java.Lang.Exception) { }
            }
            base.Dispose(disposing);
        }
    }
}
