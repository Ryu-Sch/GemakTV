using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using GemakTV.Application;
using GemakTV.Application.Manager;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;

namespace GemakTV.Manager;

/// <summary>Search every channel in the iptv-org database and add or remove it with one tap.</summary>
[Activity(Name = "io.github.ryusch.gemaktv.manager.SearchActivity", Label = "Add channels", WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateVisible)]
public sealed class SearchActivity : AppCompatActivity
{
    private ManagerSession _session = null!;
    private CatalogSearch? _search;
    private TextInputEditText _query = null!;
    private MaterialSwitch _onlyStreams = null!;
    private TextView _summary = null!;
    private View _progress = null!;
    private ResultAdapter _adapter = null!;
    private CancellationTokenSource? _typing;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (ManagerSession.Current is not { } session) { Finish(); return; }
        _session = session;
        SetContentView(Resource.Layout.activity_search);

        FindViewById<MaterialToolbar>(Resource.Id.toolbar)!.NavigationClick += (_, _) => Finish();
        _query = FindViewById<TextInputEditText>(Resource.Id.query)!;
        _onlyStreams = FindViewById<MaterialSwitch>(Resource.Id.only_streams)!;
        _summary = FindViewById<TextView>(Resource.Id.summary)!;
        _progress = FindViewById(Resource.Id.progress)!;
        var results = FindViewById<RecyclerView>(Resource.Id.results)!;
        _adapter = new ResultAdapter(_session, results);
        results.SetLayoutManager(new LinearLayoutManager(this));
        results.SetAdapter(_adapter);

        _query.TextChanged += (_, _) => ScheduleSearch();
        _onlyStreams.CheckedChange += (_, _) => ScheduleSearch();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _search = await _session.CatalogAsync();
            _progress.Visibility = ViewStates.Gone;
            Search();
        }
        catch (Exception ex)
        {
            _progress.Visibility = ViewStates.Gone;
            _summary.Text = "Could not load the channel list from iptv-org: " + ex.Message;
            Snackbar.Make(_summary, "No channel list", Snackbar.LengthIndefinite).SetAction("Retry", v =>
            {
                _progress.Visibility = ViewStates.Visible;
                _ = LoadAsync();
            }).Show();
        }
    }

    private void ScheduleSearch()
    {
        _typing?.Cancel();
        _typing = new CancellationTokenSource();
        var token = _typing.Token;
        _query.PostDelayed(() => { if (!token.IsCancellationRequested) Search(); }, 200);
    }

    private void Search()
    {
        if (_search is null) return;
        var query = _query.Text ?? "";
        var found = _search.Search(query, _onlyStreams.Checked, limit: 150);
        _adapter.Show(found);
        _summary.Text = query.Length == 0
            ? $"{_search.Count:N0} channels. Type a name to search."
            : found.Count == 0 ? "No channels found." : $"{found.Count} found";
    }

    private sealed class ResultAdapter(ManagerSession session, View anchor) : RecyclerView.Adapter
    {
        private IReadOnlyList<CatalogChannel> _items = [];

        public void Show(IReadOnlyList<CatalogChannel> items)
        {
            _items = items;
            NotifyDataSetChanged();
        }

        public override int ItemCount => _items.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.item_search_result, parent, false)!;
            var holder = new Holder(view);
            holder.Toggle.Click += (_, _) =>
            {
                if (holder.Channel is not { } c) return;
                if (session.Editor.Contains(c.Id))
                {
                    session.Editor.Remove(c.Id);
                }
                else
                {
                    session.Editor.Add(c.ToChannel());
                    Snackbar.Make(anchor, c.HasStreams ? $"{c.Name} added" : $"{c.Name} added. Open it to paste a stream address.", Snackbar.LengthShort).Show();
                }
                NotifyItemChanged(holder.BindingAdapterPosition);
            };
            return holder;
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var h = (Holder)holder;
            var c = _items[position];
            h.Channel = c;
            h.Name.Text = c.Name;
            var streams = c.HasStreams ? (c.Streams.Count == 1 ? "1 free stream" : $"{c.Streams.Count} free streams") : "no free stream";
            var parts = new[] { c.Country, c.Categories.FirstOrDefault(), streams, c.IsClosed ? "closed" : null }.Where(p => !string.IsNullOrEmpty(p));
            h.Detail.Text = string.Join(" · ", parts);
            var added = session.Editor.Contains(c.Id);
            h.Toggle.Text = holder.ItemView.Context!.GetString(added ? Resource.String.added : Resource.String.add);
            h.Toggle.SetIconResource(added ? Resource.Drawable.ic_ok : Resource.Drawable.ic_add);
            session.Logos.Bind(h.Logo, h.Letter, c.Name, c.LogoUrl);
        }

        private sealed class Holder(View view) : RecyclerView.ViewHolder(view)
        {
            public CatalogChannel? Channel { get; set; }
            public TextView Name { get; } = view.FindViewById<TextView>(Resource.Id.name)!;
            public TextView Detail { get; } = view.FindViewById<TextView>(Resource.Id.detail)!;
            public ImageView Logo { get; } = view.FindViewById<ImageView>(Resource.Id.logo)!;
            public TextView Letter { get; } = view.FindViewById<TextView>(Resource.Id.letter)!;
            public MaterialButton Toggle { get; } = view.FindViewById<MaterialButton>(Resource.Id.toggle)!;
        }
    }
}
