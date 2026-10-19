using System.Windows;

namespace TeachMeOnce.WpfFixture;

public partial class DelayedDialogWindow : Window
{
    public DelayedDialogWindow()
    {
        InitializeComponent();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
