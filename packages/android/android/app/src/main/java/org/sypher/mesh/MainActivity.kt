package org.sypher.mesh

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.webkit.JavascriptInterface
import android.webkit.JsResult
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.MulticastSocket
import kotlin.concurrent.thread

/**
 * Main Activity for Sypher Android Client.
 * Displays the hardware-accelerated dark glassmorphism messenger interface,
 * bridges JavaScript to local UDP Multicast (239.255.77.88:7788),
 * and coordinates with the background SypherMeshService BLE radio.
 */
class MainActivity : Activity() {

    companion object {
        private const val TAG = "SypherMainActivity"
        private const val PERMISSION_REQUEST_CODE = 42
        const val MULTICAST_GROUP = "239.255.77.88"
        const val MULTICAST_PORT = 7788
    }

    private lateinit var webView: WebView
    private var multicastLock: WifiManager.MulticastLock? = null
    private var isMeshRunning = true
    private var multicastSocket: MulticastSocket? = null

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        try {
            window.decorView.setBackgroundColor(Color.parseColor("#0A0D14"))

            webView = WebView(this).apply {
                setBackgroundColor(Color.parseColor("#0A0D14"))
                settings.apply {
                    javaScriptEnabled = true
                    domStorageEnabled = true
                    allowFileAccess = true
                    allowContentAccess = true
                    cacheMode = WebSettings.LOAD_DEFAULT
                    displayZoomControls = false
                    useWideViewPort = true
                    loadWithOverviewMode = true
                }

                // Expose Native Android Mesh Bridge to JavaScript
                addJavascriptInterface(AndroidMeshBridge(this@MainActivity), "AndroidBridge")

                webViewClient = object : WebViewClient() {
                    override fun onPageFinished(view: WebView?, url: String?) {
                        Log.i(TAG, "Sypher UI loaded successfully: $url")
                    }
                }

                webChromeClient = object : WebChromeClient() {
                    override fun onJsAlert(view: WebView?, url: String?, message: String?, result: JsResult?): Boolean {
                        AlertDialog.Builder(this@MainActivity, AlertDialog.THEME_DEVICE_DEFAULT_DARK)
                            .setTitle("Sypher")
                            .setMessage(message)
                            .setPositiveButton(android.R.string.ok) { _, _ -> result?.confirm() }
                            .setOnCancelListener { result?.cancel() }
                            .create()
                            .show()
                        return true
                    }

                    override fun onJsConfirm(view: WebView?, url: String?, message: String?, result: JsResult?): Boolean {
                        AlertDialog.Builder(this@MainActivity, AlertDialog.THEME_DEVICE_DEFAULT_DARK)
                            .setTitle("Sypher")
                            .setMessage(message)
                            .setPositiveButton(android.R.string.ok) { _, _ -> result?.confirm() }
                            .setNegativeButton(android.R.string.cancel) { _, _ -> result?.cancel() }
                            .setOnCancelListener { result?.cancel() }
                            .create()
                            .show()
                        return true
                    }
                }
            }

            setContentView(webView)
            webView.loadUrl("file:///android_asset/index.html")

            // Start native UDP Multicast listener for Wi-Fi / Hotspot radio communication
            startUdpMulticastListener()

            // Request runtime permissions on Android 12+ and start BLE mesh service
            checkAndRequestPermissions()

        } catch (t: Throwable) {
            Log.e(TAG, "Fatal error initializing MainActivity UI", t)
        }
    }

    fun broadcastUdpPacket(payloadJson: String) {
        thread(name = "SypherUdpSender", isDaemon = true) {
            try {
                val group = InetAddress.getByName(MULTICAST_GROUP)
                val socket = DatagramSocket()
                val bytes = payloadJson.toByteArray(Charsets.UTF_8)
                val packet = DatagramPacket(bytes, bytes.size, group, MULTICAST_PORT)
                socket.send(packet)
                socket.close()
                Log.d(TAG, "Broadcast UDP packet (${bytes.size} bytes) to $MULTICAST_GROUP:$MULTICAST_PORT")
            } catch (e: Exception) {
                Log.e(TAG, "UDP send error: ${e.message}")
            }
        }
    }

    private fun startUdpMulticastListener() {
        try {
            val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifiManager?.createMulticastLock("sypherMulticastLock")?.apply {
                setReferenceCounted(true)
                acquire()
            }
            Log.i(TAG, "MulticastLock acquired successfully")
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire MulticastLock: ${e.message}")
        }

        thread(name = "SypherUdpListener", isDaemon = true) {
            try {
                val group = InetAddress.getByName(MULTICAST_GROUP)
                val socket = MulticastSocket(MULTICAST_PORT).apply {
                    reuseAddress = true
                    joinGroup(group)
                }
                multicastSocket = socket
                val buffer = ByteArray(65535)
                Log.i(TAG, "UDP Multicast listener active on $MULTICAST_GROUP:$MULTICAST_PORT")

                while (isMeshRunning) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    socket.receive(packet)
                    val raw = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)

                    runOnUiThread {
                        if (::webView.isInitialized) {
                            val jsArg = JSONObject.quote(raw)
                            webView.evaluateJavascript("if(window.onMeshPacketReceived) window.onMeshPacketReceived($jsArg);", null)
                        }
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "UDP multicast listener stopped: ${e.message}")
            }
        }
    }

    private fun checkAndRequestPermissions() {
        val permissionsToRequest = mutableListOf<String>()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED) {
                permissionsToRequest.add(android.Manifest.permission.BLUETOOTH_SCAN)
            }
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED) {
                permissionsToRequest.add(android.Manifest.permission.BLUETOOTH_ADVERTISE)
            }
            if (checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                permissionsToRequest.add(android.Manifest.permission.BLUETOOTH_CONNECT)
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                permissionsToRequest.add(android.Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        if (permissionsToRequest.isNotEmpty()) {
            requestPermissions(permissionsToRequest.toTypedArray(), PERMISSION_REQUEST_CODE)
        } else {
            startMeshServiceSafe()
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        startMeshServiceSafe()
    }

    private fun startMeshServiceSafe() {
        try {
            val serviceIntent = Intent(this, SypherMeshService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(serviceIntent)
            } else {
                startService(serviceIntent)
            }
        } catch (t: Throwable) {
            Log.w(TAG, "Could not start background mesh service: ${t.message}")
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        isMeshRunning = false
        try {
            multicastSocket?.close()
        } catch (e: Exception) {}
        try {
            if (multicastLock?.isHeld == true) {
                multicastLock?.release()
            }
        } catch (e: Exception) {}
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (::webView.isInitialized) {
            webView.evaluateJavascript("(function(){ var l = document.getElementById('appLayout'); if(l && l.classList.contains('mobile-chat-open')) { l.classList.remove('mobile-chat-open'); return 'closed'; } return 'noop'; })()") { result ->
                if (result == null || !result.contains("closed")) {
                    if (webView.canGoBack()) {
                        webView.goBack()
                    } else {
                        super.onBackPressed()
                    }
                }
            }
        } else {
            super.onBackPressed()
        }
    }

    class AndroidMeshBridge(private val activity: MainActivity) {
        @JavascriptInterface
        fun sendMeshPacket(payloadJson: String) {
            activity.broadcastUdpPacket(payloadJson)
        }

        @JavascriptInterface
        fun isAvailable(): Boolean = true
    }
}
