using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using GemakTV.Domain;
using GemakTV.Infrastructure.Iptv;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

namespace GemakTV.Manager;

/// <summary>One channel: its streams in the order the TV tries them, their health, and adding your own stream address.</summary>
[Activity(Name = "io.github.ryusch.gemaktv.manager.ChannelActivity", Label = "Channel", WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
public sealed class ChannelActivity : AppCompatActivity
{
    public const string ChannelIdExtra = "channel";

    private ManagerSession _session = null!;
    private string _id = "";
    private LinearLayout _streams = null!;
    private TextInputEditText _newUrl = null!;
    private TextInputLayout _newUrlLayout = null!;
    private MaterialButton _check = null!;
    private readonly HashSet<string> _checking = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (ManagerSession.Current is not { } session || Intent?.GetStringExtra(ChannelIdExtra) is not { } id) { Finish(); return; }
        _session = session;
        _id = id;
        SetContentView(Resource.Layout.activity_channel);

        FindViewById<MaterialToolbar>(Resource.Id.toolbar)!.NavigationClick += (_, _) => Finish();
        _streams = FindViewById<LinearLayout>(Resource.Id.streams)!;
        _newUrl = FindViewById<TextInputEditText>(Resource.Id.new_url)!;
        _newUrlLayout = FindViewById<TextInputLayout>(Resource.Id.new_url_layout)!;
        _check = FindViewById<MaterialButton>(Resource.Id.check_streams)!;

        FindViewById(Resource.Id.add_stream)!.Click += (_, _) => AddStream();
        _check.Click += async (_, _) => await CheckAllAsync();
        FindViewById(Resource.Id.remove_channel)!.Click += (_, _) =>
        {
            _session.Editor.Remove(_id);
            Finish();
        };
        _session.Editor.Changed += OnChanged;
        Render();
        if (Channel is { IsPlayable: true } c && c.Streams.Any(s => !_session.Health.ContainsKey(s.Url)))
            _ = CheckAllAsync();
    }

    protected override void OnDestroy()
    {
        if (_session is not null) _session.Editor.Changed -= OnChanged;
        base.OnDestroy();
    }

    private Channel? Channel => _session.Editor.Find(_id);

    private void OnChanged(object? sender, EventArgs e) => RunOnUiThread(Render);

    private void Render()
    {
        if (Channel is not { } channel) { Finish(); return; }
        var toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        toolbar.Title = channel.Name;
        FindViewById<TextView>(Resource.Id.name)!.Text = channel.Name;
        FindViewById<TextView>(Resource.Id.channel_id)!.Text = channel.Id;
        _session.Logos.Bind(FindViewById<ImageView>(Resource.Id.logo)!, FindViewById<TextView>(Resource.Id.letter)!, channel);

        _streams.RemoveAllViews();
        if (channel.Streams.Count == 0)
        {
            var none = new TextView(this) { Text = GetString(Resource.String.no_stream) };
            none.SetTextColor(GetColorStateList(Resource.Color.warn));
            none.SetPadding(4, 8, 4, 16);
            _streams.AddView(none);
        }
        for (var i = 0; i < channel.Streams.Count; i++)
        {
            var stream = channel.Streams[i];
            var row = LayoutInflater.Inflate(Resource.Layout.item_stream_row, _streams, false)!;
            row.FindViewById<TextView>(Resource.Id.position)!.Text = (i + 1).ToString();
            row.FindViewById<TextView>(Resource.Id.url)!.Text = stream.Url;
            var health = row.FindViewById<TextView>(Resource.Id.health)!;
            if (_checking.Contains(stream.Url))
            {
                health.Text = GetString(Resource.String.checking);
                health.SetTextColor(GetColorStateList(Resource.Color.muted));
            }
            else if (_session.Health.TryGetValue(stream.Url, out var h))
            {
                health.Text = (h == StreamHealth.Working ? "✓ " : "✗ ") + StreamProbe.Describe(h);
                health.SetTextColor(GetColorStateList(h == StreamHealth.Working ? Resource.Color.good : Resource.Color.bad));
            }
            else
            {
                health.Text = i == 0 ? "Tried first" : "Backup";
            }
            var up = row.FindViewById(Resource.Id.up)!;
            up.Enabled = i > 0;
            up.Alpha = i > 0 ? 1f : 0.3f;
            up.Click += (_, _) => _session.Editor.MoveStream(_id, stream.Url, -1);
            row.FindViewById(Resource.Id.remove)!.Click += (_, _) => _session.Editor.RemoveStream(_id, stream.Url);
            _streams.AddView(row);
        }
        _check.Enabled = channel.IsPlayable && _checking.Count == 0;
    }

    private void AddStream()
    {
        var url = _newUrl.Text?.Trim() ?? "";
        if (!StreamSource.IsValidUrl(url))
        {
            _newUrlLayout.Error = GetString(Resource.String.invalid_url);
            return;
        }
        _newUrlLayout.Error = null;
        // A stream the family adds by hand is usually the one they trust most: try it first.
        if (_session.Editor.AddStream(_id, new StreamSource(url), preferFirst: true))
        {
            _newUrl.Text = "";
            _ = CheckAsync([new StreamSource(url)]);
        }
    }

    private Task CheckAllAsync() => Channel is { } c ? CheckAsync(c.Streams) : Task.CompletedTask;

    private async Task CheckAsync(IReadOnlyList<StreamSource> streams)
    {
        foreach (var s in streams) _checking.Add(s.Url);
        Render();
        await Task.WhenAll(streams.Select(async s =>
        {
            var health = await _session.Probe.CheckAsync(s);
            RunOnUiThread(() =>
            {
                _session.Health[s.Url] = health;
                _checking.Remove(s.Url);
                Render();
            });
        }));
    }
}
