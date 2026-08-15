using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace WidgetApp;

public sealed partial class WidgetPage : Page
{
    private int _leftClickCount;
    private int _rightClickCount;
    private int _contextMenuOpenCount;
    private int _contextActionCount;

    public WidgetPage()
    {
        InitializeComponent();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _leftClickCount++;
        UpdateStatus();
    }

    private void InteractionSurface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _rightClickCount++;
        UpdateStatus();
        e.Handled = true;
    }

    private void RemoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTextStatus();
    }

    private void TextContextFlyout_Opened(object sender, object e)
    {
        _contextMenuOpenCount++;
        UpdateTextStatus();
    }

    private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RemoteTextBox.Focus(FocusState.Programmatic);
        RemoteTextBox.SelectAll();
        RecordContextAction();
    }

    private void InsertSampleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RemoteTextBox.Text = "Hello from WidgetApp.exe";
        RemoteTextBox.SelectionStart = RemoteTextBox.Text.Length;
        RemoteTextBox.Focus(FocusState.Programmatic);
        RecordContextAction();
    }

    private void ClearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RemoteTextBox.ClearValue(TextBox.TextProperty);
        RemoteTextBox.Focus(FocusState.Programmatic);
        RecordContextAction();
    }

    private void RecordContextAction()
    {
        _contextActionCount++;
        UpdateTextStatus();
    }

    private void UpdateStatus()
    {
        EventStatus.Text = $"Left clicks: {_leftClickCount}  ·  Right clicks: {_rightClickCount}";
    }

    private void UpdateTextStatus()
    {
        if (TextStatus is null)
        {
            return;
        }

        TextStatus.Text =
            $"Text length: {RemoteTextBox.Text.Length}  ·  " +
            $"Menu opens: {_contextMenuOpenCount}  ·  Actions: {_contextActionCount}";
    }
}
