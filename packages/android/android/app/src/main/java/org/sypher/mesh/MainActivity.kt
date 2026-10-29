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
import android.os.PowerManager
import android.util.Log
import android.webkit.*
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStream
import java.net.*
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * Main Activity for Sypher Android Client.
 * Implements full dual-stack communication:
 * 1. Offline BLE Mesh (Proximity sneakernet discovery & GATT transport)
 * 2. UDP Radio (Multicast 239.255.77.88:7788 + Subnet Broadcast + Direct Unicast)
 * 3. Embedded Local HTTP Receiver (port 7789) to guarantee packet delivery even on restricted Wi-Fi routers
 * 4. Native WebChromeClient for encrypted file sharing
 */
class MainActivity : Activity() {

    companion object {
        private const val TAG = "SypherMainActivity"
        private const val PERMISSION_REQUEST_CODE = 42
        private const val FILE_CHOOSER_REQUEST_CODE = 1001

        const val MULTICAST_GROUP = "239.255.77.88"
        const val BROADCAST_ADDR = "255.255.255.255"
        const val MULTICAST_PORT = 7788
        const val HTTP_FALLBACK_PORT = 7789

        var instance: MainActivity? = null
    }

    private lateinit var webView: WebView
    private var multicastLock: WifiManager.MulticastLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var isMeshRunning = true
    private var multicastSocket: MulticastSocket? = null
    private var httpServerSocket: ServerSocket? = null
    private var filePathCallback: ValueCallback<Array<Uri>>? = null

    // Discovered BLE peer MAC mapping: peerId -> macAddress
    private val blePeerMap = ConcurrentHashMap<String, String>()
    private val lastNotifyTimes = ConcurrentHashMap<String, Long>()
    private val isDrainingSneakernet = AtomicBoolean(false)
    private val sneakernetExecutor = Executors.newSingleThreadExecutor()

    // Sneakernet store-and-forward queue for passing offline devices
    private val pendingSneakernetPackets = java.util.concurrent.ConcurrentLinkedQueue<String>()

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        instance = this

