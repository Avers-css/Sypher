package org.sypher.mesh

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.ParcelUuid
import android.util.Log
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue

/**
 * Sypher Foreground BLE Mesh Radio Service.
 * Implements store-and-forward sneakernet proximity mesh:
 * 1. Split-packet BLE Advertising (Under 31-byte limit to prevent DATA_TOO_LARGE)
 * 2. Unrestricted continuous BLE Scanning for passing Sypher peers
 * 3. Local GATT Server accepting direct encrypted mesh packets without Wi-Fi & without OS pairing
 * 4. GATT Client with TRANSPORT_LE & MTU 512 for instant P2P transfer between walking passerby
 * 5. Automatic Bluetooth radio state listener & periodic pulse monitor
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

    private val mainHandler = Handler(Looper.getMainLooper())
    private var isRadioActive = false

    // Multi-chunk reassembly buffer: MAC -> StringBuilder
    private val chunkBuffers = ConcurrentHashMap<String, StringBuilder>()

    // Outgoing queue per device: MAC -> Queue<ByteArray>
    private val outgoingQueues = ConcurrentHashMap<String, ConcurrentLinkedQueue<ByteArray>>()

    private val bluetoothStateReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action == BluetoothAdapter.ACTION_STATE_CHANGED) {
                val state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR)
                if (state == BluetoothAdapter.STATE_ON) {
                    Log.i(TAG, "Bluetooth turned ON. Activating Sypher mesh radio...")
                    initBluetoothRadio()
                    startMeshRadio()
                } else if (state == BluetoothAdapter.STATE_TURNING_OFF || state == BluetoothAdapter.STATE_OFF) {
                    Log.i(TAG, "Bluetooth turned OFF. Pausing mesh radio.")
                    stopMeshRadio()
                }
            }
        }
    }

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

            // Register Bluetooth state listener
            registerReceiver(bluetoothStateReceiver, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED))

            initBluetoothRadio()
            initGattServer()

            // Periodic radio pulse check every 15 seconds to ensure advertiser & scanner stay alive
            schedulePeriodicRadioCheck()
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

    private fun schedulePeriodicRadioCheck() {
        mainHandler.postDelayed(object : Runnable {
            override fun run() {
                try {
                    if (bluetoothAdapter?.isEnabled == true && !isRadioActive) {
                        Log.i(TAG, "Radio pulse: Restarting mesh radio...")
                        startMeshRadio()
                    }
                } catch (e: Exception) {}
                mainHandler.postDelayed(this, 15000)
            }
        }, 15000)
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
                override fun onConnectionStateChange(device: BluetoothDevice?, status: Int, newState: Int) {
                    super.onConnectionStateChange(device, status, newState)
                    if (newState == BluetoothProfile.STATE_CONNECTED && device != null) {
                        val shortId = device.address.replace(":", "").takeLast(6).lowercase()
                        val peerId = "syp-peer-" + shortId
                        Log.i(TAG, "BLE device connected to GATT Server: $peerId (${device.address})")
                        MainActivity.instance?.notifyBlePeerDiscovered(peerId, -55, device.address)
                    }
                }

                override fun onMtuChanged(device: BluetoothDevice?, mtu: Int) {
                    Log.d(TAG, "GATT Server MTU changed to $mtu for ${device?.address}")
                }

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
                        handleIncomingGattBytes(device?.address ?: "unknown", value)
                    }
                }
            })

            val service = BluetoothGattService(SYPHER_SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)
            val characteristic = BluetoothGattCharacteristic(
                SYPHER_CHAR_UUID,
                BluetoothGattCharacteristic.PROPERTY_WRITE or
                        BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE or
                        BluetoothGattCharacteristic.PROPERTY_READ,
                BluetoothGattCharacteristic.PERMISSION_WRITE or BluetoothGattCharacteristic.PERMISSION_READ
            )
            service.addCharacteristic(characteristic)
            gattServer?.addService(service)
            Log.i(TAG, "BLE GATT Server initialized with service: $SYPHER_SERVICE_UUID")
        } catch (t: Throwable) {
            Log.w(TAG, "Could not initialize GATT server: ${t.message}")
        }
    }

    private fun handleIncomingGattBytes(deviceAddress: String, value: ByteArray) {
        val chunkStr = String(value, Charsets.UTF_8)
        Log.d(TAG, "GATT Server received ${value.size} bytes from $deviceAddress: ${chunkStr.take(30)}...")

        // Handle chunking: CHK:seq:total:data
        if (chunkStr.startsWith("CHK:")) {
            val parts = chunkStr.split(":", limit = 4)
            if (parts.size == 4) {
                val seq = parts[1].toIntOrNull() ?: 0
                val total = parts[2].toIntOrNull() ?: 1
                val data = parts[3]

                val buf = chunkBuffers.getOrPut(deviceAddress) { StringBuilder() }
                if (seq == 0) buf.setLength(0)
                buf.append(data)

                if (seq == total - 1) {
                    val fullJson = buf.toString()
                    chunkBuffers.remove(deviceAddress)
                    deliverCompletePacket(fullJson)
                }
                return
            }
        }

        // Direct complete JSON packet
        if (chunkStr.trimStart().startsWith("{")) {
            deliverCompletePacket(chunkStr)
        }
    }

    private fun deliverCompletePacket(rawJson: String) {
        MainActivity.instance?.runOnUiThread {
            MainActivity.instance?.deliverMeshPacket(rawJson)
        }
    }

    fun startMeshRadio() {
        if (bluetoothAdapter == null || bluetoothAdapter?.isEnabled != true) {
            Log.i(TAG, "Bluetooth is disabled. Radio waiting for activation.")
            isRadioActive = false
            return
        }

        // Check runtime permissions on Android 12+ (API 31+)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(android.Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                Log.w(TAG, "Bluetooth runtime permissions not yet granted.")
                return
            }
        }

        // 1. Start Low-Power Split-Packet BLE Advertising
        try {
            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_BALANCED)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                .setConnectable(true)
                .setTimeout(0)
                .build()

            val pUuid = ParcelUuid(SYPHER_SERVICE_UUID)
            val cleanId = currentNodeId.replace("syp-", "").take(6).toByteArray(Charsets.UTF_8)

            // Primary Advertise Data (Under 10 bytes)
            val advertiseData = AdvertiseData.Builder()
                .setIncludeDeviceName(false)
                .setIncludeTxPowerLevel(false)
                .addServiceUuid(pUuid)
                .build()

            // Secondary Scan Response Data (Under 12 bytes)
            val scanResponseData = AdvertiseData.Builder()
                .addServiceData(pUuid, cleanId)
                .build()

            bleAdvertiser?.stopAdvertising(object : AdvertiseCallback() {})
            bleAdvertiser?.startAdvertising(settings, advertiseData, scanResponseData, object : AdvertiseCallback() {
                override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
                    Log.i(TAG, "BLE Mesh beacon broadcasting: Node $currentNodeId")
                    isRadioActive = true
                }
                override fun onStartFailure(errorCode: Int) {
                    Log.w(TAG, "BLE advertising start notice (code: $errorCode)")
                }
            })
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE advertiser: ${t.message}")
        }

        // 2. Start Continuous BLE Scanning
        try {
            val scanSettings = ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_BALANCED)
                .setReportDelay(0)
                .build()

            val pUuid = ParcelUuid(SYPHER_SERVICE_UUID)
            val scanFilter = ScanFilter.Builder()
                .setServiceUuid(pUuid)
                .build()

            bleScanner?.stopScan(object : ScanCallback() {})
            bleScanner?.startScan(listOf(scanFilter), scanSettings, object : ScanCallback() {
                override fun onScanResult(callbackType: Int, result: ScanResult?) {
                    result ?: return
                    val scanRecord = result.scanRecord ?: return
                    val serviceData = scanRecord.getServiceData(pUuid)

                    val shortId = if (serviceData != null && serviceData.isNotEmpty()) {
                        String(serviceData, Charsets.UTF_8)
                    } else {
                        result.device.address.replace(":", "").takeLast(6).lowercase()
                    }
                    val peerId = "syp-" + shortId

                    val rssi = result.rssi
                    Log.d(TAG, "Discovered Sypher BLE Peer: $peerId (RSSI: $rssi, MAC: ${result.device.address})")

                    // Forward discovered passerby to UI & drain any pending sneakernet packets
                    MainActivity.instance?.notifyBlePeerDiscovered(peerId, rssi, result.device.address)
                }

                override fun onScanFailed(errorCode: Int) {
                    Log.w(TAG, "BLE Scan notice code: $errorCode")
                }
            })
            Log.i(TAG, "BLE Mesh scanner active. Seeking passing devices.")
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE scanner: ${t.message}")
        }
    }

    fun stopMeshRadio() {
        isRadioActive = false
        try {
            bleAdvertiser?.stopAdvertising(object : AdvertiseCallback() {})
            bleScanner?.stopScan(object : ScanCallback() {})
        } catch (e: Exception) {}
    }

    /**
     * Transmits a mesh packet to a nearby discovered BLE device over direct GATT connection
     * using TRANSPORT_LE without requiring Wi-Fi and without requiring OS Bluetooth pairing.
     */
    fun sendBlePacket(macAddress: String, jsonPacket: String) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                return
            }
        }

        try {
            val device = bluetoothAdapter?.getRemoteDevice(macAddress) ?: return
            val packetBytes = jsonPacket.toByteArray(Charsets.UTF_8)

            // Split into chunks if exceeds 480 bytes
            val queue = ConcurrentLinkedQueue<ByteArray>()
            val maxChunk = 480
            if (packetBytes.size <= maxChunk) {
                queue.add(packetBytes)
            } else {
                val total = (packetBytes.size + maxChunk - 1) / maxChunk
                for (seq in 0 until total) {
                    val start = seq * maxChunk
                    val end = Math.min(start + maxChunk, packetBytes.size)
                    val chunkData = String(packetBytes.copyOfRange(start, end), Charsets.UTF_8)
                    val header = "CHK:$seq:$total:$chunkData"
                    queue.add(header.toByteArray(Charsets.UTF_8))
                }
            }
            outgoingQueues[macAddress] = queue

            val gattCallback = object : BluetoothGattCallback() {
                private var currentMtu = 23

                override fun onConnectionStateChange(gatt: BluetoothGatt?, status: Int, newState: Int) {
                    if (newState == BluetoothProfile.STATE_CONNECTED) {
                        Log.d(TAG, "Connected to BLE peer $macAddress via TRANSPORT_LE. Requesting MTU 512...")
                        try {
                            gatt?.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH)
                            val requested = gatt?.requestMtu(512) ?: false
                            if (!requested) {
                                gatt?.discoverServices()
                            }
                        } catch (e: Exception) {
                            gatt?.discoverServices()
                        }
                    } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                        Log.d(TAG, "Disconnected from BLE peer $macAddress")
                        outgoingQueues.remove(macAddress)
                        try { gatt?.close() } catch (e: Exception) {}
                    }
                }

                override fun onMtuChanged(gatt: BluetoothGatt?, mtu: Int, status: Int) {
                    Log.d(TAG, "BLE MTU negotiated: $mtu with $macAddress")
                    currentMtu = mtu
                    gatt?.discoverServices()
                }

                override fun onServicesDiscovered(gatt: BluetoothGatt?, status: Int) {
                    if (status == BluetoothGatt.GATT_SUCCESS) {
                        val service = gatt?.getService(SYPHER_SERVICE_UUID)
                        val characteristic = service?.getCharacteristic(SYPHER_CHAR_UUID)
                        if (characteristic != null) {
                            writeNextChunk(gatt, characteristic)
                        } else {
                            Log.w(TAG, "Sypher GATT characteristic not found on peer $macAddress")
                            gatt?.disconnect()
                        }
                    } else {
                        gatt?.disconnect()
                    }
                }

                private fun writeNextChunk(gatt: BluetoothGatt?, characteristic: BluetoothGattCharacteristic) {
                    val q = outgoingQueues[macAddress]
                    val chunk = q?.poll()
                    if (chunk != null) {
                        characteristic.value = chunk
                        characteristic.writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                        val success = gatt?.writeCharacteristic(characteristic) ?: false
                        Log.d(TAG, "Wrote chunk (${chunk.size} bytes) to $macAddress: success=$success")
                    } else {
                        Log.i(TAG, "All BLE chunks successfully transmitted to $macAddress")
                        gatt?.disconnect()
                    }
                }

                override fun onCharacteristicWrite(
                    gatt: BluetoothGatt?,
                    characteristic: BluetoothGattCharacteristic?,
                    status: Int
                ) {
                    if (status == BluetoothGatt.GATT_SUCCESS && characteristic != null) {
                        writeNextChunk(gatt, characteristic)
                    } else {
                        Log.w(TAG, "Characteristic write failed status: $status")
                        gatt?.disconnect()
                    }
                }
            }

            // Explicitly use TRANSPORT_LE to avoid BR/EDR pairing requirement!
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                device.connectGatt(this, false, gattCallback, BluetoothDevice.TRANSPORT_LE)
            } else {
                device.connectGatt(this, false, gattCallback)
            }

        } catch (t: Throwable) {
            Log.w(TAG, "Error initiating BLE connection to $macAddress: ${t.message}")
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
            .setContentText("Автономный радиоканал активен • BLE + UDP • Без серверов")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        super.onDestroy()
        instance = null
        stopMeshRadio()
        try { unregisterReceiver(bluetoothStateReceiver) } catch (e: Exception) {}
        try { gattServer?.close() } catch (t: Throwable) {}
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
