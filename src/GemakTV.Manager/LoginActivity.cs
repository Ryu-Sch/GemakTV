using Android.Content;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using AndroidX.AppCompat.App;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

namespace GemakTV.Manager;

/// <summary>
/// Sign in with the GemakTV GitHub account. Signing in to the app and to GitHub is the same thing: the
/// credentials are checked with GitHub and must allow changing the repository. They are remembered on the phone.
/// </summary>
[Activity(Name = "io.github.ryusch.gemaktv.manager.LoginActivity", Label = "GemakTV Manager", MainLauncher = true, Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class LoginActivity : AppCompatActivity
{
    private TextInputEditText _username = null!, _token = null!, _server = null!;
    private MaterialButton _signIn = null!;
    private View _progress = null!;
    private TextView _error = null!;
    private CredentialStore _store = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_login);
        _store = new CredentialStore(this);
        _username = FindViewById<TextInputEditText>(Resource.Id.username)!;
        _token = FindViewById<TextInputEditText>(Resource.Id.token)!;
        _server = FindViewById<TextInputEditText>(Resource.Id.server)!;
        _signIn = FindViewById<MaterialButton>(Resource.Id.sign_in)!;
        _progress = FindViewById(Resource.Id.progress)!;
        _error = FindViewById<TextView>(Resource.Id.error)!;

#if DEBUG
        // Test builds can talk to a local stand-in for GitHub (tools/GemakTV.FakeGitHub).
        FindViewById(Resource.Id.server_layout)!.Visibility = ViewStates.Visible;
#endif
        _signIn.Click += async (_, _) => await SignInAsync(ReadForm());
        _token.EditorAction += async (_, e) =>
        {
            if (e.ActionId != ImeAction.Done) { e.Handled = false; return; }
            await SignInAsync(ReadForm());
        };

        _username.Text = _store.LastUsername;
        if (ManagerSession.Current is not null) OpenChannels();
        else if (_store.Load() is { } saved) _ = SignInAsync(saved, remembered: true);
    }

    private Credentials ReadForm() => new(
        _username.Text?.Trim() ?? "",
        _token.Text?.Trim() ?? "",
        string.IsNullOrWhiteSpace(_server.Text) ? null : _server.Text.Trim());

    private async Task SignInAsync(Credentials credentials, bool remembered = false)
    {
        if (credentials.Username.Length == 0 || credentials.Token.Length == 0)
        {
            ShowError("Enter the username and the password (access token).");
            return;
        }
        HideKeyboard();
        SetBusy(true);
        try
        {
            await ManagerSession.SignInAsync(this, credentials);
            _store.Save(credentials);
            OpenChannels();
        }
        catch (SignInException ex)
        {
            if (remembered) _store.Clear();
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("GemakTV.Manager", ex.ToString());
            ShowError("Something went wrong while signing in: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenChannels()
    {
        StartActivity(new Intent(this, typeof(ChannelsActivity)));
        Finish();
    }

    private void SetBusy(bool busy)
    {
        _signIn.Enabled = !busy;
        _signIn.Text = busy ? GetString(Resource.String.login_signing_in) : GetString(Resource.String.login_button);
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Invisible;
        if (busy) _error.Visibility = ViewStates.Gone;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = ViewStates.Visible;
    }

    private void HideKeyboard()
    {
        var input = (InputMethodManager?)GetSystemService(InputMethodService);
        input?.HideSoftInputFromWindow(_token.WindowToken, HideSoftInputFlags.None);
    }
}
