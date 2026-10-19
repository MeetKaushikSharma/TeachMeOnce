using System.Drawing;
using System.Windows.Forms;

namespace TeachMeOnce.WinFormsFixture;

public sealed class DelayedDialogForm : Form
{
    public DelayedDialogForm()
    {
        Text = "Fixture dialog";
        Name = "DelayedDialogWindow";
        AccessibleName = "Fixture dialog";
        Width = 280;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var message = new Label
        {
            AutoSize = true,
            Text = "Delayed dialog appeared.",
            Name = "DialogMessage",
            AccessibleName = "DialogMessage",
            Location = new Point(20, 20)
        };

        var okButton = new Button
        {
            AutoSize = true,
            Text = "OK",
            Name = "DialogOkButton",
            AccessibleName = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(180, 70)
        };

        AcceptButton = okButton;
        Controls.Add(message);
        Controls.Add(okButton);
    }
}
