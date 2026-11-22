using System.Globalization;
using HeatBridge.Core.Devices;
using HeatBridge.Core.Services;
using Microsoft.AspNetCore.Components;

namespace HeatBridge.Web.Components.Devices;

/// <summary>
/// Dashboard card for one climate device.
/// <para>
/// User input is kept in local "pending" state (<see cref="_pendingTarget"/>, <see cref="_pendingMode"/>)
/// which takes precedence over incoming snapshots, so background refreshes never overwrite what the user
/// is doing. Repeated +/- presses are debounced into a single command.
/// </para>
/// </summary>
public sealed partial class ThermostatCard : IDisposable
{
    private const double DialRadius = 96;
    private static readonly double Circumference = 2 * Math.PI * DialRadius;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SuccessVisibleFor = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ErrorVisibleFor = TimeSpan.FromSeconds(12);

    private static readonly ClimateMode[] ModeOrder =
    [
        ClimateMode.Off, ClimateMode.Heat, ClimateMode.Cool, ClimateMode.HeatCool,
        ClimateMode.Auto, ClimateMode.FanOnly, ClimateMode.Dry,
    ];

    private DeviceSnapshot _snapshot = default!;
    private double? _pendingTarget;
    private ClimateMode? _pendingMode;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _feedbackTimer;
    private Feedback? _feedback;
    private bool _disposed;

    private enum FeedbackKind
    {
        Pending,
        Sending,
        Success,
        Error,
    }

    [Parameter, EditorRequired]
    public IDeviceSession Session { get; set; } = default!;

    private ClimateState? Climate => _snapshot.Climate;

    private bool CanControl => _snapshot.IsOnline && Climate is not null;

    private double? DisplayedTarget => _pendingTarget ?? Climate?.TargetTemperature;

    private ClimateMode DisplayedMode => _pendingMode ?? Climate?.Mode ?? ClimateMode.Unknown;

    private bool IsTargetPending => _pendingTarget is not null;

    private bool CanIncrease => CanControl && DisplayedTarget is { } t && Climate!.Limits.CanIncrease(t);

    private bool CanDecrease => CanControl && DisplayedTarget is { } t && Climate!.Limits.CanDecrease(t);

    private List<ClimateMode> AvailableModes => ModeOrder.Where(_snapshot.Capabilities.Supports).ToList();

    private string StatusKey => _snapshot.Status switch
    {
        ConnectionStatus.Online => "online",
        ConnectionStatus.Offline => "offline",
        _ => "connecting",
    };

    private string StatusLabel => _snapshot.Status switch
    {
        ConnectionStatus.Online => "Online",
        ConnectionStatus.Offline => "Offline",
        _ => "Ansluter…",
    };

    private double TargetFraction
    {
        get
        {
            if (Climate is not { } climate || DisplayedTarget is not { } target)
            {
                return 0;
            }

            var span = climate.Limits.Maximum - climate.Limits.Minimum;
            return span <= 0 ? 1 : Math.Clamp((target - climate.Limits.Minimum) / span, 0, 1);
        }
    }

    private string KnobX => Format(120 + (DialRadius * Math.Cos(KnobAngle)));

    private string KnobY => Format(120 + (DialRadius * Math.Sin(KnobAngle)));

    // The arc starts at 135° (bottom left) and sweeps 270° clockwise.
    private double KnobAngle => (135 + (270 * TargetFraction)) * Math.PI / 180;

    public void Dispose()
    {
        _disposed = true;
        Session.SnapshotChanged -= OnSnapshotChanged;
        CancelAndDispose(ref _debounce);
        CancelAndDispose(ref _feedbackTimer);
    }

    protected override void OnInitialized()
    {
        _snapshot = Session.Snapshot;
        Session.SnapshotChanged += OnSnapshotChanged;
    }

