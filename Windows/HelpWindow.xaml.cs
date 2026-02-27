using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Malie.Windows;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            var targetUri = e.Uri?.AbsoluteUri ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetUri))
            {
                StatusTextBlock.Text = "Link could not be opened (missing URI).";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = targetUri,
                UseShellExecute = true
            });
            StatusTextBlock.Text = $"Opened: {targetUri}";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to open link: {ex.Message}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
