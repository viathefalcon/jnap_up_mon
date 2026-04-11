package net.viathefalcon.jnapupmon.remote

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.le.BluetoothLeScanner
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.ParcelUuid
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.RequiresApi
import androidx.annotation.RequiresPermission
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import net.viathefalcon.jnapupmon.remote.ui.theme.RemoteTheme
import java.util.UUID

sealed class UiState {
    data object Scanning : UiState()
    data class Connecting(val device: BleDevice) : UiState()
    data class Failed(val device: BleDevice) : UiState()
    data class Connected(val device: BleDevice) : UiState()
    data object Cancelled : UiState()
}

class MainActivity : ComponentActivity() {
    private var bluetoothAdapter: BluetoothAdapter? = null
    private var bluetoothLeScanner: BluetoothLeScanner? = null
    private var uiState = mutableStateOf<UiState>(UiState.Scanning)
    private var scanActive = false

    // Service UUID from the Arduino sketch
    private val SERVICE_UUID = UUID.fromString("505F8A1F-3872-449E-9167-B3549A5D7A87")

    // Characteristic UUIDs from the Arduino sketch
    private val CHARACTERISTIC_MRR_UUID = UUID.fromString("43ADDD14-843B-407C-9B40-696E3819B4AE")
    private val CHARACTERISTIC_RUN_UUID = UUID.fromString("E2C0FF71-A900-434D-9C39-6465443F3F5A")
    private val CHARACTERISTIC_REBOOT_UUID = UUID.fromString("143E8851-01C0-49ED-8F37-9D287B6B32C7")
    private val CHARACTERISTIC_RESET_UUID = UUID.fromString("B6C3D7F2-28E7-4C95-B6AB-65D34D7D9E13")

    private var bluetoothGatt: BluetoothGatt? = null