    private static string ArcLength(double fraction) => Format(Circumference * 0.75 * fraction);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private void OnSnapshotChanged(object? sender, DeviceSnapshot snapshot) =>
        _ = InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = snapshot;
            StateHasChanged();
        });

    private void ChangeTarget(int steps)
    {
        if (!CanControl || DisplayedTarget is not { } current)
        {
            return;
        }

        var next = Climate!.Limits.Offset(current, steps);
        if (TemperatureLimits.AreEqual(next, current))
        {
            return;
        }

        _pendingTarget = next;
        ShowFeedback(FeedbackKind.Pending, $"Ny måltemperatur {SwedishText.TemperatureWithUnit(next)} skickas strax…");

        CancelAndDispose(ref _debounce);
        _debounce = new CancellationTokenSource();
        _ = SendTargetAfterDebounceAsync(next, _debounce.Token);
    }

    private async Task SendTargetAfterDebounceAsync(double target, CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, debounceToken);
        }
        catch (OperationCanceledException)
        {
            return; // A newer value replaced this one.
        }

        ShowFeedback(FeedbackKind.Sending, $"Ställer in {SwedishText.TemperatureWithUnit(target)}…");
        StateHasChanged();

        // Not tied to the component lifetime: once sent, a command should complete even if the tab closes.
        var result = await RunCommandAsync(() => Session.SetTargetTemperatureAsync(target, CancellationToken.None));
        if (_disposed)
        {
            return;
        }

        _snapshot = Session.Snapshot;
        if (_pendingTarget is { } pending && !TemperatureLimits.AreEqual(pending, target))
        {
            // The user changed the value again while this command ran; that command reports instead.
            StateHasChanged();
            return;
        }

        _pendingTarget = null;
        ShowResult(result, $"Måltemperaturen är {SwedishText.TemperatureWithUnit(target)}");
        StateHasChanged();
    }

    private async Task SelectModeAsync(ClimateMode mode)
    {
        if (!CanControl || _pendingMode is not null || mode == DisplayedMode)
        {
            return;
        }

        _pendingMode = mode;
        ShowFeedback(FeedbackKind.Sending, $"Byter driftläge till {SwedishText.ModeLabel(mode)}…");

        var result = await RunCommandAsync(() => Session.SetModeAsync(mode, CancellationToken.None));
        if (_disposed)
        {
            return;
        }

        _snapshot = Session.Snapshot;
        _pendingMode = null;
        ShowResult(result, $"Driftläget är {SwedishText.ModeLabel(mode)}");
    }

    private static async Task<CommandResult> RunCommandAsync(Func<Task<CommandResult>> command)
    {
        try
        {
            return await command();
        }
#pragma warning disable CA1031 // Any unexpected failure must be shown to the user, not crash the circuit.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return CommandResult.Failed(DeviceErrorKind.Unknown, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private void ShowResult(CommandResult result, string successMessage)
    {
        if (result.Succeeded)
        {
            ShowFeedback(FeedbackKind.Success, successMessage);
        }
        else
        {
            var kind = result.Error?.Kind ?? DeviceErrorKind.Unknown;
            ShowFeedback(FeedbackKind.Error, SwedishText.ErrorMessage(kind), result.Error?.Detail);
        }
    }

    private void ShowFeedback(FeedbackKind kind, string message, string? detail = null)
    {
        CancelAndDispose(ref _feedbackTimer);
        var feedback = new Feedback(kind, message, detail);
        _feedback = feedback;

        var visibleFor = kind switch
        {
            FeedbackKind.Success => SuccessVisibleFor,
            FeedbackKind.Error => ErrorVisibleFor,
            _ => (TimeSpan?)null,
        };

        if (visibleFor is { } delay)
        {
            _feedbackTimer = new CancellationTokenSource();
            _ = ClearFeedbackLaterAsync(feedback, delay, _feedbackTimer.Token);
        }
    }

    private async Task ClearFeedbackLaterAsync(Feedback feedback, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_disposed && ReferenceEquals(_feedback, feedback))
        {
            _feedback = null;
            StateHasChanged();
        }
    }

    private sealed record Feedback(FeedbackKind Kind, string Message, string? Detail);
}
