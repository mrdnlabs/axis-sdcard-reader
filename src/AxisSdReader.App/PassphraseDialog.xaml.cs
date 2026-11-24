using System.Runtime.InteropServices;
using System.Windows;

namespace AxisSdReader.App;

/// <summary>Modal prompt for a LUKS card's passphrase. The passphrase is returned to the caller as a mutable
/// char[] (so the caller can zero it after use) and is never stored by this dialog. It is read out of the
/// PasswordBox's SecureString directly — never through <c>PasswordBox.Password</c>, which would leave an
/// immutable string copy on the heap — and the box is cleared afterwards.</summary>
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
        Closed += (_, _) => PasswordInput.Clear();
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
        using var secure = PasswordInput.SecurePassword; // a copy; disposed (wiped) here
        var buffer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            // Pinned: the garbage collector never moves (and so never copies) it before the caller zeroes it.
            var chars = GC.AllocateUninitializedArray<char>(secure.Length, pinned: true);
            Marshal.Copy(buffer, chars, 0, chars.Length);
            Passphrase = chars;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(buffer);
        }

        PasswordInput.Clear();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        DialogResult = false;
    }
}
