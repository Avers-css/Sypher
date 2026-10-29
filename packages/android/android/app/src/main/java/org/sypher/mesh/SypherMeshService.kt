package org.sypher.mesh

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.ParcelUuid
import android.util.Log
import java.util.UUID

/**
 * Sypher Foreground BLE Mesh Radio Service.
 * Implements store-and-forward sneakernet proximity mesh:
 * 1. Low-power BLE Advertising broadcasting node identity & service UUID
 * 2. Continuous BLE Scanning for passing Sypher peers
 * 3. Local GATT Server accepting direct encrypted mesh packets without Wi-Fi
 * 4. GATT Client for peer-to-peer packet exchange between walking passerby
 */
class SypherMeshService : Service() {

    companion object {
        const val TAG = "SypherMeshService"
        const val CHANNEL_ID = "sypher_mesh_channel"
        const val NOTIFICATION_ID = 1337
        val SYPHER_SERVICE_UUID: UUID = UUID.fromString("00005950-0000-1000-8000-00805f9b34fb")
        val SYPHER_CHAR_UUID: UUID = UUID.fromString("00005951-0000-1000-8000-00805f9b34fb")

        var instance: SypherMeshService? = null
    }

    private var bluetoothAdapter: BluetoothAdapter? = null
    private var bleScanner: BluetoothLeScanner? = null
    private var bleAdvertiser: BluetoothLeAdvertiser? = null
    private var gattServer: BluetoothGattServer? = null
    private var currentNodeId: String = "syp_"

    override fun onCreate() {
        super.onCreate()
        instance = this
        try {
            createNotificationChannel()
            val notification = buildOngoingNotification()

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
                )
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }

