using System.Windows;

namespace Phosphor;

public partial class DarkConfirmDialog : JukeboxWindow
{
    public bool Confirmed { get; private set; }

    public DarkConfirmDialog(string title, string message, Window? owner = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        if (owner != null)
            Owner = owner;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    /// <summary>
    /// Convenience method matching the common confirmation pattern.
    /// Returns true if the user clicked OK.
    /// </summary>
    public static bool Confirm(string title, string message, Window? owner = null)
    {
        var dlg = new DarkConfirmDialog(title, message, owner);
        dlg.ShowDialog();
        return dlg.Confirmed;
    }
}
