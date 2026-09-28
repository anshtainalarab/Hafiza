using System.Windows;
using System.Windows.Input;
using Hafiza.Services;

namespace Hafiza;

public partial class TabNameDialog : Window
{
    public string TabName => NameBox.Text.Trim();
    private readonly bool _allowEmpty;

    public TabNameDialog(string currentName = "", string? prompt = null, bool allowEmpty = false)
    {
        InitializeComponent();
        FlowDirection = LocalizationService.CurrentFlowDirection;
        _allowEmpty = allowEmpty;
        PromptText.Text = prompt ?? LocalizationService.Get("DialogTabNameTitle");
        Title = PromptText.Text;
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowEmpty && string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