            initBluetoothRadio()
            initGattServer()
        } catch (t: Throwable) {
            Log.e(TAG, "Error in service onCreate", t)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val nodeId = intent?.getStringExtra("EXTRA_NODE_ID")
        if (!nodeId.isNullOrBlank()) {
            currentNodeId = nodeId
        }
        try {
            startMeshRadio()
        } catch (t: Throwable) {
            Log.e(TAG, "Error starting mesh radio in service", t)
        }
        return START_STICKY
    }

    private fun initBluetoothRadio() {
        try {
            val bluetoothManager = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
            bluetoothAdapter = bluetoothManager?.adapter
            if (bluetoothAdapter?.isEnabled == true) {
                bleScanner = bluetoothAdapter?.bluetoothLeScanner
                bleAdvertiser = bluetoothAdapter?.bluetoothLeAdvertiser
            }
        } catch (t: Throwable) {
            Log.w(TAG, "Bluetooth not accessible or disabled: ${t.message}")
        }
    }

    private fun initGattServer() {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                    return
                }
            }

            val bluetoothManager = getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
            gattServer = bluetoothManager?.openGattServer(this, object : BluetoothGattServerCallback() {
                override fun onCharacteristicWriteRequest(
                    device: BluetoothDevice?,
                    requestId: Int,
                    characteristic: BluetoothGattCharacteristic?,
                    preparedWrite: Boolean,
                    responseNeeded: Boolean,
                    offset: Int,
                    value: ByteArray?
                ) {
                    super.onCharacteristicWriteRequest(device, requestId, characteristic, preparedWrite, responseNeeded, offset, value)
                    if (responseNeeded) {
                        try {
                            gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, value)
                        } catch (e: Exception) {}
                    }
                    if (value != null && value.isNotEmpty()) {
                        val rawJson = String(value, Charsets.UTF_8)
                        Log.i(TAG, "Received BLE Mesh packet (${value.size} bytes) from ${device?.address}")
                        MainActivity.instance?.deliverMeshPacket(rawJson)
                    }
                }
            })

            val service = BluetoothGattService(SYPHER_SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)
            val characteristic = BluetoothGattCharacteristic(
                SYPHER_CHAR_UUID,
                BluetoothGattCharacteristic.PROPERTY_WRITE or BluetoothGattCharacteristic.PROPERTY_READ or BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
                BluetoothGattCharacteristic.PERMISSION_WRITE or BluetoothGattCharacteristic.PERMISSION_READ
            )
            service.addCharacteristic(characteristic)
            gattServer?.addService(service)
            Log.i(TAG, "BLE GATT Server initialized for offline sneakernet reception")
        } catch (t: Throwable) {
            Log.w(TAG, "Could not initialize GATT server: ${t.message}")
        }
    }

    fun startMeshRadio() {
        if (bluetoothAdapter == null || bluetoothAdapter?.isEnabled != true) {
            Log.i(TAG, "Bluetooth is disabled or unavailable. Waiting for radio activation.")
            return
        }

        // Check runtime permission on Android 12+ (API 31+)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(android.Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                Log.w(TAG, "Bluetooth permissions not yet granted by user.")
                return
            }
        }

        // 1. Start BLE Advertising with Node Identity in Service Data
        try {
            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                .setConnectable(true)
                .setTimeout(0)
                .build()

            val cleanId = currentNodeId.take(8).toByteArray(Charsets.UTF_8)
            val data = AdvertiseData.Builder()
                .setIncludeDeviceName(false)
                .addServiceUuid(ParcelUuid(SYPHER_SERVICE_UUID))
                .addServiceData(ParcelUuid(SYPHER_SERVICE_UUID), cleanId)
                .build()

            bleAdvertiser?.stopAdvertising(object : AdvertiseCallback() {})
            bleAdvertiser?.startAdvertising(settings, data, object : AdvertiseCallback() {
                override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
                    Log.i(TAG, "BLE Mesh beacon broadcasting successfully (Node: $currentNodeId)")
                }
                override fun onStartFailure(errorCode: Int) {
                    Log.w(TAG, "BLE advertising failure code: $errorCode")
                }
            })
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE advertiser: ${t.message}")
        }

        // 2. Start BLE Scanning to Discover Nearby Passing Devices
        try {
            val scanSettings = ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_BALANCED)
                .setReportDelay(0)
                .build()

            val scanFilter = ScanFilter.Builder()
                .setServiceUuid(ParcelUuid(SYPHER_SERVICE_UUID))
                .build()

            bleScanner?.stopScan(object : ScanCallback() {})
            bleScanner?.startScan(listOf(scanFilter), scanSettings, object : ScanCallback() {
                override fun onScanResult(callbackType: Int, result: ScanResult?) {
                    result ?: return
                    val scanRecord = result.scanRecord ?: return
                    val serviceData = scanRecord.getServiceData(ParcelUuid(SYPHER_SERVICE_UUID))

                    val peerId = if (serviceData != null && serviceData.isNotEmpty()) {
                        String(serviceData, Charsets.UTF_8)
                    } else {
                        "syp_" + result.device.address.replace(":", "").takeLast(6).lowercase()
                    }

                    val rssi = result.rssi
                    Log.d(TAG, "Discovered Sypher BLE Peer: $peerId (RSSI: $rssi, MAC: ${result.device.address})")

                    // Forward discovered passerby to UI and register device MAC
                    MainActivity.instance?.notifyBlePeerDiscovered(peerId, rssi, result.device.address)
                }

                override fun onScanFailed(errorCode: Int) {
                    Log.w(TAG, "BLE Scan failed: $errorCode")
                }
            })
            Log.i(TAG, "BLE Mesh scanner active. Seeking passing devices.")
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE scanner: ${t.message}")
        }
    }

    /**
     * Transmits a mesh packet to a nearby discovered BLE device over direct GATT connection
     * without requiring Wi-Fi or router infrastructure.
     */
    fun sendBlePacket(macAddress: String, jsonPacket: String) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                return
            }
        }

        try {
            val device = bluetoothAdapter?.getRemoteDevice(macAddress) ?: return
            device.connectGatt(this, false, object : BluetoothGattCallback() {
                override fun onConnectionStateChange(gatt: BluetoothGatt?, status: Int, newState: Int) {
                    if (newState == BluetoothProfile.STATE_CONNECTED) {
                        Log.d(TAG, "Connected to BLE peer $macAddress, discovering GATT services...")
                        gatt?.discoverServices()
                    } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                        gatt?.close()
                    }
                }

                override fun onServicesDiscovered(gatt: BluetoothGatt?, status: Int) {
                    if (status == BluetoothGatt.GATT_SUCCESS) {
                        val service = gatt?.getService(SYPHER_SERVICE_UUID)
                        val characteristic = service?.getCharacteristic(SYPHER_CHAR_UUID)
                        if (characteristic != null) {
                            val bytes = jsonPacket.toByteArray(Charsets.UTF_8)
                            characteristic.value = bytes
                            characteristic.writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                            gatt.writeCharacteristic(characteristic)
                            Log.i(TAG, "Wrote ${bytes.size} bytes to BLE peer $macAddress")
                        } else {
                            gatt?.disconnect()
                        }
                    } else {
                        gatt?.disconnect()
                    }
                }

                override fun onCharacteristicWrite(
                    gatt: BluetoothGatt?,
                    characteristic: BluetoothGattCharacteristic?,
                    status: Int
                ) {
                    Log.i(TAG, "BLE characteristic write finished with status: $status")
                    gatt?.disconnect()
                }
            })
        } catch (t: Throwable) {
            Log.w(TAG, "Error sending BLE packet to $macAddress: ${t.message}")
        }
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Sypher Mesh Background Radio",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Keeps local P2P mesh relay active with zero cloud dependencies."
                setShowBadge(false)
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildOngoingNotification(): Notification {
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL_ID)
        } else {
            Notification.Builder(this)
        }

        return builder
            .setContentTitle("Sypher Mesh")
            .setContentText("Автономный P2P радиоканал активен • BLE + UDP • 0 серверов")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        super.onDestroy()
        instance = null
        try {
            bleAdvertiser?.stopAdvertising(object : AdvertiseCallback() {})
            bleScanner?.stopScan(object : ScanCallback() {})
            gattServer?.close()
        } catch (t: Throwable) {
            Log.w(TAG, "Error stopping radio in onDestroy: ${t.message}")
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
