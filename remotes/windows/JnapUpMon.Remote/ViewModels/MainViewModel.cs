using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Net.ViaTheFalcon.JnapUpMon.Remote.Bluetooth;

namespace Net.ViaTheFalcon.JnapUpMon.Remote.ViewModels;

/// <summary>
/// Drives the main window: keeps a continuously-updated list of discovered
/// service instances, manages the connection to the currently selected one and
/// surfaces its characteristics to the UI.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // Advertisements that haven't been seen for this long are pruned from the list,
    // unless they belong to the currently selected instance.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    // How often background maintenance (scan recovery, stale pruning) runs.
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher;
    private readonly UpMonScanner _scanner;
    private readonly DispatcherTimer _maintenanceTimer;

    private UpMonConnection? _connection;
    private UpMonInstance? _selectedInstance;
    private int _connectionToken;

    private string _statusText;
    private string _mrrText;
    private string _mruText;
    private bool _isConnected;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "The view model must be created on a UI thread.");

        _statusText = Localizer.Get("Status_Scanning");
        _mrrText = Localizer.Get("Mrr_Unknown");
        _mruText = Localizer.Get("Mru_Unknown");

        RunCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Run), () => IsConnected);
        RebootCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Reboot), () => IsConnected);
        ResetCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Reset), () => IsConnected);

        _scanner = new UpMonScanner();
        _scanner.Discovered += OnInstanceDiscovered;
        _scanner.Stopped += OnScannerStopped;

        _maintenanceTimer = new DispatcherTimer { Interval = MaintenanceInterval };
        _maintenanceTimer.Tick += OnMaintenanceTick;
    }

    public ObservableCollection<UpMonInstance> Instances { get; } = new();

    public AsyncRelayCommand RunCommand { get; }

    public AsyncRelayCommand RebootCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    /// <summary>The instance currently chosen in the drop down.</summary>
    public UpMonInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (ReferenceEquals(_selectedInstance, value))
            {
                return;
            }

            _selectedInstance = value;
            OnPropertyChanged();

            // Connecting is fire-and-forget; UI state is updated as it progresses.
            _ = ConnectToSelectedAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>Localised representation of the read-only "most recent reboot" characteristic.</summary>
    public string MrrText
    {
        get => _mrrText;
        private set => SetField(ref _mrrText, value);
    }

    /// <summary>Localised representation of the "most recent run" characteristic.</summary>
    public string MruText
    {
        get => _mruText;
        private set => SetField(ref _mruText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetField(ref _isConnected, value))
            {
                RunCommand.RaiseCanExecuteChanged();
                RebootCommand.RaiseCanExecuteChanged();
                ResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Begins background scanning and the maintenance timer.</summary>
    public void Start()
    {
        _maintenanceTimer.Start();
        _ = TryStartScanningAsync();
    }

    /// <summary>
    /// Attempts to (re)start the background scan, updating the status text to
    /// reflect whether the Bluetooth radio is available.
    /// </summary>
    private async Task TryStartScanningAsync()
    {
        if (_scanner.IsRunning)
        {
            return;
        }

        UpMonScanStartResult result = await _scanner.StartAsync();
        if (result == UpMonScanStartResult.Started ||
            result == UpMonScanStartResult.AlreadyRunning)
        {
            if (!IsConnected)
            {
                StatusText = Localizer.Get("Status_Scanning");
            }
        }
        else if (!IsConnected)
        {
            StatusText = Localizer.Get(GetStatusKeyForStartFailure(result));
        }
    }

    private void OnInstanceDiscovered(object? sender, UpMonAdvertisementEventArgs e)
    {
        // Marshal onto the UI thread before touching the bound collection.
        _dispatcher.TryEnqueue(() =>
        {
            foreach (UpMonInstance existing in Instances)
            {
                if (existing.BluetoothAddress == e.BluetoothAddress)
                {
                    existing.LastSeen = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(e.LocalName))
                    {
                        existing.Name = e.LocalName;
                    }

                    return;
                }
            }

            Instances.Add(new UpMonInstance(e.BluetoothAddress, e.LocalName));

            // Connect to the first device we discover automatically, so the user
            // does not have to make a selection in the common single-device case.
            if (SelectedInstance is null && Instances.Count == 1)
            {
                SelectedInstance = Instances[0];
            }
        });
    }

    private void OnScannerStopped(object? sender, UpMonScannerStoppedEventArgs e)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!IsConnected)
            {
                StatusText = Localizer.Get(GetStatusKeyForStopReason(e.Reason));
            }
        });

    private void OnMaintenanceTick(object? sender, object e)
    {
        // Keep trying to bring scanning up; the radio may have been turned on
        // after the app started, or recovered after being switched off.
        _ = TryStartScanningAsync();
    }

    private static string GetStatusKeyForStartFailure(UpMonScanStartResult result)
        => result switch
        {
            UpMonScanStartResult.NoBluetoothAdapter => "Status_BluetoothNoAdapter",
            UpMonScanStartResult.BluetoothTurnedOff => "Status_BluetoothOff",
            UpMonScanStartResult.BluetoothDisabled => "Status_BluetoothDisabled",
            UpMonScanStartResult.BluetoothUnavailable => "Status_BluetoothUnavailable",
            _ => "Status_BluetoothUnavailable",
        };

    private static string GetStatusKeyForStopReason(UpMonScanStoppedReason reason)
        => reason switch
        {
            UpMonScanStoppedReason.NoBluetoothAdapter => "Status_ScanStopped_NoAdapter",
            UpMonScanStoppedReason.BluetoothTurnedOff => "Status_ScanStopped_BluetoothOff",
            UpMonScanStoppedReason.BluetoothDisabled => "Status_ScanStopped_BluetoothDisabled",
            UpMonScanStoppedReason.BluetoothUnavailable => "Status_ScanStopped_BluetoothUnavailable",
            _ => "Status_ScanStopped",
        };

    private async Task ConnectToSelectedAsync()
    {
        // Each connection attempt gets a token so a stale attempt can detect that
        // the selection has moved on and abandon its results.
        int token = ++_connectionToken;

        TearDownConnection();
        IsConnected = false;
        MrrText = Localizer.Get("Mrr_Unknown");
        MruText = Localizer.Get("Mru_Unknown");

        UpMonInstance? target = _selectedInstance;
        if (target is null)
        {
            StatusText = Localizer.Get("Status_NoSelection");
            return;
        }

        StatusText = Localizer.Format("Status_Connecting", target.DisplayName);

        UpMonConnection? connection;
        try
        {
            connection = await UpMonConnection.ConnectAsync(target.BluetoothAddress);
        }
        catch
        {
            connection = null;
        }

        // The selection changed (or the VM was disposed) while we were connecting.
        if (token != _connectionToken)
        {
            connection?.Dispose();
            return;
        }

        if (connection is null)
        {
            StatusText = Localizer.Format("Status_Failed", target.DisplayName);

            // Remove the failed instance from the scanned list and clear selection.
            for (int i = Instances.Count - 1; i >= 0; i--)
            {
                if (Instances[i].BluetoothAddress == target.BluetoothAddress)
                {
                    Instances.RemoveAt(i);
                }
            }

            if (ReferenceEquals(_selectedInstance, target))
            {
                _selectedInstance = null;
                OnPropertyChanged(nameof(SelectedInstance));
            }

            return;
        }

        _connection = connection;
        _connection.ConnectionLost += OnConnectionLost;
        _connection.MostRecentRebootChanged += OnMostRecentRebootChanged;
        _connection.MostRecentRunChanged += OnMostRecentRunChanged;
        IsConnected = true;

        // Now that we are connected, prefer the device's own reported name and
        // reflect it in the drop down entry.
        string? deviceName = connection.DeviceName;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            target.Name = deviceName;
        }

        StatusText = Localizer.Format("Status_Connected", target.DisplayName);

        // Read the current values once, then rely on notifications for updates.
        await ReadCurrentValuesAsync();

        // Subscribe to push notifications so the timestamps update without polling.
        if (ReferenceEquals(_connection, connection))
        {
            await connection.StartMostRecentRebootNotificationsAsync();
            await connection.StartMostRecentRunNotificationsAsync();
        }
    }

    private async Task ReadCurrentValuesAsync()
    {
        UpMonConnection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        uint? millis = await connection.ReadMostRecentRebootMillisAsync();
        uint? runMillis = await connection.ReadMostRecentRunMillisAsync();

        // Ignore results that arrive after we've moved on.
        if (!ReferenceEquals(_connection, connection))
        {
            return;
        }

        MrrText = FormatMostRecentReboot(millis);
        MruText = FormatMostRecentRun(runMillis);
    }

    private async Task TriggerAsync(UpMonAction action)
    {
        UpMonConnection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        bool ok = await connection.TriggerAsync(action);
        if (!ReferenceEquals(_connection, connection))
        {
            return;
        }

        StatusText = ok
            ? Localizer.Get($"Action_{action}_Sent")
            : Localizer.Get("Action_Failed");
    }

    private void OnConnectionLost(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _connection))
            {
                return;
            }

            UpMonInstance? droppedInstance = _selectedInstance;

            TearDownConnection();
            IsConnected = false;

            if (droppedInstance is not null)
            {
                for (int i = Instances.Count - 1; i >= 0; i--)
                {
                    if (Instances[i].BluetoothAddress == droppedInstance.BluetoothAddress)
                    {
                        Instances.RemoveAt(i);
                    }
                }

                if (ReferenceEquals(_selectedInstance, droppedInstance))
                {
                    _selectedInstance = null;
                    OnPropertyChanged(nameof(SelectedInstance));
                }
            }

            MrrText = Localizer.Get("Mrr_Unknown");
            MruText = Localizer.Get("Mru_Unknown");
            StatusText = Localizer.Get("Status_Disconnected");
        });

    private void OnMostRecentRunChanged(object? sender, uint? millis)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _connection))
            {
                return;
            }

            MruText = FormatMostRecentRun(millis);
        });

    private void OnMostRecentRebootChanged(object? sender, uint? millis)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _connection))
            {
                return;
            }

            MrrText = FormatMostRecentReboot(millis);
        });

    private void TearDownConnection()
    {
        UpMonConnection? conn = _connection;
        if (conn is null)
        {
            return;
        }

        // Unsubscribe all events and null the field first so no further callbacks
        // reach this view-model after this point.
        conn.ConnectionLost -= OnConnectionLost;
        conn.MostRecentRebootChanged -= OnMostRecentRebootChanged;
        conn.MostRecentRunChanged -= OnMostRecentRunChanged;
        _connection = null;

        // Dispose on a background thread.  Calling Dispose() on the UI STA thread
        // while GATT async operations are still in-flight can deadlock: the WinRT
        // BLE stack needs to dispatch operation completions back to the STA thread,
        // but the STA thread is blocked inside Dispose() waiting for that cleanup.
        // Running Dispose() off-thread lets the STA thread stay free to process
        // those completions; in-flight operations are also cancelled via _disposeCts.
        _ = Task.Run(() =>
        {
            try
            {
                conn.Dispose();
            }
            catch
            {
                // Disposal failures are non-fatal; swallow to avoid crashing
                // the background thread.
            }
        });
    }

    /// <summary>
    /// Renders the milliseconds-since-last-reboot value as the localised wall-clock
    /// time at which the reboot occurred, expressed in the current time zone.
    /// </summary>
    private static string FormatMostRecentReboot(uint? millis)
    {
        if (millis is null)
        {
            return Localizer.Get("Mrr_Unknown");
        }

        if (millis.Value == 0)
        {
            return Localizer.Get("Mrr_Never");
        }

        // The characteristic reports how long ago the reboot happened, so subtract
        // that span from the present to recover the moment it occurred, then render
        // it in the machine's local time zone using the current culture.
        DateTimeOffset rebootedAt =
            DateTimeOffset.Now - TimeSpan.FromMilliseconds(millis.Value);
        return Localizer.Format("Mrr_At", rebootedAt.ToString("F"));
    }

    /// <summary>
    /// Renders the milliseconds-since-last-run value as the localised wall-clock time
    /// at which the run occurred, expressed in the current time zone.
    /// </summary>
    private static string FormatMostRecentRun(uint? millis)
    {
        // The device reports uint.MaxValue when no run has completed yet or the clock
        // rolled over and the elapsed time can no longer be determined.
        if (millis is null || millis.Value == uint.MaxValue)
        {
            return Localizer.Get("Mru_Unknown");
        }

        if (millis.Value == 0)
        {
            return Localizer.Get("Mrr_NotYet");
        }

        // The characteristic reports how long ago the run happened, so subtract that
        // span from the present to recover the moment it occurred, then render it in
        // the machine's local time zone using the current culture.
        DateTimeOffset ranAt =
            DateTimeOffset.Now - TimeSpan.FromMilliseconds(millis.Value);
        return Localizer.Format("Mru_At", ranAt.ToString("F"));
    }

    public void Dispose()
    {
        _connectionToken++;
        _maintenanceTimer.Stop();
        _maintenanceTimer.Tick -= OnMaintenanceTick;

        _scanner.Discovered -= OnInstanceDiscovered;
        _scanner.Stopped -= OnScannerStopped;
        _scanner.Stop();

        TearDownConnection();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
