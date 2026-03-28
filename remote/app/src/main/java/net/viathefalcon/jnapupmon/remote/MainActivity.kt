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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import net.viathefalcon.jnapupmon.remote.ui.theme.RemoteTheme
import java.util.UUID

class MainActivity : ComponentActivity() {
    private var bluetoothAdapter: BluetoothAdapter? = null
    private var bluetoothLeScanner: BluetoothLeScanner? = null
    private var isScanning = mutableStateOf(false)
    private val discoveredDevices = mutableStateListOf<BleDevice>()
    private var hasAutoStarted = false

    // Service UUID from the Arduino sketch
    private val SERVICE_UUID = UUID.fromString("505F8A1F-3872-449E-9167-B3549A5D7A87")

    // Characteristic UUIDs from the Arduino sketch
    private val CHARACTERISTIC_STATE_UUID = UUID.fromString("8186E6A2-77A6-43CC-8C99-31DC36136147")
    private val CHARACTERISTIC_MRR_UUID = UUID.fromString("43ADDD14-843B-407C-9B40-696E3819B4AE")

    private var bluetoothGatt: BluetoothGatt? = null
    private var connectingAddress = mutableStateOf<String?>(null)
    
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
                        name = scanResult.device.name ?: "Unknown Device",
                        address = scanResult.device.address,
                        rssi = scanResult.rssi
                    )
                    
                    // Update device if exists (preserve characteristic values), otherwise add new
                    val existingIndex = discoveredDevices.indexOfFirst { it.address == foundDevice.address }
                    if (existingIndex >= 0) {
                        discoveredDevices[existingIndex] = discoveredDevices[existingIndex].copy(
                            name = foundDevice.name,
                            rssi = foundDevice.rssi
                        )
                    } else {
                        discoveredDevices.add(foundDevice)
                    }
                } catch (e: SecurityException) {
                    // Permission was revoked during scan
                    stopBleScan()
                }
            }
        }
        
        override fun onScanFailed(errorCode: Int) {
            Toast.makeText(
                this@MainActivity,
                "Scan failed with error: $errorCode",
                Toast.LENGTH_SHORT
            ).show()
            isScanning.value = false
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
                        FloatingActionButton(
                            onClick = { toggleScan() }
                        ) {
                            Icon(
                                imageVector = if (isScanning.value) Icons.Filled.Close else Icons.Filled.Refresh,
                                contentDescription = if (isScanning.value) "Stop scanning" else "Restart scanning"
                            )
                        }
                    }
                ) { innerPadding ->
                    BleScanner(
                        modifier = Modifier.padding(innerPadding),
                        isScanning = isScanning.value,
                        devices = discoveredDevices,
                        connectingAddress = connectingAddress.value,
                        onDeviceTapped = { device -> connectToDevice(device) }
                    )
                }
            }
        }
    }
    
    override fun onResume() {
        super.onResume()
        
        // Automatically start scanning when the activity first launches
        if (!hasAutoStarted) {
            hasAutoStarted = true
            if (checkPermissions()) {
                startBleScan()
            } else {
                requestPermissions()
            }
        }
    }
    
    private fun toggleScan() {
        if (isScanning.value) {
            stopBleScan()
        } else {
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
        
        // Don't clear devices - keep accumulating discoveries
        
        val scanFilter = ScanFilter.Builder()
            .setServiceUuid(ParcelUuid(SERVICE_UUID))
            .build()
        
        val scanSettings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .setCallbackType(ScanSettings.CALLBACK_TYPE_ALL_MATCHES)
            .setReportDelay(0) // Report immediately for continuous updates
            .build()
        
        try {
            bluetoothLeScanner?.startScan(listOf(scanFilter), scanSettings, scanCallback)
            isScanning.value = true
        } catch (e: SecurityException) {
            Toast.makeText(this, "Permission denied", Toast.LENGTH_SHORT).show()
        }
    }
    
    private fun stopBleScan() {
        try {
            bluetoothLeScanner?.stopScan(scanCallback)
        } catch (e: SecurityException) {
            // Ignore
        }
        isScanning.value = false
    }
    
    override fun onDestroy() {
        super.onDestroy()
        stopBleScan()
        try {
            bluetoothGatt?.close()
        } catch (e: SecurityException) {
            // ignore
        }
        bluetoothGatt = null
    }

    private fun connectToDevice(bleDevice: BleDevice) {
        if (connectingAddress.value != null) return
        stopBleScan()
        try {
            bluetoothGatt?.close()
        } catch (e: SecurityException) {
            // ignore
        }
        bluetoothGatt = null
        connectingAddress.value = bleDevice.address
        try {
            val device = bluetoothAdapter?.getRemoteDevice(bleDevice.address) ?: run {
                connectingAddress.value = null
                return
            }
            bluetoothGatt = device.connectGatt(this, false, gattCallback)
        } catch (e: SecurityException) {
            Toast.makeText(this, "Permission denied", Toast.LENGTH_SHORT).show()
            connectingAddress.value = null
        }
    }

    private val gattCallback = object : BluetoothGattCallback() {
        @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                try {
                    gatt.discoverServices()
                } catch (e: SecurityException) {
                    runOnUiThread { connectingAddress.value = null }
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                gatt.close()
                runOnUiThread {
                    if (connectingAddress.value == gatt.device.address) {
                        connectingAddress.value = null
                    }
                    if (bluetoothGatt == gatt) {
                        bluetoothGatt = null
                    }
                }
            }
        }

        override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
            if (status == BluetoothGatt.GATT_SUCCESS) {
                val stateChar = gatt.getService(SERVICE_UUID)
                    ?.getCharacteristic(CHARACTERISTIC_STATE_UUID)
                try {
                    if (stateChar != null) {
                        gatt.readCharacteristic(stateChar)
                    } else {
                        runOnUiThread { connectingAddress.value = null }
                        gatt.disconnect()
                    }
                } catch (e: SecurityException) {
                    runOnUiThread { connectingAddress.value = null }
                }
            } else {
                runOnUiThread { connectingAddress.value = null }
                try {
                    gatt.disconnect()
                } catch (e: SecurityException) {
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
                runOnUiThread { connectingAddress.value = null }
                try {
                    gatt.disconnect()
                } catch (e: SecurityException) {
                    // ignore
                }
                return
            }
            val address = gatt.device.address
            when (characteristic.uuid) {
                CHARACTERISTIC_STATE_UUID -> {
                    val stateByte = if (value.isNotEmpty()) value[0].toInt() and 0xFF else -1
                    val stateString = stateByteToString(stateByte)
                    runOnUiThread {
                        val idx = discoveredDevices.indexOfFirst { it.address == address }
                        if (idx >= 0) {
                            discoveredDevices[idx] = discoveredDevices[idx].copy(state = stateString)
                        }
                    }
                    val mrrChar = gatt.getService(SERVICE_UUID)
                        ?.getCharacteristic(CHARACTERISTIC_MRR_UUID)
                    try {
                        if (mrrChar != null) {
                            gatt.readCharacteristic(mrrChar)
                        } else {
                            runOnUiThread { connectingAddress.value = null }
                            gatt.disconnect()
                        }
                    } catch (e: SecurityException) {
                        runOnUiThread { connectingAddress.value = null }
                    }
                }
                CHARACTERISTIC_MRR_UUID -> {
                    val mrrMs = value.toUInt32LittleEndian()
                    val mrrSeconds = mrrMs.toDouble() / 1000.0
                    runOnUiThread {
                        val idx = discoveredDevices.indexOfFirst { it.address == address }
                        if (idx >= 0) {
                            discoveredDevices[idx] = discoveredDevices[idx].copy(mrrSeconds = mrrSeconds)
                        }
                        connectingAddress.value = null
                    }
                    try {
                        gatt.disconnect()
                    } catch (e: SecurityException) {
                        // ignore
                    }
                }
            }
        }
    }

    private fun stateByteToString(stateByte: Int): String = when (stateByte) {
        0x00 -> "Starting"
        0x01 -> "Polling"
        0x02 -> "Connecting"
        0x03 -> "Reading"
        0x04 -> "Rebooting"
        else -> "Unknown"
    }
}

