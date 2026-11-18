using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using GemakTV.Application.Manager;
using GemakTV.Domain;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;

namespace GemakTV.Manager;

/// <summary>The channels as they are (or will be) on the TV: reorder, remove, open, add, then save.</summary>
[Activity(Name = "io.github.ryusch.gemaktv.manager.ChannelsActivity", Label = "Channels")]
public sealed class ChannelsActivity : AppCompatActivity
{
    private ManagerSession _session = null!;
    private MaterialToolbar _toolbar = null!;
    private RecyclerView _list = null!;
    private View _progress = null!, _empty = null!;
    private TextView _saveState = null!;
    private MaterialButton _save = null!;
    private ChannelRowAdapter _adapter = null!;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (ManagerSession.Current is not { } session)
        {
            StartActivity(new Intent(this, typeof(LoginActivity)));
            Finish();
            return;
        }
        _session = session;
        SetContentView(Resource.Layout.activity_channels);

        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.InflateMenu(Resource.Menu.channels);
        _toolbar.MenuItemClick += async (_, e) =>
        {
            if (e.Item!.ItemId == Resource.Id.action_refresh) await ReloadAsync();
            else if (e.Item.ItemId == Resource.Id.action_sign_out) SignOut();
        };
        _list = FindViewById<RecyclerView>(Resource.Id.list)!;
        _progress = FindViewById(Resource.Id.progress)!;
        _empty = FindViewById(Resource.Id.empty)!;
        _saveState = FindViewById<TextView>(Resource.Id.save_state)!;
        _save = FindViewById<MaterialButton>(Resource.Id.save)!;

        _adapter = new ChannelRowAdapter(_session, OpenChannel);
        _list.SetLayoutManager(new LinearLayoutManager(this));
        _list.SetAdapter(_adapter);

