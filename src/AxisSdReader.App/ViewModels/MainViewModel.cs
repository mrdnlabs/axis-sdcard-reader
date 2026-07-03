using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AxisSdReader.App.ViewModels;

/// <summary>A physical disk shown in the device list.</summary>
public sealed record DeviceItem(int DiskNumber, string DisplayName, bool IsUsb)
{
    public override string ToString() => DisplayName;
}

/// <summary>A camera node in the recordings tree.</summary>
public sealed record CameraNode(string Serial, IReadOnlyList<RecordingItem> Recordings)
{
    public string DisplayName => $"Camera {Serial}  ({Recordings.Count} recordings)";
}

/// <summary>A recording row in the recordings tree.</summary>
public sealed partial class RecordingItem : ObservableObject
{
    public RecordingItem(Recording recording)
    {
        Recording = recording;
        Details = $"{recording.Chunks.Count} chunks · {recording.TotalSizeBytes / (1024.0 * 1024):F0} MB";
    }

    public Recording Recording { get; }

    public string DisplayName =>
        (Recording.StartTime.Kind == DateTimeKind.Utc ? Recording.StartTime.ToLocalTime() : Recording.StartTime)
        .ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty]
    private string _details;

    public void RefreshDetails()
    {
        var duration = Recording.Duration is { } d ? $" · {d:hh\\:mm\\:ss}" : "";
        var codec = Recording.VideoCodecId switch
        {
            "V_MPEG4/ISO/AVC" => " · H.264",
            "V_MPEGH/ISO/HEVC" => " · H.265",
            "V_AV1" => " · AV1",
            null => "",
            var other => $" · {other}",
        };
        var interrupted = Recording.WasInterrupted ? " · interrupted" : "";
        Details = $"{Recording.Chunks.Count} chunks · {Recording.TotalSizeBytes / (1024.0 * 1024):F0} MB{duration}{codec}{interrupted}";
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeviceWatcher _watcher;
    private OpenCard? _card;

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public ObservableCollection<CameraNode> Cameras { get; } = [];

    public PlayerViewModel Player { get; } = new();

    [ObservableProperty]
    private DeviceItem? _selectedDevice;

    [ObservableProperty]
    private RecordingItem? _selectedRecording;

    [ObservableProperty]
    private bool _isCardOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private string _statusBanner = "";

    [ObservableProperty]
    private string _protectionDetails = "";

    [ObservableProperty]
    private string _cardDescription = "";

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _watcher = new DeviceWatcher();
        _watcher.DiskArrived += _ => _dispatcher.BeginInvoke(RefreshDevices);
        _watcher.DiskRemoved += _ => _dispatcher.BeginInvoke(RefreshDevices);
        RefreshDevices();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        var previous = SelectedDevice?.DiskNumber;
        Devices.Clear();

        foreach (var disk in DiskEnumerator.GetPhysicalDisks())
        {
            var size = disk.SizeBytes is { } s ? $"{s / (1024.0 * 1024 * 1024):F1} GB" : "?";
            Devices.Add(new DeviceItem(
                disk.DiskNumber,
                $"#{disk.DiskNumber}  {disk.FriendlyName}  ({size}{(disk.IsUsb ? ", USB" : "")})",
                disk.IsUsb));
        }

        // Prefer re-selecting the same disk, else the first USB device (likely the card reader).
        SelectedDevice = Devices.FirstOrDefault(d => d.DiskNumber == previous)
            ?? Devices.FirstOrDefault(d => d.IsUsb)
            ?? Devices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task OpenSelectedDevice()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        if (!device.IsUsb &&
            MessageBox.Show(
                $"{device.DisplayName} is not a USB device - it may be a system disk. Open it anyway?",
                "Not a USB device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await OpenCardAsync(
            () => OpenCard.FromDevice(device.DiskNumber, device.DisplayName),
            $"Opening disk #{device.DiskNumber}...");
    }

    /// <summary>Opens a card image directly (command-line startup path).</summary>
    public Task OpenImageFileAsync(string path) =>
        OpenCardAsync(() => OpenCard.FromImage(path), "Opening image...");

    [RelayCommand]
    private async Task OpenImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open card image",
            Filter = "Card images (*.img;*.dd;*.raw;*.bin)|*.img;*.dd;*.raw;*.bin|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await OpenCardAsync(() => OpenCard.FromImage(dialog.FileName), "Opening image...");
        }
    }

    private async Task OpenCardAsync(Func<OpenCard> open, string busyText)
    {
        CloseCard();
        IsBusy = true;
        BusyText = busyText;

        try
        {
            var card = await Task.Run(open);

            if (card.Status != CardOpenStatus.Ok)
            {
                var message = card.Status switch
                {
                    CardOpenStatus.Encrypted => "This card is encrypted by the camera and cannot be read here.",
                    CardOpenStatus.IncompatibleExt4 => $"The card's ext4 filesystem uses unsupported features:\n{card.FailureDetail}",
                    _ => card.FailureDetail ?? "No readable filesystem found.",
                };
                card.Dispose();
                MessageBox.Show(message, "Cannot read card", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _card = card;
            CardDescription = card.Description;
            StatusBanner = card.IsPhysicalDevice
                ? "🔒 Card locked and opened read-only - Windows cannot write to or format it while this app is open."
                : "Image file opened read-only.";
            ProtectionDetails = string.Join(Environment.NewLine, card.ProtectionLog);

            BusyText = "Indexing recordings...";
            var index = await card.IndexAsync();

            Cameras.Clear();
            foreach (var camera in index.Cameras)
            {
                Cameras.Add(new CameraNode(
                    camera.Serial,
                    camera.Recordings.Select(r => new RecordingItem(r)).ToList()));
            }

            IsCardOpen = true;

            if (index.Recordings.Count == 0)
            {
                MessageBox.Show(
                    "The card was read successfully but contains no Axis recordings.",
                    "No recordings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error opening card", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private async Task ExportSelectedRecording()
    {
        if (_card is null || SelectedRecording is not { } item)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose export destination" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<Core.Export.ExportProgress>(p =>
            {
                var pct = p.BytesTotal > 0 ? p.BytesDone * 100 / p.BytesTotal : 0;
                BusyText = $"Exporting {p.CurrentFile}  ({p.FilesDone}/{p.FilesTotal} files, {pct}%)";
            });

            var fs = _card.FileSystem!;
            var result = await _card.RunExclusive(() =>
                Core.Export.RecordingExporter.Export(fs, item.Recording, dialog.FolderName, progress));

            MessageBox.Show(
                $"Exported {result.FilesExported} files ({result.BytesExported / (1024.0 * 1024):F0} MB) to:\n{result.TargetDirectory}",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private void CloseCard()
    {
        Player.Stop();
        SelectedRecording = null;
        Cameras.Clear();
        IsCardOpen = false;
        StatusBanner = "";
        ProtectionDetails = "";
        CardDescription = "";
        _card?.Dispose();
        _card = null;
    }

    partial void OnSelectedRecordingChanged(RecordingItem? value)
    {
        if (value is not null)
        {
            _ = LoadAndPlayAsync(value);
        }
    }

    private async Task LoadAndPlayAsync(RecordingItem item)
    {
        if (_card is null)
        {
            return;
        }

        IsBusy = true;
        BusyText = "Reading recording metadata...";
        try
        {
            await _card.LoadMetadataAsync(item.Recording);
            item.RefreshDetails();
            Player.Load(_card, item.Recording);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error reading recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public void Dispose()
    {
        CloseCard();
        Player.Dispose();
        _watcher.Dispose();
    }
}