data class BleDevice(
    val name: String,
    val address: String,
    val rssi: Int,
    val state: String? = null,
    val mrrSeconds: Double? = null
)

private fun ByteArray.toUInt32LittleEndian(): ULong {
    if (size < 4) return 0uL
    return (this[0].toULong() and 0xFFuL) or
            ((this[1].toULong() and 0xFFuL) shl 8) or
            ((this[2].toULong() and 0xFFuL) shl 16) or
            ((this[3].toULong() and 0xFFuL) shl 24)
}

@Composable
fun BleScanner(
    modifier: Modifier = Modifier,
    isScanning: Boolean,
    devices: List<BleDevice>,
    connectingAddress: String? = null,
    onDeviceTapped: (BleDevice) -> Unit = {}
) {
    Box(modifier = modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "JNAP UpMon Remote",
                style = MaterialTheme.typography.headlineMedium,
                modifier = Modifier.padding(bottom = 16.dp)
            )
            
            Text(
                text = if (isScanning) "Scanning..." else "Scanning stopped",
                style = MaterialTheme.typography.bodyMedium,
                color = if (isScanning) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
                modifier = Modifier.padding(bottom = 16.dp)
            )
            
            if (devices.isEmpty()) {
                Text(
                    text = if (isScanning) "Waiting for devices..." else "No devices found",
                    style = MaterialTheme.typography.bodyLarge,
                    modifier = Modifier.padding(16.dp)
                )
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(devices) { device ->
                        DeviceCard(
                            device = device,
                            isConnecting = device.address == connectingAddress,
                            onTap = { onDeviceTapped(device) }
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
    isConnecting: Boolean = false,
    onTap: () -> Unit = {}
) {
    val isExpanded = device.state != null || device.mrrSeconds != null
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = !isConnecting) { onTap() },
        elevation = CardDefaults.cardElevation(defaultElevation = 4.dp)
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp)
        ) {
            Text(
                text = device.name,
                style = MaterialTheme.typography.titleMedium
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "Address: ${device.address}",
                style = MaterialTheme.typography.bodyMedium
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "Signal: ${device.rssi} dBm",
                style = MaterialTheme.typography.bodySmall
            )
            if (isConnecting) {
                Spacer(modifier = Modifier.height(8.dp))
                CircularProgressIndicator(modifier = Modifier.size(24.dp))
            }
            if (isExpanded) {
                Spacer(modifier = Modifier.height(8.dp))
                device.state?.let { state ->
                    Text(
                        text = "State: $state",
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
                device.mrrSeconds?.let { mrr ->
                    Spacer(modifier = Modifier.height(4.dp))
                    val mrrText = if (mrr == 0.0) {
                        "Last restart: Never"
                    } else {
                        "Last restart: ${"%.3f".format(mrr)} s ago"
                    }
                    Text(
                        text = mrrText,
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
            }
        }
    }
}