        try {
            window.decorView.setBackgroundColor(Color.parseColor("#05070A"))

            webView = WebView(this).apply {
                setBackgroundColor(Color.parseColor("#05070A"))
                settings.apply {
                    javaScriptEnabled = true
                    domStorageEnabled = true
                    databaseEnabled = true
                    allowFileAccess = true
                    allowContentAccess = true
                    cacheMode = WebSettings.LOAD_DEFAULT
                    displayZoomControls = false
                    useWideViewPort = true
                    loadWithOverviewMode = true
                }

                addJavascriptInterface(AndroidMeshBridge(this@MainActivity), "AndroidBridge")

                webViewClient = object : WebViewClient() {
                    override fun onPageFinished(view: WebView?, url: String?) {
                        Log.i(TAG, "Sypher UI loaded: $url")
                        val localIp = getLocalIpAddress()
                        view?.evaluateJavascript("if(window.onLocalIpDetected) window.onLocalIpDetected('$localIp');", null)
                    }
                }

                webChromeClient = object : WebChromeClient() {
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

            // Acquire locks to keep radio listeners active in background
            acquireLocks()

            // Start UDP Listener & Embedded HTTP server
            startUdpMulticastListener()
            startEmbeddedHttpListener()

            // Request runtime permissions and initialize BLE service
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

    private fun acquireLocks() {
        try {
            val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifiManager?.createMulticastLock("sypherMulticastLock")?.apply {
                setReferenceCounted(true)
                acquire()
            }
            val powerManager = getSystemService(Context.POWER_SERVICE) as? PowerManager
            wakeLock = powerManager?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "sypher:wakeLock")?.apply {
                acquire(10 * 60 * 1000L) // 10 minutes default
            }
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire system locks: ${e.message}")
        }
    }

    /**
     * Broadcasts outgoing packet across all transports:
     * 1. UDP Multicast (239.255.77.88:7788)
     * 2. UDP Subnet Broadcast & 255.255.255.255:7788
     * 3. BLE GATT direct to nearby passerby
     */
    fun broadcastUdpPacket(payloadJson: String) {
        thread(name = "SypherPacketBroadcaster", isDaemon = true) {
            val bytes = payloadJson.toByteArray(Charsets.UTF_8)

            // 1. Send via UDP socket
            try {
                val socket = DatagramSocket().apply { broadcast = true }

                // A. Multicast
                try {
                    val mGroup = InetAddress.getByName(MULTICAST_GROUP)
                    socket.send(DatagramPacket(bytes, bytes.size, mGroup, MULTICAST_PORT))
                } catch (e: Exception) {}

                // B. Universal Broadcast
                try {
                    val bGroup = InetAddress.getByName(BROADCAST_ADDR)
                    socket.send(DatagramPacket(bytes, bytes.size, bGroup, MULTICAST_PORT))
                } catch (e: Exception) {}

                // C. Specific Subnet Broadcasts
                try {
                    val ifaces = NetworkInterface.getNetworkInterfaces()
                    while (ifaces.hasMoreElements()) {
                        val iface = ifaces.nextElement()
                        if (iface.isLoopback || !iface.isUp) continue
                        for (addr in iface.interfaceAddresses) {
                            val bcast = addr.broadcast
                            if (bcast != null) {
                                socket.send(DatagramPacket(bytes, bytes.size, bcast, MULTICAST_PORT))
                            }
                        }
                    }
                } catch (e: Exception) {}

                socket.close()
            } catch (e: Exception) {
                Log.w(TAG, "UDP send notice: ${e.message}")
            }

            // 2. Relay via BLE GATT to all discovered nearby passerby
            // And save to sneakernet queue for passing devices
            if (pendingSneakernetPackets.size > 40) {
                pendingSneakernetPackets.poll()
            }
            pendingSneakernetPackets.add(payloadJson)

            blePeerMap.values.forEach { mac ->
                try {
                    SypherMeshService.instance?.sendBlePacket(mac, payloadJson)
                } catch (e: Exception) {}
            }
        }
    }

    private fun startUdpMulticastListener() {
        thread(name = "SypherUdpListener", isDaemon = true) {
            try {
                val group = InetAddress.getByName(MULTICAST_GROUP)
                val socket = MulticastSocket(MULTICAST_PORT).apply {
                    reuseAddress = true
                    broadcast = true
                }
                multicastSocket = socket

                // Join multicast on all active interfaces
                try {
                    val ifaces = NetworkInterface.getNetworkInterfaces()
                    while (ifaces.hasMoreElements()) {
                        val iface = ifaces.nextElement()
                        if (iface.isLoopback || !iface.isUp) continue
                        try {
                            socket.joinGroup(InetSocketAddress(group, MULTICAST_PORT), iface)
                        } catch (e: Exception) {}
                    }
                } catch (e: Exception) {
                    try { socket.joinGroup(group) } catch (ignored: Exception) {}
                }

                val buffer = ByteArray(65535)
                Log.i(TAG, "UDP listener ready on port $MULTICAST_PORT")

                while (isMeshRunning) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    socket.receive(packet)
                    val raw = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                    deliverMeshPacket(raw)
                }
            } catch (e: Exception) {
                Log.w(TAG, "UDP listener stopped: ${e.message}")
            }
        }
    }

    /**
     * Embedded HTTP server on port 7789 for fail-proof TCP message reception from PC
     */
    private fun startEmbeddedHttpListener() {
        thread(name = "SypherHttpServer", isDaemon = true) {
            try {
                httpServerSocket = ServerSocket(HTTP_FALLBACK_PORT).apply {
                    reuseAddress = true
                }
                Log.i(TAG, "Embedded HTTP receiver active on port $HTTP_FALLBACK_PORT")

                while (isMeshRunning) {
                    val client = httpServerSocket?.accept() ?: break
                    thread(isDaemon = true) {
                        handleClientHttpRequest(client)
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "Embedded HTTP receiver stopped: ${e.message}")
            }
        }
    }

    private fun handleClientHttpRequest(client: Socket) {
        try {
            val reader = BufferedReader(InputStreamReader(client.getInputStream(), Charsets.UTF_8))
            val line = reader.readLine() ?: return
            val parts = line.split(" ")
            val method = parts.getOrNull(0) ?: "GET"
            val path = parts.getOrNull(1) ?: "/"

            var contentLength = 0
            var headerLine = reader.readLine()
            while (!headerLine.isNullOrBlank()) {
                if (headerLine.startsWith("Content-Length:", ignoreCase = true)) {
                    contentLength = headerLine.substringAfter(":").trim().toIntOrNull() ?: 0
                }
                headerLine = reader.readLine()
            }

            val out: OutputStream = client.getOutputStream()

            if (method == "OPTIONS") {
                val response = "HTTP/1.1 204 No Content\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
                        "Access-Control-Allow-Headers: Content-Type\r\n\r\n"
                out.write(response.toByteArray(Charsets.UTF_8))
                out.flush()
                client.close()
                return
            }

            if (path == "/api/mesh/send" && method == "POST") {
                val bodyChars = CharArray(contentLength)
                var readTotal = 0
                while (readTotal < contentLength) {
                    val read = reader.read(bodyChars, readTotal, contentLength - readTotal)
                    if (read == -1) break
                    readTotal += read
                }
                val body = String(bodyChars, 0, readTotal)
                if (body.isNotBlank()) {
                    deliverMeshPacket(body)
                }

                val response = "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Connection: close\r\n\r\n" +
                        "{\"status\":\"ok\"}"
                out.write(response.toByteArray(Charsets.UTF_8))
                out.flush()
            } else {
                val localIp = getLocalIpAddress()
                val response = "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Connection: close\r\n\r\n" +
                        "{\"status\":\"ok\",\"ip\":\"$localIp\"}"
                out.write(response.toByteArray(Charsets.UTF_8))
                out.flush()
            }
            client.close()
        } catch (e: Exception) {
            try { client.close() } catch (ignored: Exception) {}
        }
    }

    fun deliverMeshPacket(raw: String) {
        runOnUiThread {
            if (::webView.isInitialized) {
                val jsArg = JSONObject.quote(raw)
                webView.evaluateJavascript("if(window.onMeshPacketReceived) window.onMeshPacketReceived($jsArg);", null)
            }
        }
    }

    fun notifyBlePeerDiscovered(peerId: String, rssi: Int, mac: String, name: String = "") {
        blePeerMap[peerId] = mac
        val now = System.currentTimeMillis()
        val lastTime = lastNotifyTimes[peerId] ?: 0L

        // Debounce: update UI at most once every 2.5s per peer to prevent lag and frame drops
        if (now - lastTime >= 2500L) {
            lastNotifyTimes[peerId] = now
            val distance = Math.round(Math.pow(10.0, (-59.0 - rssi) / 20.0) * 10.0) / 10.0
            runOnUiThread {
                if (::webView.isInitialized) {
                    webView.evaluateJavascript("if(window.onBlePeerDiscovered) window.onBlePeerDiscovered('$peerId', $rssi, $distance, '$mac', '$name');", null)
                }
            }
        }

        // Automatic sneakernet relay: transmit all pending packets using dedicated single-thread worker
        if (pendingSneakernetPackets.isNotEmpty() && isDrainingSneakernet.compareAndSet(false, true)) {
            sneakernetExecutor.execute {
                try {
                    while (pendingSneakernetPackets.isNotEmpty()) {
                        val queuedPacket = pendingSneakernetPackets.poll() ?: break
                        SypherMeshService.instance?.sendBlePacket(mac, queuedPacket)
                        Thread.sleep(300)
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Sneakernet drain notice: ${e.message}")
                } finally {
                    isDrainingSneakernet.set(false)
                }
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

        // Location is needed on Android for BLE proximity scanning
        if (checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            permissionsToRequest.add(android.Manifest.permission.ACCESS_FINE_LOCATION)
        }
        if (checkSelfPermission(android.Manifest.permission.ACCESS_COARSE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            permissionsToRequest.add(android.Manifest.permission.ACCESS_COARSE_LOCATION)
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

    fun startMeshServiceSafe(nodeId: String? = null) {
        try {
            val serviceIntent = Intent(this, SypherMeshService::class.java).apply {
                if (!nodeId.isNullOrBlank()) {
                    putExtra("EXTRA_NODE_ID", nodeId)
                }
            }
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
        instance = null
        isMeshRunning = false
        try { sneakernetExecutor.shutdownNow() } catch (e: Exception) {}
        try { multicastSocket?.close() } catch (e: Exception) {}
        try { httpServerSocket?.close() } catch (e: Exception) {}
        try { if (multicastLock?.isHeld == true) multicastLock?.release() } catch (e: Exception) {}
        try { if (wakeLock?.isHeld == true) wakeLock?.release() } catch (e: Exception) {}
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
        fun sendDirectBlePacket(mac: String, payloadJson: String) {
            SypherMeshService.instance?.sendBlePacket(mac, payloadJson)
        }

        @JavascriptInterface
        fun updateNodeIdentity(nodeId: String) {
            activity.startMeshServiceSafe(nodeId)
        }

        @JavascriptInterface
        fun getLocalIp(): String {
            return activity.getLocalIpAddress()
        }

        @JavascriptInterface
        fun isAvailable(): Boolean = true
    }
}
