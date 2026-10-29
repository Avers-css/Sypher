package org.sypher.mesh

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.webkit.JavascriptInterface
import android.webkit.JsResult
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import kotlin.concurrent.thread

/**
 * Main Activity for Sypher Android Client.
 * Displays the dark glassmorphism messenger interface,
 * bridges JavaScript to local UDP Multicast (239.255.77.88:7788) & Subnet Broadcast,
 * handles native file choosing for encrypted file sharing,
 * and coordinates with the background SypherMeshService BLE radio.
 */
class MainActivity : Activity() {

    companion object {
        private const val TAG = "SypherMainActivity"
        private const val PERMISSION_REQUEST_CODE = 42
        private const val FILE_CHOOSER_REQUEST_CODE = 1001
        const val MULTICAST_GROUP = "239.255.77.88"
        const val BROADCAST_ADDR = "255.255.255.255"
        const val MULTICAST_PORT = 7788
    }

    private lateinit var webView: WebView
    private var multicastLock: WifiManager.MulticastLock? = null
    private var isMeshRunning = true
    private var multicastSocket: MulticastSocket? = null
    private var filePathCallback: ValueCallback<Array<Uri>>? = null

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        try {
            window.decorView.setBackgroundColor(Color.parseColor("#07090E"))

            webView = WebView(this).apply {
                setBackgroundColor(Color.parseColor("#07090E"))
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
                        // Push detected local IP to web interface
                        val localIp = getLocalIpAddress()
                        view?.evaluateJavascript("if(window.onLocalIpDetected) window.onLocalIpDetected('$localIp');", null)
                    }
                }

                webChromeClient = object : WebChromeClient() {
                    // 1. Native File Chooser for Encrypted File Attachments
                    override fun onShowFileChooser(
                        mWebView: WebView?,
                        filePathCallback: ValueCallback<Array<Uri>>?,
                        fileChooserParams: FileChooserParams?
                    ): Boolean {
                        this@MainActivity.filePathCallback?.onReceiveValue(null)
                        this@MainActivity.filePathCallback = filePathCallback

                        val intent = Intent(Intent.ACTION_GET_CONTENT).apply {
                            addCategory(Intent.CATEGORY_OPENABLE)
                            type = "*/*"
                        }
                        startActivityForResult(
                            Intent.createChooser(intent, "Выберите файл для шифрования и отправки"),
                            FILE_CHOOSER_REQUEST_CODE
                        )
                        return true
                    }

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

            // Start native UDP Multicast & Broadcast listener
            startUdpMulticastListener()

            // Request runtime permissions on Android 12+ and start BLE mesh service
            checkAndRequestPermissions()

        } catch (t: Throwable) {
            Log.e(TAG, "Fatal error initializing MainActivity UI", t)
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == FILE_CHOOSER_REQUEST_CODE) {
            val results = if (resultCode == Activity.RESULT_OK && data != null) {
                val uri = data.data
                if (uri != null) arrayOf(uri) else null
            } else null
            filePathCallback?.onReceiveValue(results)
            filePathCallback = null
        }
    }

    fun broadcastUdpPacket(payloadJson: String) {
        thread(name = "SypherUdpSender", isDaemon = true) {
            try {
                val socket = DatagramSocket().apply {
                    broadcast = true
                }
                val bytes = payloadJson.toByteArray(Charsets.UTF_8)

                // 1. Send via Multicast
                try {
                    val mGroup = InetAddress.getByName(MULTICAST_GROUP)
                    val mPacket = DatagramPacket(bytes, bytes.size, mGroup, MULTICAST_PORT)
                    socket.send(mPacket)
                } catch (e: Exception) {
                    Log.w(TAG, "Multicast send error: ${e.message}")
                }

                // 2. Send via Subnet Broadcast (bypasses router IGMP/multicast filters)
                try {
                    val bGroup = InetAddress.getByName(BROADCAST_ADDR)
                    val bPacket = DatagramPacket(bytes, bytes.size, bGroup, MULTICAST_PORT)
                    socket.send(bPacket)
                } catch (e: Exception) {
                    Log.w(TAG, "Broadcast send error: ${e.message}")
                }

                socket.close()
                Log.d(TAG, "Transmitted packet (${bytes.size} bytes) to Mesh")
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
            Log.i(TAG, "MulticastLock acquired")
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire MulticastLock: ${e.message}")
        }

        thread(name = "SypherUdpListener", isDaemon = true) {
            try {
                val group = InetAddress.getByName(MULTICAST_GROUP)
                val socket = MulticastSocket(MULTICAST_PORT).apply {
                    reuseAddress = true
                    broadcast = true
                    joinGroup(group)
                }
                multicastSocket = socket
                val buffer = ByteArray(65535)
                Log.i(TAG, "UDP radio listener active on port $MULTICAST_PORT")

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
                Log.w(TAG, "UDP listener stopped: ${e.message}")
            }
        }
    }

    fun getLocalIpAddress(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val iface = interfaces.nextElement()
                if (iface.isLoopback || !iface.isUp) continue
                val addresses = iface.inetAddresses
                while (addresses.hasMoreElements()) {
                    val addr = addresses.nextElement()
                    if (addr is Inet4Address && !addr.isLoopbackAddress) {
                        return addr.hostAddress ?: ""
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Error obtaining local IP: ${e.message}")
        }
        return "127.0.0.1"
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
        try { multicastSocket?.close() } catch (e: Exception) {}
        try {
            if (multicastLock?.isHeld == true) multicastLock?.release()
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
        fun getLocalIp(): String {
            return activity.getLocalIpAddress()
        }

        @JavascriptInterface
        fun isAvailable(): Boolean = true
    }
}
