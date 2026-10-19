using System.Drawing;
using System.Windows.Forms;

namespace TeachMeOnce.WinFormsFixture;

public sealed class MainForm : Form
{
    private readonly FlowLayoutPanel _valueControlHost;
    private readonly FlowLayoutPanel _asyncWaitHost;

    public MainForm()
    {
        Text = "TeachMeOnce WinForms Fixture";
        Name = "WinFormsFixtureWindow";
        Width = 720;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(24)
        };

        var heading = new Label
        {
            AutoSize = true,
            Text = "UI Automation coverage fixture",
            Font = new Font(Font, FontStyle.Bold),
            AccessibleName = "FixtureHeading"
        };
        layout.Controls.Add(heading);

        var statusLabel = new Label
        {
            AutoSize = true,
            Text = "Status: ready",
            Name = "StatusLabel",
            AccessibleName = "StatusLabel"
        };

        var invokeButton = new Button
        {
            AutoSize = true,
            Text = "Invoke button",
            Name = "InvokeButton",
            AccessibleName = "InvokeButton"
        };
        invokeButton.Click += (_, _) =>
        {
            statusLabel.Text = "Status: invoked";
            statusLabel.AccessibleName = "Status: invoked";
        };
        layout.Controls.Add(invokeButton);
        layout.Controls.Add(statusLabel);

        _valueControlHost = new FlowLayoutPanel
        {
            AutoSize = true,
            Width = 320,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Name = "ValueControlHost",
            AccessibleName = "ValueControlHost"
        };
        _valueControlHost.Controls.Add(CreateValueTextBox());
        layout.Controls.Add(_valueControlHost);
        layout.Controls.Add(CreateFixtureButton("Recreate value control", "RecreateValueButton", (_, _) => RecreateValueControl()));
        layout.Controls.Add(CreateFixtureButton("Add duplicate value control", "AddDuplicateValueButton", (_, _) => AddDuplicateValueControl()));
        layout.Controls.Add(CreateFixtureButton("Remove duplicate value control", "RemoveDuplicateValueButton", (_, _) => RemoveDuplicateValueControl()));
        layout.Controls.Add(CreateFixtureButton("Remove value control", "RemoveValueButton", (_, _) => RemoveValueControl()));

        _asyncWaitHost = new FlowLayoutPanel
        {
            AutoSize = true,
            Width = 320,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Name = "AsyncWaitHost",
            AccessibleName = "AsyncWaitHost"
        };
        layout.Controls.Add(_asyncWaitHost);

        layout.Controls.Add(new TextBox
        {
            Width = 320,
            UseSystemPasswordChar = true,
            Name = "PasswordBox",
            AccessibleName = "PasswordBox"
        });
        layout.Controls.Add(new CheckBox
        {
            AutoSize = true,
            Text = "Toggle option",
            Name = "ToggleCheckBox",
            AccessibleName = "ToggleCheckBox"
        });

        var comboBox = new ComboBox
        {
            Width = 320,
            Name = "SelectionComboBox",
            AccessibleName = "SelectionComboBox",
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        comboBox.Items.AddRange(["First option", "Second option", "Third option"]);
        comboBox.SelectedIndex = 0;
        layout.Controls.Add(comboBox);

        var listBox = new ListBox
        {
            Width = 320,
            Height = 90,
            Name = "SelectionListBox",
            AccessibleName = "SelectionListBox"
        };
        listBox.Items.AddRange(["List item one", "List item two", "List item three"]);
        listBox.SelectedIndex = 0;
        layout.Controls.Add(listBox);

        var delayedButton = new Button
        {
            AutoSize = true,
            Text = "Show delayed dialog",
            Name = "DelayedDialogButton",
            AccessibleName = "DelayedDialogButton"
        };
        delayedButton.Click += async (_, _) =>
        {
            await Task.Delay(750);
            if (_asyncWaitHost.Controls.Count == 0)
            {
                _asyncWaitHost.Controls.Add(new Label
                {
                    AutoSize = true,
                    Text = "Delayed element appeared.",
                    Name = "DelayedElement",
                    AccessibleName = "DelayedElement"
                });
            }

            using var dialog = new DelayedDialogForm();
            dialog.ShowDialog(this);
        };
        layout.Controls.Add(delayedButton);

        Controls.Add(layout);
    }

    private static Button CreateFixtureButton(string text, string automationId, EventHandler handler)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = text,
            Name = automationId,
            AccessibleName = automationId
        };
        button.Click += handler;
        return button;
    }

    private static TextBox CreateValueTextBox()
    {
        return new TextBox
        {
            Width = 320,
            Text = "editable value",
            Name = "ValueTextBox",
            AccessibleName = "ValueTextBox"
        };
    }

    private void RecreateValueControl()
    {
        ClearValueControls();
        _valueControlHost.Controls.Add(CreateValueTextBox());
    }

    private void AddDuplicateValueControl()
    {
        _valueControlHost.Controls.Add(CreateValueTextBox());
    }

    private void RemoveDuplicateValueControl()
    {
        if (_valueControlHost.Controls.Count > 1)
        {
            var last = _valueControlHost.Controls[_valueControlHost.Controls.Count - 1];
            _valueControlHost.Controls.RemoveAt(_valueControlHost.Controls.Count - 1);
            last.Dispose();
        }
    }

    private void RemoveValueControl()
    {
        ClearValueControls();
    }

    private void ClearValueControls()
    {
        while (_valueControlHost.Controls.Count > 0)
        {
            var control = _valueControlHost.Controls[0];
            _valueControlHost.Controls.RemoveAt(0);
            control.Dispose();
        }
    }
}
