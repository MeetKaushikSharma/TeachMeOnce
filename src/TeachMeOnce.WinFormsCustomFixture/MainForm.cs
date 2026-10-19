using System.Drawing;

namespace TeachMeOnce.WinFormsCustomFixture;

public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "TeachMeOnce WinForms Custom Fixture";
        Width = 760;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new CustomRenderedSurface { Dock = DockStyle.Fill });
    }
}
