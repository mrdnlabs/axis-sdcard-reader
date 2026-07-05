using System.Windows;

namespace AxisSdReader.App;

/// <summary>Modal prompt for a LUKS card's passphrase. The passphrase is returned to the caller and
/// never stored.</summary>
public partial class PassphraseDialog : Window
{
    public PassphraseDialog(string cardName, string? error = null)
    {
        InitializeComponent();
        SubtitleText.Text = cardName;
        if (!string.IsNullOrEmpty(error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>The entered passphrase (valid only when the dialog returned true).</summary>
    public string Passphrase { get; private set; } = "";

    /// <summary>Shows the dialog and returns the passphrase, or null if the user cancelled.</summary>
    public static string? Prompt(Window? owner, string cardName, string? error = null)
    {
        var dialog = new PassphraseDialog(cardName, error) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Passphrase : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Passphrase = PasswordInput.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