    private val requestPermissionsLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        if (permissions.all { it.value }) {
            startBleScan()
        } else {
            Toast.makeText(this, "Bluetooth permissions required", Toast.LENGTH_SHORT).show()
        }
    }
    
    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult?) {
            result?.let { scanResult ->
                try {
                    val foundDevice = BleDevice(
                        name = scanResult.device.name ?: "Unnamed Device",
                        address = scanResult.device.address,
                        rssi = scanResult.rssi
                    )
                    // Auto-connect to the first discovered device
                    connectToDevice(foundDevice)
                } catch (_: SecurityException) {
                    stopBleScan()
                }
            }
        }
        
        override fun onScanFailed(errorCode: Int) {
            Toast.makeText(
                this@MainActivity,
                "Scan failed with error: $errorCode",
                Toast.LENGTH_LONG
            ).show()
            scanActive = false
        }
    }
    
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        
        val bluetoothManager = getSystemService(BLUETOOTH_SERVICE) as BluetoothManager
        bluetoothAdapter = bluetoothManager.adapter
        bluetoothLeScanner = bluetoothAdapter?.bluetoothLeScanner
        
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(
                Color.TRANSPARENT,
                Color.TRANSPARENT
            )
        )
        setContent {
            RemoteTheme {
                Scaffold(
                    modifier = Modifier.fillMaxSize(),
                    floatingActionButton = {
                        val state = uiState.value
                        FloatingActionButton(
                            onClick = {
                                when (state) {
                                    is UiState.Scanning, is UiState.Connecting -> cancelScan()
                                    is UiState.Cancelled, is UiState.Connected, is UiState.Failed -> resumeScan()
                                }
                            }
                        ) {
                            Icon(
                                imageVector = when (state) {
                                    is UiState.Scanning, is UiState.Connecting -> Icons.Filled.Close
                                    else -> Icons.Filled.Refresh
                                },
                                contentDescription = when (state) {
                                    is UiState.Scanning, is UiState.Connecting -> "Cancel"
                                    else -> "Rescan"
                                }
                            )
                        }
                    }
                ) { innerPadding ->
                    BleStatusScreen(
                        modifier = Modifier.padding(innerPadding),
                        uiState = uiState.value,
                        onRunClick = { triggerBleAction(CHARACTERISTIC_RUN_UUID, "Run triggered") },
                        onRebootClick = { triggerBleAction(CHARACTERISTIC_REBOOT_UUID, "Reboot triggered") },
                        onResetClick = { triggerBleAction(CHARACTERISTIC_RESET_UUID, "Reset triggered") }
                    )
                }
            }
        }
    }
    
    override fun onResume() {
        super.onResume()

        if (uiState.value is UiState.Scanning && !scanActive) {
            if (checkPermissions()) {
                startBleScan()
            } else {
                requestPermissions()
            }
        }
    }
    
    private fun checkPermissions(): Boolean {
        val permissions = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            listOf(
                Manifest.permission.BLUETOOTH_SCAN,
                Manifest.permission.BLUETOOTH_CONNECT
            )
        } else {
            listOf(
                Manifest.permission.BLUETOOTH,
                Manifest.permission.BLUETOOTH_ADMIN,
                Manifest.permission.ACCESS_FINE_LOCATION
            )
        }
        
        return permissions.all {
            ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED
        }
    }
    
    private fun requestPermissions() {
        val permissions = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            arrayOf(
                Manifest.permission.BLUETOOTH_SCAN,
                Manifest.permission.BLUETOOTH_CONNECT
            )
        } else {
            arrayOf(
                Manifest.permission.BLUETOOTH,
                Manifest.permission.BLUETOOTH_ADMIN,
                Manifest.permission.ACCESS_FINE_LOCATION
            )
        }
        
        requestPermissionsLauncher.launch(permissions)
    }
    
    private fun startBleScan() {
        if (bluetoothAdapter?.isEnabled != true) {
            Toast.makeText(this, "Please enable Bluetooth", Toast.LENGTH_SHORT).show()
            return
        }

        uiState.value = UiState.Scanning

        val scanFilter = ScanFilter.Builder()
            .setServiceUuid(ParcelUuid(SERVICE_UUID))
            .build()

        val scanSettings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .setCallbackType(ScanSettings.CALLBACK_TYPE_ALL_MATCHES)
            .setReportDelay(0)
            .build()

        try {
            bluetoothLeScanner?.startScan(listOf(scanFilter), scanSettings, scanCallback)
            scanActive = true
        } catch (_: SecurityException) {
            Toast.makeText(this, "Permission denied", Toast.LENGTH_SHORT).show()
        }
    }
    
    private fun stopBleScan() {
        try {
            bluetoothLeScanner?.stopScan(scanCallback)
        } catch (_: SecurityException) {
            // Ignore
        }
        scanActive = false
    }

    private fun disconnectAndCloseGatt() {
        val gatt = bluetoothGatt ?: return
        try {
            gatt.disconnect()
        } catch (_: SecurityException) {
            // ignore
        }
        try {
            gatt.close()
        } catch (_: SecurityException) {
            // ignore
        }
        if (bluetoothGatt == gatt) {
            bluetoothGatt = null
        }
    }

    private fun cancelScan() {
        stopBleScan()
        disconnectAndCloseGatt()
        uiState.value = UiState.Cancelled
    }

    private fun resumeScan() {
        if (uiState.value is UiState.Connected) {
            disconnectAndCloseGatt()
        }
        if (checkPermissions()) {
            startBleScan()
        } else {
            requestPermissions()
        }
    }
    
    override fun onDestroy() {
        super.onDestroy()
        stopBleScan()
        disconnectAndCloseGatt()
    }

    private fun connectToDevice(bleDevice: BleDevice) {
        val currentState = uiState.value
        if (currentState is UiState.Connecting || currentState is UiState.Connected) return

        stopBleScan()
        uiState.value = UiState.Connecting(bleDevice)

        disconnectAndCloseGatt()

        try {
            val device = bluetoothAdapter?.getRemoteDevice(bleDevice.address) ?: run {
                uiState.value = UiState.Failed(bleDevice)
                return
            }
            bluetoothGatt = device.connectGatt(this, false, gattCallback)
        } catch (_: SecurityException) {
            uiState.value = UiState.Failed(bleDevice)
        }
    }

    private fun triggerBleAction(characteristicUuid: UUID, successMessage: String) {
        val gatt = bluetoothGatt ?: run {
            Toast.makeText(this, "Device not connected", Toast.LENGTH_SHORT).show()
            return
        }
        val service = gatt.getService(SERVICE_UUID) ?: run {
            Toast.makeText(this, "Service not available", Toast.LENGTH_SHORT).show()
            return
        }
        val characteristic = service.getCharacteristic(characteristicUuid) ?: run {
            Toast.makeText(this, "Characteristic not available", Toast.LENGTH_SHORT).show()
            return
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                gatt.writeCharacteristic(
                    characteristic,
                    byteArrayOf(1),
                    BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                )
            } else {
                @Suppress("DEPRECATION")
                characteristic.value = byteArrayOf(1)
                @Suppress("DEPRECATION")
                gatt.writeCharacteristic(characteristic)
            }
            Toast.makeText(this, successMessage, Toast.LENGTH_SHORT).show()
        } catch (_: SecurityException) {
            Toast.makeText(this, "Permission denied", Toast.LENGTH_SHORT).show()
        }
    }

    private val gattCallback = object : BluetoothGattCallback() {
        @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                try {
                    gatt.discoverServices()
                } catch (_: SecurityException) {
                    runOnUiThread {
                        val current = uiState.value
                        if (current is UiState.Connecting) {
                            uiState.value = UiState.Failed(current.device)
                        }
                    }
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                gatt.close()
                runOnUiThread {
                    val current = uiState.value
                    if (current is UiState.Connecting) {
                        uiState.value = UiState.Failed(current.device)
                    } else if (current is UiState.Connected) {
                        // If we lose connection while connected, go back to scanning
                        resumeScan()
                    }
                    if (bluetoothGatt == gatt) {
                        bluetoothGatt = null
                    }
                }
            }
        }

        override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
            if (status == BluetoothGatt.GATT_SUCCESS) {
                val mrrCharacteristic = gatt.getService(SERVICE_UUID)
                    ?.getCharacteristic(CHARACTERISTIC_MRR_UUID)
                try {
                    if (mrrCharacteristic != null) {
                        gatt.readCharacteristic(mrrCharacteristic)
                    } else {
                        runOnUiThread {
                            val current = uiState.value
                            if (current is UiState.Connecting) {
                                uiState.value = UiState.Failed(current.device)
                            }
                        }
                        gatt.disconnect()
                    }
                } catch (_: SecurityException) {
                    runOnUiThread {
                        val current = uiState.value
                        if (current is UiState.Connecting) {
                            uiState.value = UiState.Failed(current.device)
                        }
                    }
                }
            } else {
                runOnUiThread {
                    val current = uiState.value
                    if (current is UiState.Connecting) {
                        uiState.value = UiState.Failed(current.device)
                    }
                }
                try {
                    gatt.disconnect()
                } catch (_: SecurityException) {
                    // ignore
                }
            }
        }

        @Suppress("DEPRECATION")
        @Deprecated("Deprecated in Java")
        override fun onCharacteristicRead(
            gatt: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int
        ) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
                onCharacteristicReadCompat(
                    gatt, characteristic, characteristic.value ?: byteArrayOf(), status
                )
            }
        }

        @RequiresApi(Build.VERSION_CODES.TIRAMISU)
        override fun onCharacteristicRead(
            gatt: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray,
            status: Int
        ) {
            onCharacteristicReadCompat(gatt, characteristic, value, status)
        }

        private fun onCharacteristicReadCompat(
            gatt: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray,
            status: Int
        ) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                runOnUiThread {
                    val current = uiState.value
                    if (current is UiState.Connecting) {
                        uiState.value = UiState.Failed(current.device)
                    }
                }
                try {
                    gatt.disconnect()
                } catch (_: SecurityException) {
                    // ignore
                }
                return
            }
            when (characteristic.uuid) {
                CHARACTERISTIC_MRR_UUID -> {
                    val mrrMs = value.toUInt32LittleEndian()
                    val mrrSeconds = mrrMs.toDouble() / 1000.0
                    runOnUiThread {
                        val current = uiState.value
                        if (current is UiState.Connecting) {
                            uiState.value = UiState.Connected(
                                current.device.copy(mrr = mrrSeconds)
                            )
                        }
                    }
                    // Do not disconnect, so buttons can be used
                }
            }
        }
    }
}

