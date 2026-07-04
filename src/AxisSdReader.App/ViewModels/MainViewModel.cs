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

    /// <summary>True when the open card is genuinely protected (image, or every volume locked).</summary>
    [ObservableProperty]
    private bool _isCardProtected = true;

    /// <summary>Trust-banner headline reflecting the real protection outcome (never hardcoded).</summary>
    [ObservableProperty]
    private string _protectionHeadline = "Card locked · Read-only";

    [ObservableProperty]
    private string _protectionDetail = "Footage cannot be changed or deleted.";

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
    private readonly HashSet<string> _expandedLenses = [];
    private readonly HashSet<string> _expandedDates = [];

    // --- lens bar --------------------------------------------------------------

    public ObservableCollection<LensTab> LensTabs { get; } = [];

    [ObservableProperty]
    private bool _showLensBar;

    [ObservableProperty]
    private string _lensHint = "";

    // --- boot / splash overlay --------------------------------------------------

    [ObservableProperty]
    private bool _bootVisible;

    [ObservableProperty]
    private string _bootStageLabel = "Detecting SD card…";

    [ObservableProperty]
    private string _bootDetail = "";

    [ObservableProperty]
    private double _bootFraction; // 0..1 progress-bar fill

    [ObservableProperty]
    private bool _bootDone;

    [ObservableProperty]
    private string _bootFooter = "click anywhere to skip";

    private bool _bootSkipped;

    // --- theme --------------------------------------------------------------------

    [ObservableProperty]
    private bool _isDarkTheme = true;

    [ObservableProperty]
    private string _accentName = "Trust Blue";

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        Theme.Apply(IsDarkTheme, AccentName);
    }

    [RelayCommand]
    private void SetAccent(string name)
    {
        AccentName = name;
        Theme.Apply(IsDarkTheme, name);
    }

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
        _watcher.DiskArrived += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceArrived(); });
        _watcher.DiskRemoved += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceRemoved(); });

        if (App.StartupImagePath is null)
        {
            _ = ScanAndOpenAsync();
        }
    }

    public async Task OpenImageFileAsync(string path) =>
        await OpenCardAsync(() => OpenCard.FromImage(path), $"Opening {Path.GetFileName(path)}…");

    private async Task OnDeviceArrived()
    {
        if (!IsCardOpen && !IsBusy)
        {
            await ScanAndOpenAsync();
        }
    }

    private async Task OnDeviceRemoved()
    {
        if (_card is not { IsPhysicalDevice: true, DiskNumber: { } diskNumber })
        {
            return;
        }

        // Our card may be the device that was removed. Re-enumerate: if our disk number is gone, or its
        // slot now reports no media, the card is no longer present — close it and return to the waiting
        // state rather than leaving a dead session whose next read would throw.
        var stillPresent = await Task.Run(() =>
            DiskEnumerator.GetPhysicalDisks().Any(d => d.DiskNumber == diskNumber && d.SizeBytes > 0));

        if (!stillPresent)
        {
            CloseCardInternal();
            WaitingMessage = "The card was removed. Insert an Axis camera SD card to begin.";
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
        _bootSkipped = false;
        SetBootStage("Detecting SD card…", 0.08, "scanning USB readers");
        try
        {
            var card = await Task.Run(() => DiskEnumerator.GetPhysicalDisks()
                .Where(d => d.IsUsb && d.SizeBytes > 0)
                .Select(d => (Disk: d, Probe: AxisCardDetector.Probe(d.DiskNumber)))
                .FirstOrDefault(x => x.Probe.IsLikelyAxisCard));

            if (card.Disk is null)
            {
                WaitingMessage = "No Axis camera card detected.\nInsert a card, or open a card image.";
                BootVisible = false;
                return;
            }

            SetBootStage("Detecting SD card…", 0.15, $"{card.Disk.FriendlyName} · ext4");

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
        SetBootStage("Verifying read-only mount…", 0.25, "locking volume");

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
                BootVisible = false;
                return;
            }

            _card = card;
            BusyText = "Indexing recordings…";
            SetBootStage("Indexing recordings…", 0.45, "0 recordings found");
            BootFooter = $"{card.Description} — click anywhere to skip";

            var indexProgress = new Progress<int>(count =>
            {
                BootDetail = $"{count} recording{(count == 1 ? "" : "s")} found";
                BootFraction = Math.Min(0.92, 0.45 + count * 0.01);
            });
            var index = await card.IndexAsync(count => ((IProgress<int>)indexProgress).Report(count));

            _cameras = BuildBrowseModel(index);
            ApplyCardIdentity(card, index);

            _expandedCameras.Clear();
            _expandedLenses.Clear();
            _expandedDates.Clear();
            _selectedDateKey = null;

            var firstCamera = _cameras.FirstOrDefault();
            _selectedCameraSerial = firstCamera?.Serial;
            if (firstCamera is not null)
            {
                _expandedCameras.Add(firstCamera.Serial);
                var lens = firstCamera.ActiveLens;
                if (firstCamera.IsMultiLens)
                {
                    _expandedLenses.Add(LensKey(firstCamera, lens));
                }

                var firstDate = lens.Dates.FirstOrDefault();
                if (firstDate is not null)
                {
                    _selectedDateKey = DateKey(firstCamera, lens, firstDate);
                    _expandedDates.Add(_selectedDateKey);
                }
            }

            RebuildRows();
            IsCardOpen = true;

            if (_cameras.Count > 0)
            {
                SetBootStage("Preparing player…", 0.95, "starting video engine");
                await LoadCameraIntoPlayer(_cameras[0]);
            }

            // Boot complete: brief "Ready to review" beat, then reveal the app.
            SetBootStage("Ready to review", 1.0, RecCountLabel);
            BootDone = true;
            if (!_bootSkipped)
            {
                await Task.Delay(650);
            }

            BootVisible = false;
        }
        catch (Exception ex)
        {
            WaitingMessage = ex.Message;
            BootVisible = false;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void SetBootStage(string stage, double fraction, string detail)
    {
        if (_bootSkipped)
        {
            return;
        }

        BootVisible = true;
        BootDone = false;
        BootStageLabel = stage;
        BootFraction = fraction;
        BootDetail = detail;
    }

    [RelayCommand]
    private void SkipBoot()
    {
        _bootSkipped = true;
        BootVisible = false;
    }

    /// <summary>Replays the intro (title-bar logo click) using the already-open card's data.</summary>
    [RelayCommand]
    private async Task ReplayIntro()
    {
        if (!IsCardOpen || BootVisible)
        {
            return;
        }

        _bootSkipped = false;
        BootDone = false;
        foreach (var (stage, fraction, delay) in new[]
                 {
                     ("Detecting SD card…", 0.15, 350),
                     ("Verifying read-only mount…", 0.40, 350),
                     ("Indexing recordings…", 0.80, 420),
                 })
        {
            SetBootStage(stage, fraction, RecCountLabel);
            await Task.Delay(delay);
            if (_bootSkipped)
            {
                return;
            }
        }

        SetBootStage("Ready to review", 1.0, RecCountLabel);
        BootDone = true;
        await Task.Delay(600);
        BootVisible = false;
    }

    private static IReadOnlyList<CameraNode> BuildBrowseModel(AxisCard index)
    {
        var cameras = new List<CameraNode>();
        foreach (var camera in index.Cameras)
        {
            // Multi-sensor cameras record each lens as a separate VAPIX source. Order numeric
            // sources naturally (1, 3, 4, 5) so gaps are visible.
            var lenses = camera.Recordings
                .GroupBy(r => r.SourceToken)
                .Select(g => new LensNode(g.Key, g
                    .GroupBy(LocalDate)
                    .OrderByDescending(d => d.Key)
                    .Select(d => new DateNode(d.Key, d.OrderBy(r => r.StartTime).ToList()))
                    .ToList()))
                .OrderBy(l => l.SourceNumber ?? int.MaxValue)
                .ThenBy(l => l.SourceToken, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var name = $"Camera {camera.Serial[^Math.Min(4, camera.Serial.Length)..]}";
            cameras.Add(new CameraNode(camera.Serial, name, FormatMac(camera.Serial), lenses));
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

        // Reflect the ACTUAL protection outcome, never a hardcoded reassurance. An image file needs no
        // protection; a physical card is "protected" only when every volume was locked.
        IsCardProtected = !card.IsPhysicalDevice || card.FullyProtected;
        if (!card.IsPhysicalDevice)
        {
            MountLabel = $"Image · ext4 · {label} volume";
            ProtectionHeadline = "Read-only image";
            ProtectionDetail = "Opened from a file — the source is never modified.";
        }
        else if (card.FullyProtected)
        {
            MountLabel = $"Locked read-only · ext4 · {label} volume";
            ProtectionHeadline = "Card locked · Read-only";
            ProtectionDetail = "Footage cannot be changed or deleted.";
        }
        else
        {
            MountLabel = $"NOT fully locked · ext4 · {label} volume";
            ProtectionHeadline = "Card not fully locked";
            ProtectionDetail = "Windows still has access — do not click any \"format disk\" prompt.";
        }
    }

    // --- browse tree ---------------------------------------------------------

    private static string LensKey(CameraNode camera, LensNode lens) => $"{camera.Serial}|{lens.SourceToken}";

    private static string DateKey(CameraNode camera, LensNode lens, DateNode date) =>
        $"{camera.Serial}|{lens.SourceToken}|{date.Key}";

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
                if (camera.IsMultiLens)
                {
                    // Camera → Lens → Date → Clip: each source is its own branch.
                    foreach (var lens in camera.Lenses)
                    {
                        var lensExpanded = _expandedLenses.Contains(LensKey(camera, lens)) || hasQuery;
                        var lensChildren = lensExpanded ? BuildDateClipRows(camera, lens, query, hasQuery, cameraMatches) : [];

                        if (hasQuery && lensChildren.Count == 0 && !cameraMatches)
                        {
                            continue;
                        }

                        childRows.Add(new LensRow(camera, lens, SelectLensNodeCommand)
                        {
                            IsSelected = camera.Serial == _selectedCameraSerial && lens == camera.ActiveLens,
                            IsExpanded = lensExpanded,
                            Indent = new Thickness(20, 0, 0, 0),
                        });
                        childRows.AddRange(lensChildren);
                    }
                }
                else
                {
                    // Single-sensor: dates directly under the camera.
                    childRows.AddRange(BuildDateClipRows(camera, camera.Lenses[0], query, hasQuery, cameraMatches));
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

    private List<BrowseRow> BuildDateClipRows(CameraNode camera, LensNode lens, string query, bool hasQuery, bool cameraMatches)
    {
        var rows = new List<BrowseRow>();
        foreach (var date in lens.Dates)
        {
            var dateExpanded = _expandedDates.Contains(DateKey(camera, lens, date)) || hasQuery;
            var clipRows = new List<ClipRow>();
            if (dateExpanded)
            {
                foreach (var recording in date.Recordings)
                {
                    var clip = new ClipRow(camera, lens, date, recording, SelectClipCommand)
                    {
                        IsSelected = recording == Player.ActiveRecording,
                        Indent = new Thickness(camera.IsMultiLens ? 60 : 40, 0, 0, 0),
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

            rows.Add(new DateRow(camera, lens, date, SelectDateCommand)
            {
                IsSelected = camera.Serial == _selectedCameraSerial && DateKey(camera, lens, date) == _selectedDateKey,
                IsExpanded = dateExpanded,
                Indent = new Thickness(camera.IsMultiLens ? 40 : 20, 0, 0, 0),
            });
            rows.AddRange(clipRows);
        }

        return rows;
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

    /// <summary>Selecting a lens branch: switch the active lens and expand it.</summary>
    [RelayCommand]
    private async Task SelectLensNode(LensRow row)
    {
        var key = LensKey(row.Camera, row.Node);
        if (!_expandedLenses.Remove(key))
        {
            _expandedLenses.Add(key);
        }

        _selectedCameraSerial = row.Camera.Serial;
        await SwitchToLens(row.Camera, row.Node);
        RebuildRows();
    }

    [RelayCommand]
    private async Task SelectDate(DateRow row)
    {
        var key = DateKey(row.Camera, row.Lens, row.Node);
        if (!_expandedDates.Remove(key))
        {
            _expandedDates.Add(key);
        }

        _selectedCameraSerial = row.Camera.Serial;
        _selectedDateKey = key;
        await SwitchToLens(row.Camera, row.Lens);
        RebuildRows();

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
        _selectedDateKey = DateKey(row.Camera, row.Lens, row.Date);
        await SwitchToLens(row.Camera, row.Lens);
        RebuildRows();
        await Player.SeekToRecording(row.Recording);
    }

    /// <summary>Makes the given lens active on its camera and loads it, preserving nothing here
    /// (callers seek afterwards). No-op if it is already the loaded lens.</summary>
    private async Task SwitchToLens(CameraNode camera, LensNode lens)
    {
        if (_card is null)
        {
            return;
        }

        if (camera.ActiveLens == lens && _loadedCameraSerial == camera.Serial)
        {
            return;
        }

        camera.ActiveLensIndex = Math.Max(0, camera.Lenses.ToList().IndexOf(lens));
        await LoadCameraIntoPlayer(camera, force: true);
    }

    /// <summary>Loads the camera's active lens into the player (no-op if already loaded).</summary>
    private async Task LoadCameraIntoPlayer(CameraNode camera, bool force = false)
    {
        if (_card is null || (!force && _loadedCameraSerial == camera.Serial))
        {
            return;
        }

        Player.ClearSelection();
        Player.LensLabel = camera.IsMultiLens ? camera.ActiveLens.Label : "";
        var recordings = camera.ActiveLens.Recordings.OrderBy(r => r.StartTime).ToList();
        await Player.LoadCamera(_card, camera.Name, camera.Model, recordings);
        _loadedCameraSerial = camera.Serial;
        RebuildLensBar(camera);
    }

    private void RebuildLensBar(CameraNode camera)
    {
        LensTabs.Clear();
        ShowLensBar = camera.IsMultiLens;
        if (!camera.IsMultiLens)
        {
            LensHint = "";
            return;
        }

        // Show every source the camera appears to have (1..highest recorded), so an un-recorded
        // sensor shows as a disabled placeholder rather than silently vanishing — which is what
        // made it look like a recorded source was missing.
        var recorded = camera.Lenses.Where(l => l.SourceNumber is not null)
            .ToDictionary(l => l.SourceNumber!.Value);
        var maxNumeric = recorded.Keys.DefaultIfEmpty(0).Max();

        for (var n = 1; n <= maxNumeric; n++)
        {
            if (recorded.TryGetValue(n, out var lens))
            {
                LensTabs.Add(LensTab.Recorded(lens, SelectLensCommand));
            }
            else
            {
                LensTabs.Add(LensTab.Missing(n));
            }
        }

        // Any non-numeric source tokens (rare) as plain recorded tabs after the numeric ones.
        foreach (var lens in camera.Lenses.Where(l => l.SourceNumber is null))
        {
            LensTabs.Add(LensTab.Recorded(lens, SelectLensCommand));
        }

        foreach (var tab in LensTabs)
        {
            tab.IsActive = tab.Node == camera.ActiveLens;
        }

        var recordedCount = camera.Lenses.Count;
        var missing = LensTabs.Count(t => !t.IsRecorded);
        LensHint = missing > 0
            ? $"{recordedCount} of {LensTabs.Count} sources recorded · viewed one at a time"
            : $"{recordedCount} lenses recorded · viewed one at a time";
    }

    /// <summary>Lens bar tab: switch viewpoint but keep the moment (same timestamp + play state).</summary>
    [RelayCommand]
    private async Task SelectLens(LensTab tab)
    {
        var camera = _cameras.FirstOrDefault(c => c.Serial == _selectedCameraSerial);
        if (_card is null || camera is null || tab.Node is null || camera.ActiveLens == tab.Node)
        {
            return;
        }

        var position = Player.CurrentSeconds;
        var wasPlaying = Player.IsPlaying;

        _expandedLenses.Add(LensKey(camera, tab.Node)); // reveal it in the tree too
        await SwitchToLens(camera, tab.Node);
        RebuildRows();
        await Player.SeekToSeconds(position, autoPlay: wasPlaying);
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

    /// <summary>True when FFmpeg is available; MP4 export and trimming require it.</summary>
    public bool FfmpegAvailable { get; } = Core.Export.FfmpegExporter.IsAvailable;

    [RelayCommand]
    private async Task DoExport()
    {
        if (_card is null || !Player.HasSelection)
        {
            return;
        }

        var container = ExportFormat switch
        {
            "mp4" => Core.Export.ExportContainer.Mp4,
            "mkv" => Core.Export.ExportContainer.Mkv,
            _ => (Core.Export.ExportContainer?)null,
        };

        if (container is null)
        {
            MessageBox.Show(
                "ASF export isn't available. Choose MP4 (plays almost anywhere) or MKV (original streams).",
                "Format not supported", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!FfmpegAvailable)
        {
            MessageBox.Show(
                "FFmpeg was not found, so trimmed export is unavailable. Install ffmpeg.exe (on the PATH " +
                "or in an 'ffmpeg' folder next to the app) and try again.",
                "FFmpeg required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BusyText = "Preparing export…";
        IsBusy = true;
        IReadOnlyList<ExportSlice> slices;
        try
        {
            slices = await Player.BuildExportSlices();
        }
        finally
        {
            IsBusy = false;
        }

        if (slices.Count == 0)
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

        // Export reads card chunks under the exclusive I/O lock; stop playback first so the VLC demux
        // thread isn't reading the shared device stream (and doesn't stall behind the whole export).
        Player.SuspendForExclusiveIo();

        var burnNote = IncludeStamp
            ? "\n\nNote: timestamp burn-in needs re-encoding and isn't applied to a lossless export; " +
              "Axis footage already carries a burned-in timestamp."
            : "";
        try
        {
            var fs = _card.FileSystem!;
            var extension = container == Core.Export.ExportContainer.Mp4 ? "mp4" : "mkv";
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            var index = 0;

            foreach (var slice in slices)
            {
                index++;
                var baseName = $"{slice.WallClockStart:yyyy-MM-dd_HH-mm-ss}_{SanitizeForFileName(Player.CameraName)}";
                // Never silently overwrite: disambiguate against files produced earlier in this run and
                // any pre-existing files in the destination.
                var outputPath = UniqueOutputPath(dialog.FolderName, baseName, extension, usedPaths);

                var progress = new Progress<Core.Export.FfmpegProgress>(p =>
                    BusyText = slices.Count > 1
                        ? $"Exporting clip {index}/{slices.Count} · {p.Phase} {p.Fraction:P0}"
                        : $"Exporting · {p.Phase} {p.Fraction:P0}");

                // Runs on the card's exclusive I/O queue: chunk reads + ffmpeg are serialized
                // against playback so the shared device stream is never touched concurrently.
                var result = await _card.RunExclusiveAsync(ct =>
                    Core.Export.FfmpegExporter.ExportAsync(
                        fs, slice.Chunks, slice.TrimStart, slice.TrimEnd, container.Value, outputPath, progress, ct));
                totalBytes += result.Bytes;
            }

            MessageBox.Show(
                $"Exported {slices.Count} clip{(slices.Count == 1 ? "" : "s")} " +
                $"({totalBytes / (1024.0 * 1024):F0} MB, {extension.ToUpperInvariant()}) to:\n{dialog.FolderName}\n\n" +
                "Trimmed to your marked range and stream-copied (no quality loss). The card was not modified." +
                burnNote,
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
        // Stop playback FIRST: this halts VLC's demux thread and disposes the current chunk stream while
        // the card's I/O lock and device stream are still valid. Disposing the card out from under a live
        // demux read would be a use-after-dispose on the shared stream (and throw ObjectDisposedException
        // from the stream's lock). Reached on card pull, Close, and re-open — all can happen mid-playback.
        Player.SuspendForExclusiveIo();
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

    /// <summary>Makes a camera name safe for a file name: invalid characters and spaces become '-'.</summary>
    private static string SanitizeForFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray())
            .Trim('-');
        return cleaned.Length > 0 ? cleaned : "camera";
    }

    /// <summary>Returns a destination path that collides with neither a file produced earlier in this run
    /// (<paramref name="used"/>) nor an existing file on disk, appending " (2)", " (3)", … as needed.</summary>
    private static string UniqueOutputPath(string directory, string baseName, string extension, HashSet<string> used)
    {
        var path = Path.Combine(directory, $"{baseName}.{extension}");
        var n = 2;
        while (used.Contains(path) || File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName} ({n}).{extension}");
            n++;
        }

        used.Add(path);
        return path;
    }

    public void Dispose()
    {
        Player.ActiveRecordingChanged -= OnActiveRecordingChanged;
        CloseCardInternal();
        Player.Dispose();
        _watcher.Dispose();
    }
}
