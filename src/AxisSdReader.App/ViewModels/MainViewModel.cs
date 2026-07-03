using System.Collections.ObjectModel;
using System.IO;
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

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeviceWatcher _watcher;

    private OpenCard? _card;
    private IReadOnlyList<CameraNode> _cameras = [];
    private readonly Dictionary<Recording, ClipRow> _clipRows = [];
    private ClipRow? _highlightedClip;

    // --- app state -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaiting))]
    private bool _isCardOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaiting))]
    private string _waitingMessage = "Insert an Axis camera SD card to begin.";

    public bool ShowWaiting => !IsCardOpen;

    // --- card identity / chrome ---------------------------------------------

    [ObservableProperty]
    private string _cardTitle = "";

    [ObservableProperty]
    private string _cardSubtitle = "";

    [ObservableProperty]
    private string _recCountLabel = "";

    [ObservableProperty]
    private string _camCountLabel = "";

    [ObservableProperty]
    private string _footageSpanLabel = "";

    [ObservableProperty]
    private string _mountLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchHasText))]
    private string _searchText = "";

    public bool SearchHasText => SearchText.Length > 0;

    // --- browse tree ---------------------------------------------------------

    public ObservableCollection<BrowseRow> Rows { get; } = [];

    public PlaybackViewModel Player { get; } = new();

    private string? _selectedCameraSerial;
    private string? _selectedDateKey;
    private string? _loadedCameraSerial;
    private readonly HashSet<string> _expandedCameras = [];
    private readonly HashSet<string> _expandedDates = [];

    // --- export dialog -------------------------------------------------------

    [ObservableProperty]
    private bool _showExport;

    [ObservableProperty]
    private string _exportFormat = "mp4";

    [ObservableProperty]
    private bool _includeStamp = true;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Player.ActiveRecordingChanged += OnActiveRecordingChanged;

        _watcher = new DeviceWatcher();
        _watcher.DiskArrived += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceChanged(); });
        _watcher.DiskRemoved += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceChanged(); });

        if (App.StartupImagePath is null)
        {
            _ = ScanAndOpenAsync();
        }
    }

    public async Task OpenImageFileAsync(string path) =>
        await OpenCardAsync(() => OpenCard.FromImage(path), $"Opening {Path.GetFileName(path)}…");

    private async Task OnDeviceChanged()
    {
        if (_card is { IsPhysicalDevice: true })
        {
            // A card is open; a removal may be ours. Verify it still reads; if not, close.
            return;
        }

        if (!IsCardOpen)
        {
            await ScanAndOpenAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAndOpen()
    {
        await ScanAndOpenAsync();
    }

    private async Task ScanAndOpenAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "Scanning for Axis cards…";
        WaitingMessage = "Scanning for Axis camera cards…";
        try
        {
            var card = await Task.Run(() => DiskEnumerator.GetPhysicalDisks()
                .Where(d => d.IsUsb && d.SizeBytes > 0)
                .Select(d => (Disk: d, Probe: AxisCardDetector.Probe(d.DiskNumber)))
                .FirstOrDefault(x => x.Probe.IsLikelyAxisCard));

            if (card.Disk is null)
            {
                WaitingMessage = "No Axis camera card detected.\nInsert a card, or open a card image.";
                return;
            }

            await OpenCardAsync(
                () => OpenCard.FromDevice(card.Disk.DiskNumber, card.Disk.FriendlyName, card.Disk.SizeBytes),
                "Opening card…");
        }
        finally
        {
            if (!IsCardOpen)
            {
                IsBusy = false;
                BusyText = "";
            }
        }
    }

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
            await OpenImageFileAsync(dialog.FileName);
        }
    }

    private async Task OpenCardAsync(Func<OpenCard> open, string busyText)
    {
        CloseCardInternal();
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
                    CardOpenStatus.IncompatibleExt4 => $"The card's filesystem uses unsupported features:\n{card.FailureDetail}",
                    _ => card.FailureDetail ?? "No readable Axis recordings were found on this card.",
                };
                card.Dispose();
                WaitingMessage = message;
                return;
            }

            _card = card;
            BusyText = "Indexing recordings…";
            var index = await card.IndexAsync();

            _cameras = BuildBrowseModel(index);
            ApplyCardIdentity(card, index);

            _expandedCameras.Clear();
            _expandedDates.Clear();
            _selectedCameraSerial = _cameras.FirstOrDefault()?.Serial;
            _selectedDateKey = _cameras.FirstOrDefault()?.Dates.FirstOrDefault()?.Key;
            if (_selectedCameraSerial is not null)
            {
                _expandedCameras.Add(_selectedCameraSerial);
            }

            if (_selectedDateKey is not null)
            {
                _expandedDates.Add(_selectedDateKey);
            }

            RebuildRows();
            IsCardOpen = true;

            if (_cameras.Count > 0)
            {
                await LoadCameraIntoPlayer(_cameras[0]);
            }
        }
        catch (Exception ex)
        {
            WaitingMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private static IReadOnlyList<CameraNode> BuildBrowseModel(AxisCard index)
    {
        var cameras = new List<CameraNode>();
        foreach (var camera in index.Cameras)
        {
            var dates = camera.Recordings
                .GroupBy(r => LocalDate(r))
                .OrderByDescending(g => g.Key)
                .Select(g => new DateNode(g.Key, g.OrderBy(r => r.StartTime).ToList()))
                .ToList();

            var name = $"Camera {camera.Serial[^Math.Min(4, camera.Serial.Length)..]}";
            cameras.Add(new CameraNode(camera.Serial, name, FormatMac(camera.Serial), dates));
        }

        return cameras;
    }

    private void ApplyCardIdentity(OpenCard card, AxisCard index)
    {
        var capacity = card.CapacityBytes is { } bytes ? $" · {FormatCapacity(bytes)}" : "";
        CardTitle = $"{card.Description}{capacity}";

        var total = index.Recordings.Count;
        RecCountLabel = $"{total:N0} recording{(total == 1 ? "" : "s")}";
        CardSubtitle = $"{RecCountLabel} · Axis edge storage";
        CamCountLabel = $"{index.Cameras.Count} camera{(index.Cameras.Count == 1 ? "" : "s")}";

        if (index.Recordings.Count > 0)
        {
            // Span covers recording ENDS too: an overnight recording extends the last day.
            var first = index.Recordings.Min(r => LocalDate(r));
            var last = index.Recordings
                .Max(r => LocalStart(r).Add(TimeSegment.EstimateDuration(r)).Date);
            FootageSpanLabel = first == last
                ? first.ToString("MMM d, yyyy")
                : $"{first:MMM d} – {last:MMM d, yyyy}";
        }
        else
        {
            FootageSpanLabel = "No recordings";
        }

        var label = card.VolumeLabel is { Length: > 0 } vl ? vl : "Axis";
        MountLabel = card.IsPhysicalDevice
            ? $"Locked read-only · ext4 · {label} volume"
            : $"Image · ext4 · {label} volume";
    }

    // --- browse tree ---------------------------------------------------------

    private void RebuildRows()
    {
        var query = SearchText.Trim();
        var hasQuery = query.Length > 0;

        _clipRows.Clear();
        _highlightedClip = null;
        Rows.Clear();

        foreach (var camera in _cameras)
        {
            var camExpanded = _expandedCameras.Contains(camera.Serial) || hasQuery;
            var cameraMatches = !hasQuery || camera.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || camera.Model.Contains(query, StringComparison.OrdinalIgnoreCase);

            var cameraRow = new CameraRow(camera, SelectCameraCommand)
            {
                IsSelected = camera.Serial == _selectedCameraSerial,
                IsExpanded = camExpanded,
            };

            var childRows = new List<BrowseRow>();
            if (camExpanded)
            {
                foreach (var date in camera.Dates)
                {
                    var dateExpanded = _expandedDates.Contains(date.Key) || hasQuery;
                    var clipRows = new List<ClipRow>();
                    if (dateExpanded)
                    {
                        foreach (var recording in date.Recordings)
                        {
                            var clip = new ClipRow(camera, date, recording, SelectClipCommand)
                            {
                                IsSelected = recording == Player.ActiveRecording,
                            };
                            if (hasQuery && !cameraMatches && !clip.TimeRange.Contains(query, StringComparison.OrdinalIgnoreCase)
                                && !date.LongLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            _clipRows[recording] = clip;
                            if (clip.IsSelected)
                            {
                                _highlightedClip = clip;
                            }

                            clipRows.Add(clip);
                        }
                    }

                    if (hasQuery && clipRows.Count == 0 && !cameraMatches
                        && !date.LongLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    childRows.Add(new DateRow(camera, date, SelectDateCommand)
                    {
                        IsSelected = camera.Serial == _selectedCameraSerial && date.Key == _selectedDateKey,
                        IsExpanded = dateExpanded,
                    });
                    childRows.AddRange(clipRows);
                }
            }

            if (hasQuery && !cameraMatches && childRows.Count == 0)
            {
                continue;
            }

            Rows.Add(cameraRow);
            foreach (var row in childRows)
            {
                Rows.Add(row);
            }
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildRows();

    [RelayCommand]
    private async Task SelectCamera(CameraRow row)
    {
        var serial = row.Node.Serial;
        if (!_expandedCameras.Remove(serial))
        {
            _expandedCameras.Add(serial);
        }

        _selectedCameraSerial = serial;
        RebuildRows();
        await LoadCameraIntoPlayer(row.Node);
    }

    [RelayCommand]
    private async Task SelectDate(DateRow row)
    {
        var key = row.Node.Key;
        if (!_expandedDates.Remove(key))
        {
            _expandedDates.Add(key);
        }

        _selectedCameraSerial = row.Camera.Serial;
        _selectedDateKey = key;
        RebuildRows();

        await LoadCameraIntoPlayer(row.Camera);
        var first = row.Node.Recordings.FirstOrDefault();
        if (first is not null)
        {
            await Player.SeekToRecording(first);
        }
    }

    [RelayCommand]
    private async Task SelectClip(ClipRow row)
    {
        _selectedCameraSerial = row.Camera.Serial;
        _selectedDateKey = row.Date.Key;
        await LoadCameraIntoPlayer(row.Camera);
        await Player.SeekToRecording(row.Recording);
    }

    /// <summary>Loads all of a camera's recordings into the player (no-op if already loaded).</summary>
    private async Task LoadCameraIntoPlayer(CameraNode camera)
    {
        if (_card is null || _loadedCameraSerial == camera.Serial)
        {
            return;
        }

        Player.ClearSelection();
        var recordings = camera.Dates.SelectMany(d => d.Recordings).OrderBy(r => r.StartTime).ToList();
        await Player.LoadCamera(_card, camera.Name, camera.Model, recordings);
        _loadedCameraSerial = camera.Serial;
    }

    private void OnActiveRecordingChanged(Recording? recording)
    {
        if (_highlightedClip is not null)
        {
            _highlightedClip.IsSelected = false;
            _highlightedClip = null;
        }

        if (recording is not null && _clipRows.TryGetValue(recording, out var row))
        {
            row.IsSelected = true;
            _highlightedClip = row;

            // Metadata is loaded by the seek that made this recording active — labels can
            // now show exact times, and the MKV header tells us the true camera model.
            row.RefreshLabels();
            var model = recording.Chunks
                .Select(c => c.Metadata?.WritingApp)
                .FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));
            if (model is not null && Player.CameraModel != model)
            {
                Player.CameraModel = model;
                var camera = _cameras.FirstOrDefault(c => c.Serial == _selectedCameraSerial);
                if (camera is not null)
                {
                    camera.Model = model;
                }
            }
        }
    }

    // --- export --------------------------------------------------------------

    [RelayCommand]
    private void OpenExport()
    {
        if (Player.HasSelection)
        {
            ShowExport = true;
        }
    }

    [RelayCommand]
    private void CloseExport() => ShowExport = false;

    [RelayCommand]
    private void SetExportFormat(string format) => ExportFormat = format;

    [RelayCommand]
    private void ToggleStamp() => IncludeStamp = !IncludeStamp;

    [RelayCommand]
    private async Task DoExport()
    {
        if (_card is null || !Player.HasSelection)
        {
            return;
        }

        var recordings = Player.RecordingsInSelection();
        if (recordings.Count == 0)
        {
            MessageBox.Show("The selected range contains no recordings.", "Nothing to export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose export destination" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ShowExport = false;
        IsBusy = true;
        try
        {
            var fs = _card.FileSystem!;
            long totalBytes = 0;
            var totalFiles = 0;
            foreach (var recording in recordings)
            {
                var progress = new Progress<Core.Export.ExportProgress>(p =>
                {
                    var pct = p.BytesTotal > 0 ? p.BytesDone * 100 / p.BytesTotal : 0;
                    BusyText = $"Exporting {p.CurrentFile} ({pct}%)";
                });
                var result = await _card.RunExclusive(() =>
                    Core.Export.RecordingExporter.Export(fs, recording, dialog.FolderName, progress));
                totalBytes += result.BytesExported;
                totalFiles += result.FilesExported;
            }

            MessageBox.Show(
                $"Exported {totalFiles} original recording file{(totalFiles == 1 ? "" : "s")} " +
                $"({totalBytes / (1024.0 * 1024):F0} MB) to:\n{dialog.FolderName}\n\n" +
                "Files are lossless copies of the on-card MKV recordings. The card was not modified.",
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
    private void CloseCard() => CloseCardInternal();

    private void CloseCardInternal()
    {
        Player.ClearSelection();
        Rows.Clear();
        _clipRows.Clear();
        _cameras = [];
        _loadedCameraSerial = null;
        IsCardOpen = false;
        _card?.Dispose();
        _card = null;
    }

    // --- window commands -----------------------------------------------------

    [RelayCommand]
    private static void Minimize() =>
        System.Windows.Application.Current.MainWindow.WindowState = WindowState.Minimized;

    [RelayCommand]
    private static void ToggleMaximize()
    {
        var w = System.Windows.Application.Current.MainWindow;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    [RelayCommand]
    private static void CloseWindow() => System.Windows.Application.Current.MainWindow.Close();

    // --- helpers -------------------------------------------------------------

    private static DateTime LocalStart(Recording r)
    {
        var s = r.StartTime;
        return s.Kind == DateTimeKind.Utc ? s.ToLocalTime() : s;
    }

    private static DateTime LocalDate(Recording r) => LocalStart(r).Date;

    private static string FormatMac(string serial)
    {
        if (serial.Length != 12)
        {
            return serial;
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(i => serial.Substring(i * 2, 2)));
    }

    private static string FormatCapacity(long bytes)
    {
        var gb = bytes / (1024.0 * 1024 * 1024);
        return gb >= 1000 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";
    }

    public void Dispose()
    {
        CloseCardInternal();
        Player.Dispose();
        _watcher.Dispose();
    }
}