        FindViewById(Resource.Id.add_channels)!.Click += (_, _) => StartActivity(new Intent(this, typeof(SearchActivity)));
        _save.Click += async (_, _) => await SaveAsync();
        _session.Editor.Changed += OnEditorChanged;
        Refresh();

#pragma warning disable CA1422 // OnBackPressed is the Android 6 API
    }

    public override void OnBackPressed()
    {
        if (_session?.Editor.IsDirty != true)
        {
            base.OnBackPressed();
            return;
        }
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.discard_title)!
            .SetMessage(Resource.String.discard_message)!
            .SetPositiveButton(Resource.String.keep_editing, (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton(Resource.String.discard, (_, _) => Finish())!
            .Show();
    }
#pragma warning restore CA1422

    protected override void OnResume()
    {
        base.OnResume();
        if (_session is not null) Refresh();
    }

    protected override void OnDestroy()
    {
        if (_session is not null) _session.Editor.Changed -= OnEditorChanged;
        base.OnDestroy();
    }

    private void OnEditorChanged(object? sender, EventArgs e) => RunOnUiThread(Refresh);

    private void Refresh()
    {
        _toolbar.Subtitle = _session.DescribePublished();
        _adapter.NotifyDataSetChanged();
        _empty.Visibility = _session.Editor.Channels.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        var dirty = _session.Editor.IsDirty;
        _save.Enabled = dirty && !_busy;
        _saveState.Text = dirty ? DescribeChanges() : GetString(Resource.String.all_saved);
        _saveState.SetTextColor(GetColorStateList(dirty ? Resource.Color.warn : Resource.Color.muted));
    }

    private string DescribeChanges()
    {
        var diff = LineupDiff.Between(_session.Editor.Original, _session.Editor.Current);
        return GetString(Resource.String.unsaved) + ": " + diff.Describe();
    }

    private void OpenChannel(Channel channel)
    {
        var intent = new Intent(this, typeof(ChannelActivity));
        intent.PutExtra(ChannelActivity.ChannelIdExtra, channel.Id);
        StartActivity(intent);
    }

    private async Task SaveAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await _session.SaveAsync();
            switch (result.Outcome)
            {
                case PublishOutcome.Published:
                    Snackbar.Make(_list, Resource.String.saved_hint, Snackbar.LengthLong).Show();
                    break;
                case PublishOutcome.NoChanges:
                    Snackbar.Make(_list, Resource.String.nothing_to_save, Snackbar.LengthShort).Show();
                    break;
                case PublishOutcome.Conflict:
                    new MaterialAlertDialogBuilder(this)
                        .SetTitle(Resource.String.conflict_title)!
                        .SetMessage(Resource.String.conflict_message)!
                        .SetPositiveButton(Resource.String.reload, async (_, _) => await ReloadAsync())!
                        .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
                        .Show();
                    break;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("GemakTV.Manager", ex.ToString());
            Snackbar.Make(_list, "Could not save: " + ex.Message, Snackbar.LengthIndefinite).SetAction("OK", _ => { }).Show();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadAsync()
    {
        SetBusy(true);
        try
        {
            await _session.ReloadAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Make(_list, "Could not load the channel list: " + ex.Message, Snackbar.LengthLong).Show();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SignOut()
    {
        ManagerSession.SignOut(this);
        StartActivity(new Intent(this, typeof(LoginActivity)));
        Finish();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Invisible;
        Refresh();
    }

    private sealed class ChannelRowAdapter(ManagerSession session, Action<Channel> open) : RecyclerView.Adapter
    {
        public override int ItemCount => session.Editor.Channels.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_channel_row, parent, false)!;
            var holder = new Holder(view);
            view.Click += (_, _) => { if (holder.Channel is { } c) open(c); };
            holder.Up.Click += (_, _) => { if (holder.Channel is { } c) session.Editor.Move(c.Id, -1); };
            holder.Down.Click += (_, _) => { if (holder.Channel is { } c) session.Editor.Move(c.Id, +1); };
            holder.Remove.Click += (_, _) =>
            {
                if (holder.Channel is not { } c) return;
                session.Editor.Remove(c.Id);
                Snackbar.Make(parent, $"Removed {c.Name}", Snackbar.LengthLong)
                    .SetAction("Undo", _ => session.Editor.Add(c))
                    .Show();
            };
            return holder;
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var h = (Holder)holder;
            var channel = session.Editor.Channels[position];
            h.Channel = channel;
            h.Number.Text = (position + 1).ToString();
            h.Name.Text = channel.Name;
            h.Detail.Text = channel.IsPlayable
                ? channel.Streams.Count == 1 ? "1 stream" : $"{channel.Streams.Count} streams (backups ready)"
                : holder.ItemView.Context!.GetString(Resource.String.no_stream);
            h.Detail.SetTextColor(holder.ItemView.Context!.GetColorStateList(channel.IsPlayable ? Resource.Color.muted : Resource.Color.warn));
            h.Up.Enabled = position > 0;
            h.Up.Alpha = position > 0 ? 1f : 0.3f;
            h.Down.Enabled = position < ItemCount - 1;
            h.Down.Alpha = position < ItemCount - 1 ? 1f : 0.3f;
            session.Logos.Bind(h.Logo, h.Letter, channel);
        }

        private sealed class Holder(View view) : RecyclerView.ViewHolder(view)
        {
            public Channel? Channel { get; set; }
            public TextView Number { get; } = view.FindViewById<TextView>(Resource.Id.number)!;
            public TextView Name { get; } = view.FindViewById<TextView>(Resource.Id.name)!;
            public TextView Detail { get; } = view.FindViewById<TextView>(Resource.Id.detail)!;
            public ImageView Logo { get; } = view.FindViewById<ImageView>(Resource.Id.logo)!;
            public TextView Letter { get; } = view.FindViewById<TextView>(Resource.Id.letter)!;
            public View Up { get; } = view.FindViewById(Resource.Id.up)!;
            public View Down { get; } = view.FindViewById(Resource.Id.down)!;
            public View Remove { get; } = view.FindViewById(Resource.Id.remove)!;
        }
    }
}
