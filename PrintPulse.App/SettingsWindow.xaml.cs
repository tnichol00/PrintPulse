using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PrintPulse.Core;

namespace PrintPulse;
public partial class SettingsWindow : Window
{
    public Action? OpenPanel { get; set; }
    private void OpenPanelClick(object sender, RoutedEventArgs e) => OpenPanel?.Invoke();
    private readonly MonitorViewModel model;
    private readonly BambuApi api = new();
    private readonly CancellationTokenSource lifetime = new();
    private string? challenge;
    private string? tfaKey;
    private bool busy;
    public SettingsWindow(MonitorViewModel model)
    {
        InitializeComponent(); this.model = model; DataContext = model;
        StartupToggle.IsChecked = Startup.Enabled;
        model.PropertyChanged += ModelChanged; model.Printers.CollectionChanged += PrintersChanged;
        Closed += (_, _) => { lifetime.Cancel(); api.Dispose(); model.PropertyChanged -= ModelChanged; model.Printers.CollectionChanged -= PrintersChanged; Secret.Clear(); };
        UpdateAccount(); UpdatePrinters();
    }
    private void ModelChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateAccount();
    private void PrintersChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdatePrinters();
    private void UpdateAccount()
    {
        LoginPanel.Visibility = model.Session == null || model.ConnectionText.Contains("Sign in again", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = model.Session != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdatePrinters()
    {
        VisibilityList.Children.Clear();
        foreach (var printer in model.Printers)
        {
            var toggle = new System.Windows.Controls.CheckBox { IsChecked = !model.Preferences.HiddenPrinters.Contains(printer.Id) };
            toggle.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(Printer.Name)) { Source = printer });
            toggle.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(Printer.Model)) { Source = printer });
            toggle.Click += (_, _) => { if (toggle.IsChecked == true) model.Preferences.HiddenPrinters.Remove(printer.Id); else model.Preferences.HiddenPrinters.Add(printer.Id); Save(); };
            VisibilityList.Children.Add(toggle);
        }
    }
    private async void LoginClick(object s, RoutedEventArgs e)
    {
        if (busy) return;
        var email = Email.Text.Trim(); var value = Secret.Password; Secret.Clear();
        if (!email.Contains('@') || string.IsNullOrWhiteSpace(value)) { Message.Text = "Enter your email and password or verification code."; return; }
        var epoch = model.Generation; SetBusy(true); Message.Text = "Signing in…";
        try
        {
            if (challenge == "tfa")
            {
                var session = await api.VerifyTfa(tfaKey ?? "", value, email, Region.SelectedIndex == 1, lifetime.Token);
                if (!lifetime.IsCancellationRequested && epoch == model.Generation) { model.Accept(session, epoch); ResetChallenge(); Message.Text = "Signed in. Connecting your printers…"; }
            }
            else
            {
                var result = await api.Login(email, value, challenge == "email", Region.SelectedIndex == 1, lifetime.Token);
                if (lifetime.IsCancellationRequested || epoch != model.Generation) return;
                if (result.Session != null) { model.Accept(result.Session, epoch); ResetChallenge(); Message.Text = "Signed in. Connecting your printers…"; }
                else
                {
                    challenge = result.Challenge; tfaKey = result.TfaKey;
                    SecretLabel.Text = challenge == "tfa" ? "Authenticator code" : "Email verification code";
                    if (challenge == "email") await api.SendCode(email, Region.SelectedIndex == 1, lifetime.Token);
                    Message.Text = challenge == "tfa" ? "Enter the current code from your authenticator." : "Check your email for a verification code.";
                }
            }
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or OperationCanceledException or System.IO.IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        { if (!lifetime.IsCancellationRequested) Message.Text = ex is CloudException ? ex.Message : "Could not complete sign-in or secure session storage. Check your connection and try again."; }
        finally { SetBusy(false); }
    }
    private async void CodeClick(object s, RoutedEventArgs e)
    {
        if (busy) return;
        if (!Email.Text.Contains('@')) { Message.Text = "Enter your Bambu account email first."; return; }
        SetBusy(true); Secret.Clear();
        try { await api.SendCode(Email.Text.Trim(), Region.SelectedIndex == 1, lifetime.Token); challenge = "email"; tfaKey = null; SecretLabel.Text = "Email verification code"; Message.Text = "Code requested. Check your inbox, then choose Sign in."; }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) Message.Text = ex is CloudException ? ex.Message : "Could not request a code. Try again when connected."; }
        finally { SetBusy(false); }
    }
    private void ResetChallenge() { challenge = tfaKey = null; SecretLabel.Text = "Password"; Secret.Clear(); }
    private void SetBusy(bool value) { busy = value; LoginButton.IsEnabled = CodeButton.IsEnabled = Email.IsEnabled = Region.IsEnabled = !value; }
    private void SignOutClick(object s, RoutedEventArgs e) { try { model.SignOut(); ResetChallenge(); Message.Text = "Local session removed."; } catch { Message.Text = "Could not remove the saved session. Check folder permissions and retry."; } }
    private void StartupClick(object s, RoutedEventArgs e) { try { Startup.Set(StartupToggle.IsChecked == true); } catch { StartupToggle.IsChecked = Startup.Enabled; Message.Text = "Windows did not allow the startup change."; } }
    private void PreferenceClick(object s, RoutedEventArgs e) => Save();
    private void Save() { try { model.SavePreferences(); } catch { Message.Text = "Settings could not be saved. Check folder permissions."; } }
}

