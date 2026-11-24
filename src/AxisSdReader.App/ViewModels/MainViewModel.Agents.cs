using System.IO;
using System.Windows;
using System.Windows.Threading;
using AxisSdReader.App.Services;
using AxisSdReader.Core.Agent;
using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxisSdReader.App.ViewModels;

/// <summary>
/// Agent access: lets an AI agent (through the AxisSdReader.Mcp bridge) look at the open card — list
/// recordings, see frames and contact sheets, export clips, and move the app's playhead. Off by default,
/// never remembered between runs, visibly indicated while on, and switched off again when the card closes
/// (a different card needs fresh consent).
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly Dictionary<string, string> OpLabels = new(StringComparer.Ordinal)
    {
        ["status"] = "status",
        ["cameras"] = "cameras",
        ["recordings"] = "recordings",
        ["coverage"] = "coverage",
        ["locate"] = "frames",
        ["slices"] = "clip",
        ["view"] = "view",
        ["show"] = "show",
    };

    private AgentHost? _agentHost;
    private volatile AgentService? _agentService;
    private LocalMediaServer? _agentMedia;
    private volatile AgentCardInfo? _agentCardInfo; // snapshot, refreshed on the UI thread
    private volatile bool _agentOn;                 // AgentAccessEnabled, readable from pool threads
    private DispatcherTimer? _agentPulse;
    private volatile string _lastAgentOp = "";
    private int _agentActivityPosted;               // at most one activity update queued for the UI at a time
    private long _lastShowTicks = long.MinValue / 2; // "show" moves the player: at most one a second (no overflow in now - this)

    /// <summary>The longest export range an agent may mark in the app.</summary>
    private static readonly TimeSpan MaxAgentSelection = TimeSpan.FromHours(2);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentLabel))]
    [NotifyPropertyChangedFor(nameof(AgentTooltip))]
    private bool _agentAccessEnabled;

    /// <summary>True for a few seconds after an agent request, to make agent activity visible.</summary>
    [ObservableProperty]
    private bool _agentActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentTooltip))]
    private string _agentDetail = "";

    public string AgentLabel => AgentAccessEnabled ? "AI agents: on" : "AI agents: off";

    public string AgentTooltip => AgentAccessEnabled
        ? "AI agents can see this card's recordings, frames and clips through the Axis SD Reader MCP server. " +
          "Any program running as you can connect while this is on. Click to turn off; it also turns off when the card closes." +
          (AgentDetail.Length > 0 ? $"\n{AgentDetail}" : "")
        : "Let AI agents (via the Axis SD Reader MCP server) view this card's recordings, frames and clips. " +
          "Off by default; turns off again when the card or the app closes.";

    partial void OnAgentAccessEnabledChanged(bool value) => _agentOn = value;

    [RelayCommand]
    private void ToggleAgentAccess()
    {
        if (AgentAccessEnabled)
        {
            StopAgentHost();
            return;
        }

        // Consent is given for a card the user is looking at: never with no card open (it would attach to whatever
        // card opens next, perhaps one inserted later), and not for one already pulled out while an export unwinds.
        if (_card is null || _cardRemovedDuringExport)
        {
            MessageBox.Show("Open a card first: agent access is given for the card that is open, and ends when it closes.",
                "AI agents", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _agentHost = AgentHost.Start(HandleAgentRequestAsync);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            MessageBox.Show(
                "Agent access couldn't start, because another program is already using its connection. " +
                "Close any other copy of the app and try again.\n\n" + ex.Message,
                "AI agents", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _agentHost.RequestHandled += op =>
        {
            // Coalesced, below input and rendering: however fast requests come, the UI has one update queued.
            _lastAgentOp = op;
            if (Interlocked.Exchange(ref _agentActivityPosted, 1) == 0)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    Interlocked.Exchange(ref _agentActivityPosted, 0);
                    OnAgentActivity(_lastAgentOp);
                });
            }
        };
        AgentAccessEnabled = true;
        AgentDetail = "Waiting for an agent to connect.";
        StartAgentServiceIfEnabled();
    }

    private void StopAgentHost()
    {
        _agentOn = false;
        _agentHost?.Dispose();
        _agentHost = null;
        StopAgentService();
        _agentPulse?.Stop();
        AgentAccessEnabled = false;
        AgentActive = false;
        AgentDetail = "";
    }

    /// <summary>Creates the card-query service (with its own loopback media server) when agent access is on.</summary>
    private void StartAgentServiceIfEnabled()
    {
        if (!AgentAccessEnabled || _agentService is not null || _card is not { Index: { } index } card)
        {
            return;
        }

        _agentCardInfo = DescribeCard(card, index);
        _agentMedia = LocalMediaServer.Start(maxConnections: 12); // contact sheets read several chunks at once
        _agentService = new AgentService(new CardCatalog(index),
            () => _agentCardInfo ?? throw new AgentException("No card is open."),
            (recordings, ct) => card.LoadMetadataAsync(recordings, ct), card.OpenChunk, _agentMedia, "app");
    }

    /// <summary>Drops the card-query service, revokes every media URL it handed out, and stops its server.</summary>
    private void StopAgentService()
    {
        var service = _agentService;
        _agentService = null;
        service?.Dispose();
        _agentMedia?.Dispose();
        _agentMedia = null;
        _agentCardInfo = null;
    }

    /// <summary>Re-describes the open card to agents (its protection state can change while it is open).</summary>
    private void RefreshAgentCardInfo()
    {
        if (_agentService is not null && _card is { Index: { } index } card)
        {
            _agentCardInfo = DescribeCard(card, index);
        }
    }

    private void OnAgentActivity(string op)
    {
        if (!AgentAccessEnabled)
        {
            return; // a request that was still unwinding when access was switched off
        }

        AgentActive = true;
        AgentDetail = $"Last agent request: {(OpLabels.TryGetValue(op, out var label) ? label : "request")} at {DateTime.Now:HH:mm:ss}.";
        _agentPulse ??= CreatePulseTimer();
        _agentPulse.Stop();
        _agentPulse.Start();
    }

    private DispatcherTimer CreatePulseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AgentActive = false;
        };
        return timer;
    }

    /// <summary>
    /// Requests arrive on a pool thread. Card queries are answered right there (the service has its own catalog,
    /// card I/O goes through the card's gate), so an agent can't load the UI thread; only what reads or changes
    /// the UI — the current view, and navigation — is marshalled, at Background priority, below input and
    /// rendering.
    /// </summary>
    private async Task<AgentResponse> HandleAgentRequestAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        if (!_agentOn)
        {
            return AgentResponse.Failure("Agent access is off in the app.");
        }

        switch (request.Op)
        {
            case "view":
                return AgentResponse.Success(await OnUi(CurrentView, cancellationToken));

            case "show":
                return await OnUi(() => ShowForAgentAsync(request, cancellationToken), cancellationToken).Unwrap();

            case "status":
                var view = await OnUi(CurrentView, cancellationToken);
                if (_agentService is not { } statusService)
                {
                    return AgentResponse.Success(new AgentStatus("app", false, null, view,
                        "No card is open in the app. Insert an Axis camera SD card or open a card image."));
                }

                var reply = await statusService.HandleAsync(request, cancellationToken);
                return reply.Ok ? AgentResponse.Success(reply.Read<AgentStatus>() with { View = view }) : reply;

            default:
                return _agentService is { } service
                    ? await service.HandleAsync(request, cancellationToken)
                    : AgentResponse.Failure("No card is open in the app. Insert an Axis camera SD card or open a card image first.");
        }
    }

    /// <summary>Runs on the UI thread below input and rendering; a call still queued when the request is
    /// cancelled (timeout, or access switched off) never runs.</summary>
    private Task<T> OnUi<T>(Func<T> work, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(work, DispatcherPriority.Background, cancellationToken).Task;

    /// <summary>Moves the app to what an agent wants the user to look at: camera, source, time, and optionally
    /// an export range. Navigation only — never opens dialogs, plays, or exports — and never while the user is in
    /// the middle of something (an open export dialog, a passphrase prompt, an export).</summary>
    private async Task<AgentResponse> ShowForAgentAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        if (_agentService is not { } service || _card is not { } card)
        {
            return AgentResponse.Failure("No card is open in the app.");
        }

        if (UserIsBusy())
        {
            return AgentResponse.Failure("The app is busy right now (or the user has a dialog open); try again in a moment.");
        }

        // Each "show" makes the player open and decode the chunk asked for: one a second is plenty to point at things.
        var now = Environment.TickCount64;
        if (now - _lastShowTicks < 1000)
        {
            return AgentResponse.Failure("The app was just moved; wait a second before showing something else.");
        }

        _lastShowTicks = now;

        if (request.Time is not { } time)
        {
            return AgentResponse.Failure("Give the time to show.");
        }

        var target = service.Catalog.FindCamera(request.Camera ?? _loadedCameraSerial, out var error);
        var camera = target is null ? null : _cameras.FirstOrDefault(c => c.Serial == target.Serial);
        if (camera is null)
        {
            return AgentResponse.Failure(error ?? "Unknown camera.");
        }

        var lens = request.Source is { Length: > 0 } source
            ? camera.Lenses.FirstOrDefault(l => l.SourceToken.Equals(source.Trim(), StringComparison.OrdinalIgnoreCase))
            : camera.ActiveLens;
        if (lens is null)
        {
            return AgentResponse.Failure($"{camera.Name} has no source '{AgentService.Clean(request.Source, 24)}'.");
        }

        _selectedCameraSerial = camera.Serial;
        _expandedCameras.Add(camera.Serial);
        await SwitchToLens(camera, lens);
        if (!StillMine())
        {
            return AgentResponse.Failure("Agent access was turned off.");
        }

        RebuildRows();
        await Player.SeekToSeconds(AgentService.Seconds(time), autoPlay: false);
        if (!StillMine())
        {
            return AgentResponse.Failure("Agent access was turned off.");
        }

        if (request.SelectFrom is { } from && request.SelectTo is { } to && to > from)
        {
            // Clamped, not dropped: the user sees the start of what the agent marked, never an export range of days.
            var end = to - from > MaxAgentSelection ? from + MaxAgentSelection : to;
            Player.SetSelection(AgentService.Seconds(from), AgentService.Seconds(end));
        }

        return AgentResponse.Success(CurrentView());

        // The user may have switched agents off, closed the card, or started something meanwhile.
        bool StillMine() => _agentOn && ReferenceEquals(card, _card) && !UserIsBusy() && !cancellationToken.IsCancellationRequested;
    }

    /// <summary>The user is in the middle of something an agent must not disturb.</summary>
    private bool UserIsBusy() => IsBusy || _exporting || BootVisible || ShowExport || _openInProgress || _dialogOpen;

    private AgentView CurrentView()
    {
        var camera = _cameras.FirstOrDefault(c => c.Serial == _loadedCameraSerial);
        var recording = Player.ActiveRecording;
        return new AgentView(
            camera?.Serial,
            camera?.Name,
            AgentService.Clean(camera?.ActiveLens.SourceToken),
            IsCardOpen ? AgentService.At(Player.CurrentSeconds) : null,
            Player.IsPlaying,
            recording?.Id.Raw,
            recording is null ? null : AgentService.KindName(recording.Kind),
            Player.SelInSeconds is { } i ? AgentService.At(i) : null,
            Player.SelOutSeconds is { } o ? AgentService.At(o) : null);
    }

    private AgentCardInfo DescribeCard(OpenCard card, AxisCard index)
    {
        DateTimeOffset? first = null, last = null;
        if (index.Recordings.Count > 0)
        {
            first = AgentService.At(TimeAxis.ToSeconds(index.Recordings.Min(r => r.StartTime)));
            last = AgentService.At(TimeAxis.ToSeconds(index.Recordings.Max(r => r.StartTime)));
        }

        return new AgentCardInfo(
            AgentService.Clean(card.Description) ?? "card",
            card.IsPhysicalDevice ? "SD card" : "card image",
            card.IsEncrypted,
            IsCardProtected,
            $"{ProtectionHeadline}. {ProtectionDetail}",
            index.Recordings.Count,
            index.Cameras.Count,
            first,
            last,
            index.UnreadableEntries);
    }
}