data class BleDevice(
    val name: String,
    val address: String,
    val rssi: Int,
    val mrr: Double? = null
)

private fun ByteArray.toUInt32LittleEndian(): ULong {
    if (size < 4) return 0uL
    return (this[0].toULong() and 0xFFuL) or
            ((this[1].toULong() and 0xFFuL) shl 8) or
            ((this[2].toULong() and 0xFFuL) shl 16) or
            ((this[3].toULong() and 0xFFuL) shl 24)
}

@Composable
fun BleStatusScreen(
    modifier: Modifier = Modifier,
    uiState: UiState,
    onRunClick: () -> Unit,
    onRebootClick: () -> Unit,
    onResetClick: () -> Unit
) {
    Column(
        modifier = modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text(
            text = "JNAP UpMon",
            style = MaterialTheme.typography.headlineMedium,
            modifier = Modifier.padding(top = 16.dp, bottom = 16.dp)
        )
        Box(
            modifier = Modifier.fillMaxSize(),
            contentAlignment = Alignment.Center
        ) {
        when (uiState) {
            is UiState.Scanning -> {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    CircularProgressIndicator(modifier = Modifier.size(48.dp))
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(
                        text = "Scanning...",
                        style = MaterialTheme.typography.bodyLarge
                    )
                }
            }
            is UiState.Connecting -> {
                val label = uiState.device.name.takeIf { it != "Unnamed Device" }
                    ?: uiState.device.address
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    CircularProgressIndicator(modifier = Modifier.size(48.dp))
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(
                        text = "Connecting to $label...",
                        style = MaterialTheme.typography.bodyLarge
                    )
                }
            }
            is UiState.Failed -> {
                val label = uiState.device.name.takeIf { it != "Unnamed Device" }
                    ?: uiState.device.address
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(
                        imageVector = Icons.Filled.Warning,
                        contentDescription = "Warning",
                        modifier = Modifier.size(48.dp),
                        tint = MaterialTheme.colorScheme.error
                    )
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(
                        text = "Failed to connect to $label",
                        style = MaterialTheme.typography.bodyLarge
                    )
                }
            }
            is UiState.Connected -> {
                DeviceCard(
                    device = uiState.device,
                    onRunClick = onRunClick,
                    onRebootClick = onRebootClick,
                    onResetClick = onResetClick
                )
            }
            is UiState.Cancelled -> {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(
                        imageVector = Icons.Filled.Warning,
                        contentDescription = "Cancelled",
                        modifier = Modifier.size(48.dp),
                        tint = MaterialTheme.colorScheme.error
                    )
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(
                        text = "Cancelled",
                        style = MaterialTheme.typography.bodyLarge
                    )
                }
            }
        }
        }
    }
}

