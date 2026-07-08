using System.Windows;

namespace AxisSdReader.App;

/// <summary>Modal prompt for a LUKS card's passphrase. The passphrase is returned to the caller as a
/// mutable char[] (so the caller can zero it after use) and is never stored by this dialog. Note WPF's
/// PasswordBox keeps its own internal string that cannot be wiped, so erasure is best-effort.</summary>
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
    public char[] Passphrase { get; private set; } = [];

    /// <summary>Shows the dialog and returns the passphrase, or null if the user cancelled. The caller owns
    /// the returned array and should zero it (Array.Clear) once the card has been unlocked.</summary>
    public static char[]? Prompt(Window? owner, string cardName, string? error = null)
    {
        var dialog = new PassphraseDialog(cardName, error) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Passphrase : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Passphrase = PasswordInput.Password.ToCharArray();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
