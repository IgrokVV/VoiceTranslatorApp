using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VoiceTranslatorApp.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ShowVoiceModels(object? sender, RoutedEventArgs e)
        {
            VoiceModelsPanel.IsVisible = true;
            TranslationPanel.IsVisible = false;

            VoiceModelsButton.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            VoiceModelsButton.Foreground = Brushes.White;
            TranslationButton.Background = new SolidColorBrush(Color.Parse("#1E293B"));
            TranslationButton.Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"));
        }

        private void ShowTranslation(object? sender, RoutedEventArgs e)
        {
            VoiceModelsPanel.IsVisible = false;
            TranslationPanel.IsVisible = true;

            TranslationButton.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            TranslationButton.Foreground = Brushes.White;
            VoiceModelsButton.Background = new SolidColorBrush(Color.Parse("#1E293B"));
            VoiceModelsButton.Foreground = new SolidColorBrush(Color.Parse("#E2E8F0"));
        }
    }
}