@Composable
fun DeviceCard(
    device: BleDevice,
    onRunClick: () -> Unit,
    onRebootClick: () -> Unit,
    onResetClick: () -> Unit
) {
    var showRebootConfirmation by remember { mutableStateOf(false) }

    if (showRebootConfirmation) {
        AlertDialog(
            onDismissRequest = { showRebootConfirmation = false },
            title = { Text(text = "Confirm Reboot") },
            text = { Text(text = "Are you sure you want to trigger the JNAP reboot?") },
            confirmButton = {
                TextButton(
                    onClick = {
                        showRebootConfirmation = false
                        onRebootClick()
                    }
                ) {
                    Text("Reboot")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = { showRebootConfirmation = false }
                ) {
                    Text("Cancel")
                }
            }
        )
    }

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(16.dp),
        elevation = CardDefaults.cardElevation(defaultElevation = 4.dp)
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp)
        ) {
            Text(
                text = device.name,
                style = MaterialTheme.typography.titleLarge
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "Address: ${device.address}",
                style = MaterialTheme.typography.bodyLarge
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "Signal: ${device.rssi} dBm",
                style = MaterialTheme.typography.bodyLarge
            )
            device.mrr?.let { mrr ->
                Spacer(modifier = Modifier.height(8.dp))
                val mrrText = if (mrr == 0.0) {
                    "Last restart: Never"
                } else {
                    if (mrr < 1.0) {
                        "Last restart: Just now"
                    } else {
                        val seconds = mrr.toLong()
                        val days = seconds / 86400
                        val hours = (seconds % 86400) / 3600
                        val minutes = (seconds % 3600) / 60
                        val remainingSeconds = seconds % 60

                        val parts = mutableListOf<String>()
                        if (days > 0) parts.add("${days}d")
                        if (hours > 0) parts.add("${hours}h")
                        if (minutes > 0) parts.add("${minutes}m")
                        if (remainingSeconds > 0 || parts.isEmpty()) parts.add("${remainingSeconds}s")

                        "Last restart: ${parts.joinToString(" ")} ago"
                    }
                }
                Text(
                    text = mrrText,
                    style = MaterialTheme.typography.bodyLarge
                )
            }
            Spacer(modifier = Modifier.height(16.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)
            ) {
                Button(
                    onClick = onRunClick,
                    modifier = Modifier.weight(1f)
                ) {
                    Text("Run")
                }
                Button(
                    onClick = { showRebootConfirmation = true },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer,
                        contentColor = MaterialTheme.colorScheme.onErrorContainer
                    )
                ) {
                    Text("Reboot")
                }
            }
            Spacer(modifier = Modifier.height(8.dp))
            Button(
                onClick = onResetClick,
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.primary,
                    contentColor = MaterialTheme.colorScheme.onPrimary
                )
            ) {
                Text("Reset")
            }
        }
    }
}
