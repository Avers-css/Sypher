package org.sypher.mesh

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
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
 * Sypher Foreground Mesh Service for Android.
 * Runs in the background to maintain BLE scanning and advertising even when screen is off,
 * facilitating store-and-forward sneakernet relaying between passing phones.
 */
class SypherMeshService : Service() {

    companion object {
        const val TAG = "SypherMeshService"
        const val CHANNEL_ID = "sypher_mesh_channel"
        const val NOTIFICATION_ID = 1337
        val SYPHER_SERVICE_UUID: UUID = UUID.fromString("00005950-0000-1000-8000-00805f9b34fb")
    }

    private var bluetoothAdapter: BluetoothAdapter? = null
    private var bleScanner: BluetoothLeScanner? = null
    private var bleAdvertiser: BluetoothLeAdvertiser? = null

    override fun onCreate() {
        super.onCreate()
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
        } catch (t: Throwable) {
            Log.e(TAG, "Error in service onCreate", t)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
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

    private fun startMeshRadio() {
        if (bluetoothAdapter == null || bluetoothAdapter?.isEnabled != true) {
            Log.i(TAG, "Bluetooth is disabled or unavailable. Mesh waiting for radio activation.")
            return
        }

        // Check runtime permission on Android 12+ (API 31+)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED ||
                checkSelfPermission(android.Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED) {
                Log.w(TAG, "Bluetooth permissions not yet granted by user.")
                return
            }
        }

        // 1. Start Low-Power BLE Advertising
        try {
            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_LOW)
                .setConnectable(true)
                .build()

            val data = AdvertiseData.Builder()
                .setIncludeDeviceName(false)
                .addServiceUuid(ParcelUuid(SYPHER_SERVICE_UUID))
                .build()

            bleAdvertiser?.startAdvertising(settings, data, object : AdvertiseCallback() {
                override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
                    Log.i(TAG, "BLE Mesh beacon broadcasting successfully")
                }
                override fun onStartFailure(errorCode: Int) {
                    Log.w(TAG, "BLE advertising failure: $errorCode")
                }
            })
        } catch (e: SecurityException) {
            Log.w(TAG, "SecurityException starting BLE advertiser: ${e.message}")
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE advertiser: ${t.message}")
        }

        // 2. Start BLE Scanning
        try {
            val scanSettings = ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_LOW_POWER)
                .build()

            val scanFilter = ScanFilter.Builder()
                .setServiceUuid(ParcelUuid(SYPHER_SERVICE_UUID))
                .build()

            bleScanner?.startScan(listOf(scanFilter), scanSettings, object : ScanCallback() {
                override fun onScanResult(callbackType: Int, result: ScanResult?) {
                    result?.let {
                        Log.d(TAG, "Discovered Sypher Mesh Peer: ${it.device.address}")
                    }
                }
            })
        } catch (e: SecurityException) {
            Log.w(TAG, "SecurityException starting BLE scanner: ${e.message}")
        } catch (t: Throwable) {
            Log.w(TAG, "Error starting BLE scanner: ${t.message}")
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
            .setContentText("Local peer-to-peer radio active • 0 servers")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        super.onDestroy()
        try {
            bleAdvertiser?.stopAdvertising(object : AdvertiseCallback() {})
            bleScanner?.stopScan(object : ScanCallback() {})
        } catch (t: Throwable) {
            Log.w(TAG, "Error stopping radio in onDestroy: ${t.message}")
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
