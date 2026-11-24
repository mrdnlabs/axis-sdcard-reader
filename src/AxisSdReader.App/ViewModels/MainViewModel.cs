using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Timeline;
using AxisSdReader.Core.Disk;
using AxisSdReader.Core.Ext4;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AxisSdReader.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DeviceWatcher _watcher;
    private readonly DispatcherTimer _searchDebounce;

    private OpenCard? _card;
    private bool _openInProgress; // guards against device-change re-entrancy during an open/passphrase flow
    private bool _exporting;      // an export is actively reading the card; defer card teardown until it ends
    private bool _cardRemovedDuringExport;
    private CancellationTokenSource? _exportCts; // cancels the in-flight export (Cancel button, or card teardown)
    private IReadOnlyList<CameraNode> _cameras = [];
    private readonly Dictionary<Recording, ClipRow> _clipRows = [];
    private ClipRow? _highlightedClip;
    private int _loadGeneration;  // the latest camera load; superseded loads don't commit their state
    private VolumeGuard? _pendingGuard; // an encrypted card's volume lock, held while its passphrase is asked for

    // --- app state -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaiting))]
    [NotifyPropertyChangedFor(nameof(ShowVideoSurface))]
    private bool _isCardOpen;

    /// <summary>
    /// Whether the LibVLC video surface should be shown. It is a Win32 HWND that always draws on top of
    /// WPF (airspace), so it must be hidden whenever a WPF overlay needs to appear above it (the export
    /// dialog or the boot splash) — otherwise the video punches through them.
    /// </summary>
    public bool ShowVideoSurface => IsCardOpen && Player.HasFootage && !ShowExport && !BootVisible && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoSurface))]
    private bool _isBusy;

    /// <summary>The busy overlay offers Cancel (an export is running).</summary>
    [ObservableProperty]
    private bool _canCancelBusy;

    /// <summary>Shown in the status bar when this copy runs elevated from a folder others can modify.</summary>
    public string? InstallWarning { get; } = App.InstallLocationWarning is { } w
        ? "This copy runs as administrator from a folder other programs can change. Install it under C:\\Program Files. " + w
        : null;

    public bool HasInstallWarning => InstallWarning is not null;

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
    [NotifyPropertyChangedFor(nameof(ShowVideoSurface))]
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

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        Theme.Apply(IsDarkTheme);
    }

    // --- export dialog -------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoSurface))]
    private bool _showExport;

    [ObservableProperty]
    private string _exportFormat = "mp4";

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Player.ActiveRecordingChanged += OnActiveRecordingChanged;
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.HasFootage))
            {
                OnPropertyChanged(nameof(ShowVideoSurface));
            }
        };

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildRows();
        };

        _watcher = new DeviceWatcher();
        _watcher.DiskArrived += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceArrived(); });
        _watcher.DiskRemoved += path => _dispatcher.BeginInvoke(() => { _ = OnDeviceRemoved(); });

        if (App.StartupImagePath is null)
        {
            _ = ScanAndOpenAsync();
        }
    }

    public Task OpenImageFileAsync(string path) =>
        OpenWithPassphraseFlowAsync(pass => OpenCard.FromImage(path, pass),
            $"Opening {Path.GetFileName(path)}…", Path.GetFileName(path));

    private async Task OnDeviceArrived()
    {
        // _openInProgress also blocks re-entry while a modal passphrase dialog is up: its nested message
        // loop pumps device-change events, and during the prompt IsBusy is false, so this guard is what
        // stops a second open flow / stacked dialog / disposed-card race.
        if (!_openInProgress && !IsCardOpen && !IsBusy)
        {
            // A reader that has just appeared often isn't ready to report its media yet: give it a moment.
            await Task.Delay(400);
            if (!_openInProgress && !IsCardOpen && !IsBusy)
            {
                await ScanAndOpenAsync(retryWhileNotReady: true);
            }
        }
    }

    private async Task OnDeviceRemoved()
    {
        if (_card is not { IsPhysicalDevice: true, DiskNumber: { } diskNumber } card)
        {
            // No card open, but an encrypted card's lock may be held for its passphrase prompt: a disk went away, so
            // let it go (the unlock attempt locks afresh), rather than keep a lock on a device that may be gone.
            if (_pendingGuard is not null && _pendingGuard.IsStale())
            {
                Interlocked.Exchange(ref _pendingGuard, null)?.Dispose();
            }

            return;
        }

        // Our card may be the device that was removed. Re-enumerate: if our disk number is gone, or its
        // slot now reports no media, the card is no longer present — close it and return to the waiting
        // state rather than leaving a dead session whose next read would throw.
        var stillPresent = await Task.Run(() =>
            DiskEnumerator.GetPhysicalDisks().Any(d => d.DiskNumber == diskNumber && d.SizeBytes > 0));

        // A different card may have been opened while we were checking: only close the one we checked.
        if (stillPresent || !ReferenceEquals(card, _card))
        {
            return;
        }

        CloseRemovedCard();
    }

    /// <summary>The open card is gone (device removed, or the medium pulled/swapped in its reader).</summary>
    private void CloseRemovedCard()
    {
        // The card agent access was given for is gone: end that at once, even if an export defers the rest of the
        // teardown (it may sit in a folder dialog indefinitely, and another card could go into the same reader).
        StopAgentHost();

        if (_exporting)
        {
            // An export is actively reading the card; don't dispose its streams out from under the running
            // FFmpeg read. DoExport closes the card once the export unwinds.
            _cardRemovedDuringExport = true;
            _exportCts?.Cancel();
            return;
        }

        CloseCardInternal();
        WaitingMessage = "The card was removed. Insert an Axis camera SD card to begin.";
    }

    [RelayCommand]
    private async Task ScanAndOpen()
    {
        await ScanAndOpenAsync();
    }

    private async Task ScanAndOpenAsync(bool retryWhileNotReady = false)
    {
        if (IsBusy)
        {
            return;
        }

        if (!App.IsElevated)
        {
            // Without administrator rights a card can be neither read nor locked read-only; images still open.
            WaitingMessage = "Reading SD cards needs administrator rights: start the app with AxisSdReader.exe and accept " +
                             "the prompt.\nCard images open without them.";
            BootVisible = false;
            return;
        }

        IsBusy = true;
        BusyText = "Scanning for Axis cards…";
        WaitingMessage = "Scanning for Axis camera cards…";
        _bootSkipped = false;
        SetBootStage("Detecting SD card…", 0.08, "scanning USB readers");
        try
        {
            // Prefer a readable Axis card; fall back to an encrypted card (which we can unlock after a
            // passphrase prompt). Right after a reader arrives its slot may not report media yet, so retry a
            // few times while some USB disk is still "not ready".
            (PhysicalDiskInfo? Disk, AxisCardProbeResult Probe) picked = default;
            for (var attempt = 0; ; attempt++)
            {
                var (result, anyNotReady) = await Task.Run(() =>
                {
                    var usb = DiskEnumerator.GetPhysicalDisks().Where(d => d.IsUsb).ToList();
                    var found = usb
                        .Where(d => d.SizeBytes > 0)
                        .Select(d => (Disk: (PhysicalDiskInfo?)d, Probe: AxisCardDetector.Probe(d.DiskNumber)))
                        .ToList();
                    var plain = found.FirstOrDefault(x => x.Probe.IsLikelyAxisCard);
                    var choice = plain.Disk is not null ? plain : found.FirstOrDefault(x => x.Probe.IsEncrypted);
                    return (choice, usb.Any(d => d.SizeBytes is null or 0));
                });

                picked = result;
                if (picked.Disk is not null || !retryWhileNotReady || !anyNotReady || attempt >= 3)
                {
                    break;
                }

                await Task.Delay(1000);
            }

            if (picked.Disk is null)
            {
                WaitingMessage = "No Axis camera card detected.\nInsert a card, or open a card image.";
                BootVisible = false;
                return;
            }

            var disk = picked.Disk!;
            SetBootStage("Detecting SD card…", 0.15,
                $"{disk.FriendlyName} · {(picked.Probe.IsEncrypted ? "encrypted" : "ext4")}");

            await OpenWithPassphraseFlowAsync(
                pass => OpenCard.FromDevice(disk.DiskNumber, disk.FriendlyName, disk.SizeBytes, pass,
                    Interlocked.Exchange(ref _pendingGuard, null)),
                "Opening card…", disk.FriendlyName);
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

        // The dialog's message loop still runs queued work (an agent's "show", say): mark the user as busy meanwhile.
        bool? chosen;
        _dialogOpen = true;
        try
        {
            chosen = dialog.ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }

        if (chosen == true)
        {
            await OpenImageFileAsync(dialog.FileName);
        }
    }

    private bool _dialogOpen;

    /// <summary>
    /// Opens a card, prompting for a passphrase and retrying if it turns out to be LUKS-encrypted.
    /// <paramref name="factory"/> builds an <see cref="OpenCard"/> from an optional passphrase.
    /// </summary>
    private async Task OpenWithPassphraseFlowAsync(Func<char[]?, OpenCard> factory, string busyText, string cardName)
    {
        _openInProgress = true;
        try
        {
            var status = await OpenCardAsync(() => factory(null), busyText);
            if (status != CardOpenStatus.EncryptedNeedsPassphrase)
            {
                return; // opened, or failed for a non-passphrase reason (already surfaced)
            }

            BootVisible = false;
            IsBusy = false;
            BusyText = "";

            string? error = null;
            while (true)
            {
                var passphrase = PassphraseDialog.Prompt(System.Windows.Application.Current.MainWindow, cardName, error);
                if (passphrase is null || passphrase.Length == 0)
                {
                    WaitingMessage = "This card is encrypted. Enter the SD card passphrase to unlock it.";
                    return;
                }

                CardOpenStatus? retry;
                try
                {
                    retry = await OpenCardAsync(() => factory(passphrase), "Unlocking card…");
                }
                finally
                {
                    // Zero our copy of the secret as soon as the unlock attempt is done. (The dialog reads it from the
                    // PasswordBox's SecureString and clears the box, so no immutable string copy is ever made.)
                    Array.Clear(passphrase);
                }

                if (retry == CardOpenStatus.IncorrectPassphrase)
                {
                    error = "That passphrase didn't unlock the card. Try again.";
                    BootVisible = false;
                    IsBusy = false;
                    BusyText = "";
                    continue;
                }

                return; // unlocked, or a different terminal failure (already surfaced)
            }
        }
        finally
        {
            _openInProgress = false;

            // Unlocked (the session adopted the lock), cancelled, or failed: nothing is held for a prompt any more.
            Interlocked.Exchange(ref _pendingGuard, null)?.Dispose();
        }
    }

    private async Task<CardOpenStatus?> OpenCardAsync(Func<OpenCard> open, string busyText)
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
                var status = card.Status;
                var detail = card.FailureDetail;

                // An encrypted card stays locked while its passphrase is asked for: keep the lock for the next try.
                if (status is CardOpenStatus.EncryptedNeedsPassphrase or CardOpenStatus.IncorrectPassphrase)
                {
                    var guard = card.DetachGuard();
                    guard?.KeepTrying(TimeSpan.FromSeconds(2)); // nothing else watches the card while the prompt is up
                    Interlocked.Exchange(ref _pendingGuard, guard)?.Dispose();
                }

                card.Dispose();

                // The passphrase flow handles these — leave the UI for it to drive.
                if (status is CardOpenStatus.EncryptedNeedsPassphrase or CardOpenStatus.IncorrectPassphrase)
                {
                    return status;
                }

                WaitingMessage = status switch
                {
                    CardOpenStatus.Encrypted => detail ?? "This card is encrypted in a format this app can't read.",
                    CardOpenStatus.IncompatibleExt4 => $"The card's filesystem uses unsupported features:\n{detail}",
                    _ => detail ?? "No readable Axis recordings were found on this card.",
                };
                BootVisible = false;
                return status;
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

            // Watch the card: a medium pulled or swapped in a reader that stays plugged in raises no device
            // event, and a volume Windows creates late must be locked too.
            card.MediaLost += () => _dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(card, _card))
                {
                    CloseRemovedCard();
                }
            });
            card.ProtectionChanged += () => _dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(card, _card) && card.Index is { } current)
                {
                    ApplyCardIdentity(card, current);
                }
            });
            card.StartMonitoring();
            StartAgentServiceIfEnabled();

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
            return CardOpenStatus.Ok;
        }
        catch (OperationCanceledException)
        {
            // The card was closed/removed while opening/indexing; the teardown path drives the UI message.
            BootVisible = false;
            if (!IsCardOpen)
            {
                CloseCardInternal();
            }

            return null;
        }
        catch (Exception ex)
        {
            // Don't leave a half-opened card holding the device handle and volume locks behind the waiting
            // screen. (Once the card is showing, a failure here — e.g. the first seek — just leaves it open.)
            if (!IsCardOpen)
            {
                CloseCardInternal();
            }

            WaitingMessage = ex.Message;
            BootVisible = false;
            App.Log("Open", ex);
            return null;
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
        CardSubtitle = index.UnreadableEntries > 0
            ? $"{RecCountLabel} · {index.UnreadableEntries} unreadable folder{(index.UnreadableEntries == 1 ? "" : "s")} skipped"
            : $"{RecCountLabel} · Axis edge storage";
        CamCountLabel = $"{index.Cameras.Count} camera{(index.Cameras.Count == 1 ? "" : "s")}";

        if (index.Recordings.Count > 0)
        {
            // Span covers recording ENDS too: an overnight recording extends the last day. SafeEnd
            // saturates the date math so one crafted/corrupt recording can't overflow DateTime and abort
            // the entire card open (the per-clip labels use the same helper).
            var first = index.Recordings.Min(r => LocalDate(r));
            var last = index.Recordings
                .Max(r => TimeSegment.SafeEnd(LocalStart(r), TimeSegment.EstimateDuration(r)).Date);
            FootageSpanLabel = first == last
                ? first.ToString("MMM d, yyyy")
                : $"{first:MMM d} – {last:MMM d, yyyy}";
        }
        else
        {
            FootageSpanLabel = "No recordings";
        }

        var label = card.VolumeLabel is { Length: > 0 } vl ? vl : "Axis";
        var fsDesc = card.IsEncrypted ? "encrypted ext4" : "ext4";

        // Reflect the ACTUAL protection outcome, never a hardcoded reassurance. An image file needs no
        // protection; a physical card is "protected" only when every volume was locked.
        IsCardProtected = !card.IsPhysicalDevice || card.FullyProtected;
        if (!card.IsPhysicalDevice)
        {
            MountLabel = $"Image · {fsDesc} · {label} volume";
            ProtectionHeadline = card.IsEncrypted ? "Decrypted read-only image" : "Read-only image";
            ProtectionDetail = "Opened from a file — the source is never modified.";
        }
        else if (card.FullyProtected)
        {
            MountLabel = $"Locked read-only · {fsDesc} · {label} volume";
            ProtectionHeadline = card.IsEncrypted ? "Unlocked · Read-only" : "Card locked · Read-only";
            ProtectionDetail = card.IsEncrypted
                ? "Decrypted in memory with your passphrase; the card is never modified."
                : "Footage cannot be changed or deleted.";
        }
        else
        {
            MountLabel = $"NOT fully locked · {fsDesc} · {label} volume";
            ProtectionHeadline = "Card not fully locked";
            ProtectionDetail = "Windows still has access — do not click any \"format disk\" prompt.";
        }

        RefreshAgentCardInfo();
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
                    // Camera → Lens → Date → Clip: each source is its own branch (at most 64: a card can't make the tree
                    // build hundreds of thousands of rows on the UI thread).
                    foreach (var lens in camera.Lenses.Take(64))
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

    // Debounced: every keystroke rebuilding the whole tree (and re-deriving every clip's labels) lags on big cards.
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

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

        var generation = ++_loadGeneration;
        Player.ClearSelection();
        Player.LensLabel = camera.IsMultiLens ? camera.ActiveLens.Label : "";
        var recordings = camera.ActiveLens.Recordings.OrderBy(r => r.StartTime).ToList();
        try
        {
            await Player.LoadCamera(_card, camera.Name, camera.Model, recordings);
        }
        finally
        {
            // Commit even when the initial seek failed (say, an unreadable newest chunk) so the tree keeps
            // working — but only for the latest load: a slower, superseded one must not overwrite it.
            if (generation == _loadGeneration)
            {
                _loadedCameraSerial = camera.Serial;
                RebuildLensBar(camera);
            }
        }
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
        // Source numbers come from the card, so: duplicates ("1" and "01") keep the first, and placeholders are
        // drawn only for a plausible sensor count — a corrupt token such as 2147483647 must not make the UI
        // thread build billions of tabs.
        const int MaxPlaceholderSources = 16;
        const int MaxLensTabs = 64; // a card can't make the lens bar unusable, or slow to build
        var recorded = new Dictionary<int, LensNode>();
        foreach (var lens in camera.Lenses.Where(l => l.SourceNumber is >= 0))
        {
            recorded.TryAdd(lens.SourceNumber!.Value, lens);
        }

        var maxNumeric = recorded.Keys.DefaultIfEmpty(0).Max();
        if (maxNumeric <= MaxPlaceholderSources)
        {
            for (var n = recorded.ContainsKey(0) ? 0 : 1; n <= maxNumeric; n++)
            {
                LensTabs.Add(recorded.TryGetValue(n, out var lens)
                    ? LensTab.Recorded(lens, SelectLensCommand)
                    : LensTab.Missing(n));
            }
        }
        else
        {
            foreach (var lens in recorded.OrderBy(kv => kv.Key).Select(kv => kv.Value).Take(MaxLensTabs))
            {
                LensTabs.Add(LensTab.Recorded(lens, SelectLensCommand));
            }
        }

        // Anything not shown yet (non-numeric tokens, duplicate numbers) as plain recorded tabs at the end.
        var shown = LensTabs.Select(t => t.Node).OfType<LensNode>().ToHashSet();
        foreach (var lens in camera.Lenses.Where(l => !shown.Contains(l)).Take(Math.Max(0, MaxLensTabs - LensTabs.Count)))
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
    private void CloseExport()
    {
        ShowExport = false;
        _exportCts?.Cancel(); // Cancel while the range is still being prepared abandons the export too
    }

    /// <summary>The busy overlay's Cancel button: stops the running export (FFmpeg is killed, the partial
    /// file removed).</summary>
    [RelayCommand]
    private void CancelBusy() => _exportCts?.Cancel();

    [RelayCommand]
    private void SetExportFormat(string format) => ExportFormat = format;

    /// <summary>True when FFmpeg is available; MP4 export and trimming require it.</summary>
    public bool FfmpegAvailable { get; } = Core.Export.FfmpegExporter.IsAvailable;

    [RelayCommand]
    private async Task DoExport()
    {
        if (_card is null || !Player.HasSelection)
        {
            return;
        }

        var container = ExportFormat == "mkv"
            ? Core.Export.ExportContainer.Mkv
            : Core.Export.ExportContainer.Mp4;

        if (!FfmpegAvailable)
        {
            MessageBox.Show(
                "FFmpeg was not found, so trimmed export is unavailable. Place ffmpeg.exe in an 'ffmpeg' " +
                "folder next to the app and try again. (When running as administrator, the PATH is not " +
                "searched, for security.)",
                "FFmpeg required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_exporting)
        {
            return;
        }

        // _exporting makes card removal DEFER teardown while the export is reading the card, so the shared
        // device/decrypt streams are never disposed out from under an in-flight FFmpeg read; the card is closed
        // here once the export has unwound. _exportCts is cancelled by the Cancel buttons and by teardown.
        var card = _card;
        _exporting = true;
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        try
        {
            BusyText = "Preparing export…";
            IsBusy = true;
            CanCancelBusy = true;
            IReadOnlyList<ExportSlice> slices;
            try
            {
                slices = await Player.BuildExportSlices();
            }
            catch (OperationCanceledException)
            {
                return; // the card was closed while the range was being prepared
            }
            finally
            {
                IsBusy = false;
                CanCancelBusy = false;
            }

            if (token.IsCancellationRequested || !ReferenceEquals(card, _card) || _cardRemovedDuringExport)
            {
                return; // cancelled, or the card went away, while the range was prepared
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

            if (!ReferenceEquals(card, _card) || _cardRemovedDuringExport || token.IsCancellationRequested)
            {
                return; // the card was removed while the folder dialog was open
            }

            // This process is elevated but the folder belongs to the user: resolve it now, then hold that very
            // folder open and create every file relative to it (GuardedFile), so nothing can redirect the writes.
            string folder;
            GuardedFolder exportFolder;
            try
            {
                folder = GuardedFile.ResolveDirectory(dialog.FolderName);

                // A network server decides where an elevated process's files really go there (it can redirect them,
                // even back to this machine), so exports stay on this computer.
                if (folder.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    MessageBox.Show(
                        $"{folder}\n\nExports can't be saved straight to a network folder: this app runs as administrator, " +
                        "and the server could send the files somewhere else. Export to a folder on this computer, such as " +
                        "Videos or Documents, and copy them from there.",
                        "Choose another folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                exportFolder = GuardedFile.OpenFolder(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using var heldFolder = exportFolder;

            // Write only where the user could write without elevation: an administrator's rights must not let
            // an export land in a protected folder (a swapped-in link to C:\Windows, say).
            if (!GuardedFile.IsUserWritable(exportFolder))
            {
                MessageBox.Show(
                    $"{folder}\n\nThis folder is protected (your account can't save files there without administrator " +
                    "rights), so the export won't be written there. Choose a folder of your own, such as Videos or Documents.",
                    "Choose another folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowExport = false;
            IsBusy = true;
            CanCancelBusy = true;

            // FFmpeg streams the chunks off the card through the loopback server; stop playback so the VLC demux
            // thread isn't competing with it for the card.
            Player.SuspendForExclusiveIo();

            try
            {
                var extension = container == Core.Export.ExportContainer.Mp4 ? "mp4" : "mkv";
                long totalBytes = 0;
                var index = 0;

                // This export's own loopback server: it exists only while FFmpeg is reading, so nothing is
                // listening on the machine between exports.
                using var server = LocalMediaServer.Start();

                foreach (var slice in slices)
                {
                    index++;
                    token.ThrowIfCancellationRequested();

                    // Tag the file with the recording type. A range covered by BOTH continuous and motion
                    // exports two clips with the same start time and camera, so without the type they would
                    // differ only by a "(2)" suffix and be impossible to tell apart. Invariant digits/calendar.
                    var baseName = slice.WallClockStart.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) +
                                   $"_{SanitizeForFileName(Player.CameraName)}_{KindLabel(slice.Recording.Kind)}";

                    var progress = new Progress<Core.Export.FfmpegProgress>(p =>
                        BusyText = slices.Count > 1
                            ? $"Exporting clip {index}/{slices.Count} · {p.Phase} {p.Fraction:P0}"
                            : $"Exporting · {p.Phase} {p.Fraction:P0}");

                    // What this file may take: no more than its footage could hold, and never the last of the drive.
                    var limits = Core.Export.ExportLimits.For(slice.TrimEnd - slice.TrimStart, Core.Axis.RecordingChunk.TotalBytes(slice.Chunks), folder)
                        ?? throw new IOException($"There isn't enough free space on the drive holding {folder} for this export (or it couldn't be checked).");

                    // Never overwrite: each file is created new ("name (2)", … if taken) - last, once nothing else can fail
                    // before FFmpeg runs and the catch below cleans up.
                    using var lease = server.CreateLease(card!.MediaSources(slice.Chunks));
                    using var target = CreateExportTarget(exportFolder, baseName, extension);
                    try
                    {
                        var result = await Core.Export.FfmpegExporter.ExportAsync(
                            lease.Urls, slice.TrimStart, slice.TrimEnd, container, target.Path, limits, progress, token);
                        if (target.Length == 0)
                        {
                            throw new IOException("The export file was not written.");
                        }

                        totalBytes += result.Bytes;
                    }
                    catch
                    {
                        target.DeleteIfUnchanged();
                        throw;
                    }
                }

                MessageBox.Show(
                    $"Exported {slices.Count} clip{(slices.Count == 1 ? "" : "s")} ({totalBytes / (1024.0 * 1024):F0} MB) to:\n{folder}",
                    "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                if (!_cardRemovedDuringExport && ReferenceEquals(card, _card))
                {
                    MessageBox.Show("The export was cancelled.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                App.Log("Export", ex);
                MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                CanCancelBusy = false;
                BusyText = "";
            }
        }
        finally
        {
            _exporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
            if (_cardRemovedDuringExport)
            {
                _cardRemovedDuringExport = false;
                CloseCardInternal();
                WaitingMessage = "The card was removed. Insert an Axis camera SD card to begin.";
            }
        }
    }

    /// <summary>Creates the next free "name.ext", "name (2).ext", … in the held folder, exclusively.</summary>
    private static GuardedOutputFile CreateExportTarget(GuardedFolder folder, string baseName, string extension)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{baseName}.{extension}" : $"{baseName} ({n}).{extension}";
            try
            {
                return folder.CreateExclusive(name);
            }
            catch (GuardedFileException ex) when (ex.IsAlreadyExists && n < 1000)
            {
                // taken — try the next number
            }
        }
    }

    [RelayCommand]
    private void CloseCard() => CloseCardInternal();

    private void CloseCardInternal()
    {
        // Cancel any in-flight export first so its FFmpeg read releases the card's I/O lock promptly; the
        // card's Dispose then waits briefly on that lock before tearing down the shared streams. This makes
        // teardown safe on EVERY path (window close, shutdown, Close) — not just device removal.
        _exportCts?.Cancel();

        // Detach the player: this halts VLC's demux thread and disposes the current chunk stream while the
        // card's I/O lock and device stream are still valid, and forgets the card so no later seek/callback
        // dereferences it. Disposing the card out from under a live read would be a use-after-dispose on the
        // shared stream. Reached on card pull, Close, and re-open — all mid-play.
        // Agent consent covers the card it was given for: closing that card switches agent access off again (a
        // different card needs fresh consent) and revokes every media URL handed out, before the card goes away.
        // Access switched on while no card was open carries over to the card being opened.
        if (_card is not null)
        {
            StopAgentHost();
        }

        Player.DetachCard();
        Player.ClearSelection();
        Rows.Clear();
        _clipRows.Clear();
        _highlightedClip = null;
        LensTabs.Clear();
        ShowLensBar = false;
        LensHint = "";
        _cameras = [];
        _loadedCameraSerial = null;
        _loadGeneration++;
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

    /// <summary>Short, file-safe name for a recording type, matching the timeline legend.</summary>
    private static string KindLabel(RecordingKind kind) => kind switch
    {
        RecordingKind.Continuous => "continuous",
        RecordingKind.Event => "motion",
        RecordingKind.Manual => "manual",
        RecordingKind.Scheduled => "scheduled",
        _ => "recording",
    };

    /// <summary>Makes a camera name safe for a file name: invalid characters and spaces become '-'.</summary>
    private static string SanitizeForFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray())
            .Trim('-');
        return cleaned.Length > 0 ? cleaned : "camera";
    }

    public void Dispose()
    {
        Player.ActiveRecordingChanged -= OnActiveRecordingChanged;
        _searchDebounce.Stop();
        StopAgentHost();
        CloseCardInternal();
        Player.Dispose();
        _watcher.Dispose();
        Interlocked.Exchange(ref _pendingGuard, null)?.Dispose();
    }
}
