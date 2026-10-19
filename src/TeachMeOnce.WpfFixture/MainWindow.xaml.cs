using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace TeachMeOnce.WpfFixture;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InvokeButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Status: invoked";
    }

    private void RecreateValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        ValueControlHost.Children.Clear();
        ValueControlHost.Children.Add(CreateValueTextBox());
    }

    private void AddDuplicateValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        ValueControlHost.Children.Add(CreateValueTextBox());
    }

    private void RemoveDuplicateValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ValueControlHost.Children.Count > 1)
        {
            ValueControlHost.Children.RemoveAt(ValueControlHost.Children.Count - 1);
        }
    }

    private void RemoveValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        ValueControlHost.Children.Clear();
    }

    private static TextBox CreateValueTextBox()
    {
        var textBox = new TextBox { Text = "editable value" };
        AutomationProperties.SetAutomationId(textBox, "ValueTextBox");
        return textBox;
    }

    private async void DelayedDialogButton_OnClick(object sender, RoutedEventArgs e)
    {
        await Task.Delay(750);
        if (AsyncWaitHost.Children.Count == 0)
        {
            var delayedElement = new TextBlock { Text = "Delayed element appeared." };
            AutomationProperties.SetAutomationId(delayedElement, "DelayedElement");
            AsyncWaitHost.Children.Add(delayedElement);
        }

        var dialog = new DelayedDialogWindow { Owner = this };
        dialog.ShowDialog();
    }
}
