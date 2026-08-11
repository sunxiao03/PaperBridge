using System.Text;
using System.Windows;
using System.Windows.Controls;
using PaperBridge.Application.Translation;

namespace PaperBridge.App;

public partial class TranslationSettingsDialog : Window
{
    private bool _loading;

    public TranslationSettingsDialog(TranslationServiceSettings settings, bool hasStoredKey)
    {
        InitializeComponent();
        _loading = true;
        foreach (var item in ProviderComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, settings.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                ProviderComboBox.SelectedItem = item;
                break;
            }
        }

        ProviderComboBox.SelectedIndex = ProviderComboBox.SelectedIndex < 0 ? 2 : ProviderComboBox.SelectedIndex;
        BaseUrlTextBox.Text = settings.BaseUrl;
        ModelTextBox.Text = settings.Model;
        CustomInstructionTextBox.Text = settings.CustomInstruction ?? string.Empty;
        KeyStatusText.Text = hasStoredKey
            ? "已保存密钥；留空表示继续使用现有密钥。"
            : "尚未保存密钥。";
        _loading = false;
    }

    public TranslationServiceSettings? ResultSettings { get; private set; }

    public string? NewApiKey { get; private set; }

    public string? DeleteKeyForProviderId { get; private set; }

    private string SelectedProviderId =>
        (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string
        ?? TranslationServiceSettings.CompatibleProviderId;

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        switch (SelectedProviderId)
        {
            case TranslationServiceSettings.OpenAiProviderId:
                BaseUrlTextBox.Text = "https://api.openai.com/v1/";
                ModelTextBox.Text = "gpt-4.1-mini";
                break;
            case TranslationServiceSettings.DeepSeekProviderId:
                BaseUrlTextBox.Text = "https://api.deepseek.com/v1/";
                ModelTextBox.Text = "deepseek-chat";
                break;
        }

        KeyStatusText.Text = "如该服务商已有密钥，留空会继续使用；否则请输入新密钥。";
    }

    private void DeleteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteKeyForProviderId = SelectedProviderId;
        ApiKeyPasswordBox.Clear();
        KeyStatusText.Text = "保存设置后删除该服务商的已存密钥。";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultSettings = new TranslationServiceSettings(
                SelectedProviderId,
                BaseUrlTextBox.Text,
                ModelTextBox.Text,
                CustomInstructionTextBox.Text,
                RequestTimeoutSeconds: 60,
                MaxConcurrency: 2).Validate();
            NewApiKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? null
                : ApiKeyPasswordBox.Password;
            if (NewApiKey is not null && Encoding.Unicode.GetByteCount(NewApiKey) > 2560)
            {
                throw new ArgumentException("API Key 超过 Windows Credential Manager 的 2560 字节限制。", nameof(NewApiKey));
            }

            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
