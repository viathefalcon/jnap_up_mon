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

    // How often the read-only characteristics are re-read while connected.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher;
    private readonly UpMonScanner _scanner;
    private readonly DispatcherTimer _maintenanceTimer;

    private UpMonConnection? _connection;
    private UpMonInstance? _selectedInstance;
    private int _connectionToken;

    private string _statusText;
    private string _mrrText;
    private bool _isConnected;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "The view model must be created on a UI thread.");

        _statusText = Localizer.Get("Status_Scanning");
        _mrrText = Localizer.Get("Mrr_Unknown");

        RunCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Run), () => IsConnected);
        RebootCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Reboot), () => IsConnected);
        ResetCommand = new AsyncRelayCommand(() => TriggerAsync(UpMonAction.Reset), () => IsConnected);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);

        _scanner = new UpMonScanner();
        _scanner.Discovered += OnInstanceDiscovered;
        _scanner.Stopped += OnScannerStopped;

        _maintenanceTimer = new DispatcherTimer { Interval = RefreshInterval };
        _maintenanceTimer.Tick += OnMaintenanceTick;
    }

    public ObservableCollection<UpMonInstance> Instances { get; } = new();

    public AsyncRelayCommand RunCommand { get; }

    public AsyncRelayCommand RebootCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

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
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Begins background scanning and the maintenance timer.</summary>
    public void Start()
    {
        _maintenanceTimer.Start();
        TryStartScanning();
    }

    /// <summary>
    /// Attempts to (re)start the background scan, updating the status text to
    /// reflect whether the Bluetooth radio is available.
    /// </summary>
    private void TryStartScanning()
    {
        if (_scanner.IsRunning)
        {
            return;
        }

        if (_scanner.Start())
        {
            if (!IsConnected)
            {
                StatusText = Localizer.Get("Status_Scanning");
            }
        }
        else if (!IsConnected)
        {
            StatusText = Localizer.Get("Status_BluetoothUnavailable");
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

    private void OnScannerStopped(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!IsConnected)
            {
                StatusText = Localizer.Get("Status_ScanStopped");
            }
        });

    private void OnMaintenanceTick(object? sender, object e)
    {
        // Keep trying to bring scanning up; the radio may have been turned on
        // after the app started, or recovered after being switched off.
        TryStartScanning();

        PruneStaleInstances();

        if (IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    private void PruneStaleInstances()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - StaleThreshold;
        for (int i = Instances.Count - 1; i >= 0; i--)
        {
            UpMonInstance instance = Instances[i];
            if (instance.LastSeen < cutoff &&
                !ReferenceEquals(instance, _selectedInstance))
            {
                Instances.RemoveAt(i);
            }
        }
    }

    private async Task ConnectToSelectedAsync()
    {
        // Each connection attempt gets a token so a stale attempt can detect that
        // the selection has moved on and abandon its results.
        int token = ++_connectionToken;

        TearDownConnection();
        IsConnected = false;
        MrrText = Localizer.Get("Mrr_Unknown");

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
            return;
        }

        _connection = connection;
        _connection.ConnectionLost += OnConnectionLost;
        IsConnected = true;

        // Now that we are connected, prefer the device's own reported name and
        // reflect it in the drop down entry.
        string? deviceName = connection.DeviceName;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            target.Name = deviceName;
        }

        StatusText = Localizer.Format("Status_Connected", target.DisplayName);

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        UpMonConnection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        uint? millis = await connection.ReadMostRecentRebootMillisAsync();

        // Ignore results that arrive after we've moved on.
        if (!ReferenceEquals(_connection, connection))
        {
            return;
        }

        MrrText = FormatMostRecentReboot(millis);
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

        // The actions can change the device's reboot timestamp; reflect it promptly.
        await RefreshAsync();
    }

    private void OnConnectionLost(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _connection))
            {
                return;
            }

            TearDownConnection();
            IsConnected = false;
            MrrText = Localizer.Get("Mrr_Unknown");
            StatusText = Localizer.Get("Status_Disconnected");
        });

    private void TearDownConnection()
    {
        if (_connection is null)
        {
            return;
        }

        _connection.ConnectionLost -= OnConnectionLost;
        _connection.Dispose();
        _connection = null;
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
