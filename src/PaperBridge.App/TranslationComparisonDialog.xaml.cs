using System.Windows;

namespace PaperBridge.App;

public partial class TranslationComparisonDialog : Window
{
    public TranslationComparisonDialog(string machineTranslation, string userTranslation)
    {
        InitializeComponent();
        MachineTextBox.Text = machineTranslation;
        UserTextBox.Text = userTranslation;
    }
}
