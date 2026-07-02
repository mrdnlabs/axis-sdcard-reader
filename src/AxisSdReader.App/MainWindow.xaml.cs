using System.Windows;
using AxisSdReader.App.ViewModels;

namespace AxisSdReader.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();

        if (App.StartupImagePath is { } imagePath)
        {
            Loaded += async (_, _) => await _viewModel.OpenImageFileAsync(imagePath);
        }
    }

    private void OnRecordingSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is RecordingItem recording)
        {
            _viewModel.SelectedRecording = recording;
        }
    }

    private void OnSliderDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => _viewModel.Player.SeekSession(TimelineSlider.Value);

    private void OnSliderClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _viewModel.Player.SeekSession(TimelineSlider.Value);
}
