using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SypherDesktop
{
    static class Program
    {
        private static HttpListener listener;
        private static NotifyIcon trayIcon;
        private static int port = 7789;

        private const string MulticastGroup = "239.255.77.88";
        private const string BroadcastAddr = "255.255.255.255";
        private const int MulticastPort = 7788;
        private static readonly Guid SypherUuid = new Guid("00005950-0000-1000-8000-00805f9b34fb");
        private static readonly Guid SypherCharUuid = new Guid("00005951-0000-1000-8000-00805f9b34fb");

        private static UdpClient udpSender;
        private static UdpClient udpReceiver;
        private static readonly List<HttpListenerResponse> sseClients = new List<HttpListenerResponse>();
        private static readonly HashSet<string> knownPeers = new HashSet<string>();
        private static readonly Dictionary<string, ulong> discoveredBlePeers = new Dictionary<string, ulong>();
        private static readonly object lockObj = new object();
        private static object bleGattProvider;
        private static object blePublisher;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            StartUdpMulticast();
            StartBleGattServer();
            StartBlePublisher();
            StartBleWatcher();
            StartHttpServer();
            SetupTrayIcon();
            LaunchAppWindow();

            Application.Run();
        }

        private static object AwaitAsyncOp(object asyncOp, Type resultType, int timeoutMs = 3000)
        {
            try
            {
                Type extType = Type.GetType("System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                if (extType == null) return null;
                MethodInfo asTask = null;
                foreach (var m in extType.GetMethods())
                {
                    if (m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        asTask = m;
                        break;
                    }
                }
                if (asTask == null) return null;
                var genericMethod = asTask.MakeGenericMethod(resultType);
                var task = genericMethod.Invoke(null, new object[] { asyncOp }) as Task;
                if (task == null) return null;
                task.Wait(timeoutMs);
                var prop = task.GetType().GetProperty("Result");
                return prop != null ? prop.GetValue(task, null) : null;
            }
            catch
            {
                return null;
            }
        }

        static void StartBleGattServer()
        {
            ThreadPool.QueueUserWorkItem((_) =>
            {
                try
                {
                    Type provType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    if (provType == null) return;

                    MethodInfo createMethod = provType.GetMethod("CreateAsync", new Type[] { typeof(Guid) });
                    object asyncOp = createMethod.Invoke(null, new object[] { SypherUuid });

                    Type resType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    object res = AwaitAsyncOp(asyncOp, resType);
                    if (res == null) return;

                    bleGattProvider = resType.GetProperty("ServiceProvider").GetValue(res, null);
                    if (bleGattProvider == null) return;

                    // Characteristic parameters
                    Type charParamsType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicParameters, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    object charParams = Activator.CreateInstance(charParamsType);

                    Type propType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    charParamsType.GetProperty("CharacteristicProperties").SetValue(charParams, Enum.ToObject(propType, 14), null); // Write | WriteWithoutResponse | Read

                    Type protType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattProtectionLevel, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    charParamsType.GetProperty("WriteProtectionLevel").SetValue(charParams, Enum.ToObject(protType, 0), null); // Plain

                    object service = bleGattProvider.GetType().GetProperty("Service").GetValue(bleGattProvider, null);
                    MethodInfo createChar = service.GetType().GetMethod("CreateCharacteristicAsync", new Type[] { typeof(Guid), charParamsType });
                    object charAsyncOp = createChar.Invoke(service, new object[] { SypherCharUuid, charParams });

                    Type charResType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicResult, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    object charRes = AwaitAsyncOp(charAsyncOp, charResType);
                    if (charRes == null) return;

                    object ch = charResType.GetProperty("Characteristic").GetValue(charRes, null);
                    if (ch == null) return;

                    // Hook WriteRequested
                    EventInfo writeEvent = ch.GetType().GetEvent("WriteRequested");
                    MethodInfo onWriteMethod = typeof(Program).GetMethod("OnGattWriteRequested", BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate writeDelegate = Delegate.CreateDelegate(writeEvent.EventHandlerType, onWriteMethod);
                    writeEvent.GetAddMethod().Invoke(ch, new object[] { writeDelegate });

                    // Start Advertising GATT service
                    Type advParamsType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    object advParams = Activator.CreateInstance(advParamsType);
                    advParamsType.GetProperty("IsConnectable").SetValue(advParams, true, null);
                    advParamsType.GetProperty("IsDiscoverable").SetValue(advParams, true, null);

                    MethodInfo startAdv = bleGattProvider.GetType().GetMethod("StartAdvertising", new Type[] { advParamsType });
                    startAdv.Invoke(bleGattProvider, new object[] { advParams });
                }
                catch { }
            });
        }

        private static void OnGattWriteRequested(object sender, object args)
        {
            try
            {
                Type argsType = args.GetType();
                MethodInfo getReq = argsType.GetMethod("GetRequestAsync");
                object asyncOp = getReq.Invoke(args, null);

                Type reqType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteRequest, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                object req = AwaitAsyncOp(asyncOp, reqType, 1000);
                if (req == null) return;

                object buf = reqType.GetProperty("Value").GetValue(req, null);
                MethodInfo toArray = typeof(WindowsRuntimeBufferExtensions).GetMethod("ToArray", BindingFlags.Public | BindingFlags.Static);
                byte[] bytes = (byte[])toArray.Invoke(null, new object[] { buf });
                string text = Encoding.UTF8.GetString(bytes);

                // Dispatch to UI clients
                lock (lockObj)
                {
                    byte[] eventBytes = Encoding.UTF8.GetBytes("data: " + text + "\n\n");
                    for (int i = sseClients.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            sseClients[i].OutputStream.Write(eventBytes, 0, eventBytes.Length);
                            sseClients[i].OutputStream.Flush();
                        }
                        catch
                        {
                            sseClients.RemoveAt(i);
                        }
                    }
                }

                // Respond Success
                MethodInfo respond = reqType.GetMethod("Respond");
                respond.Invoke(req, null);
            }
            catch { }
        }

        static void StartBlePublisher()
        {
            ThreadPool.QueueUserWorkItem((_) =>
            {
                try
                {
                    Type pubType = Type.GetType("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    Type mfgType = Type.GetType("Windows.Devices.Bluetooth.Advertisement.BluetoothLEManufacturerData, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                    if (pubType == null || mfgType == null) return;

                    blePublisher = Activator.CreateInstance(pubType);
                    object ad = pubType.GetProperty("Advertisement").GetValue(blePublisher, null);
                    object mfgList = ad.GetType().GetProperty("ManufacturerData").GetValue(ad, null);

                    object mfg = Activator.CreateInstance(mfgType);
                    mfgType.GetProperty("CompanyId").SetValue(mfg, (ushort)0x5950, null);

                    byte[] payload = Encoding.UTF8.GetBytes("SYP_WIN");
                    MethodInfo asBuffer = typeof(WindowsRuntimeBufferExtensions).GetMethod(
                        "AsBuffer",
                        new Type[] { typeof(byte[]), typeof(int), typeof(int) }
                    );
                    object buffer = asBuffer.Invoke(null, new object[] { payload, 0, payload.Length });
                    mfgType.GetProperty("Data").SetValue(mfg, buffer, null);

                    Type colType = typeof(ICollection<>).MakeGenericType(mfgType);
                    MethodInfo addMethod = colType.GetMethod("Add");
                    addMethod.Invoke(mfgList, new object[] { mfg });

                    MethodInfo startMethod = pubType.GetMethod("Start");
                    startMethod.Invoke(blePublisher, null);
                }
                catch { }
            });
        }

        static void StartBleWatcher()
        {
            try
            {
                Type watcherType = Type.GetType("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                if (watcherType == null) return;

                object watcher = Activator.CreateInstance(watcherType);

                EventInfo receivedEvent = watcherType.GetEvent("Received");
                MethodInfo handlerMethod = typeof(Program).GetMethod("OnBleAdvertisementReceived", BindingFlags.Static | BindingFlags.NonPublic);
                Delegate handler = Delegate.CreateDelegate(receivedEvent.EventHandlerType, handlerMethod);

                MethodInfo addMethod = receivedEvent.GetAddMethod();
                addMethod.Invoke(watcher, new object[] { handler });

                MethodInfo startMethod = watcherType.GetMethod("Start");
                startMethod.Invoke(watcher, null);
            }
            catch { }
        }

        private static void OnBleAdvertisementReceived(object sender, object args)
        {
            try
            {
                Type argsType = args.GetType();
                PropertyInfo rssiProp = argsType.GetProperty("RawSignalStrengthInDBm");
                PropertyInfo addrProp = argsType.GetProperty("BluetoothAddress");
                PropertyInfo adProp = argsType.GetProperty("Advertisement");

                short rssi = (short)rssiProp.GetValue(args, null);
                ulong rawAddr = (ulong)addrProp.GetValue(args, null);

                byte[] bytes = BitConverter.GetBytes(rawAddr);
                string mac = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                    bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0]);

                object ad = adProp.GetValue(args, null);
                PropertyInfo uuidsProp = ad.GetType().GetProperty("ServiceUuids");
                IEnumerable uuids = uuidsProp.GetValue(ad, null) as IEnumerable;

                bool isSypher = false;
                if (uuids != null)
                {
                    foreach (var u in uuids)
                    {
                        if (u is Guid && ((Guid)u) == SypherUuid) { isSypher = true; break; }
                        if (u != null && u.ToString().IndexOf("5950", StringComparison.OrdinalIgnoreCase) >= 0) { isSypher = true; break; }
                    }
                }

                if (!isSypher)
                {
                    PropertyInfo mfgProp = ad.GetType().GetProperty("ManufacturerData");
                    IEnumerable mfgs = mfgProp.GetValue(ad, null) as IEnumerable;
                    if (mfgs != null)
                    {
                        foreach (var m in mfgs)
                        {
                            PropertyInfo cidProp = m.GetType().GetProperty("CompanyId");
                            ushort cid = (ushort)cidProp.GetValue(m, null);
                            if (cid == 0x5950) { isSypher = true; break; }
                        }
                    }
                }

                if (isSypher)
                {
                    lock (discoveredBlePeers)
                    {
                        discoveredBlePeers[mac] = rawAddr;
                    }

                    string shortId = mac.Replace(":", "").ToLower();
                    if (shortId.Length > 6) shortId = shortId.Substring(shortId.Length - 6);
                    string peerId = "syp-droid-" + shortId;

                    double distance = Math.Round(Math.Pow(10.0, (-59.0 - (double)rssi) / 20.0), 1);

                    string sseJson = "data: {\"type\":\"BLE_PEER_DISCOVERED\",\"peerId\":\"" + peerId +
                        "\",\"mac\":\"" + mac +
                        "\",\"rssi\":" + rssi +
                        ",\"distance\":" + distance.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ",\"transport\":\"BLE\"}\n\n";

                    lock (lockObj)
                    {
                        byte[] eventBytes = Encoding.UTF8.GetBytes(sseJson);
                        for (int i = sseClients.Count - 1; i >= 0; i--)
                        {
                            try
                            {
                                sseClients[i].OutputStream.Write(eventBytes, 0, eventBytes.Length);
                                sseClients[i].OutputStream.Flush();
                            }
                            catch
                            {
                                sseClients.RemoveAt(i);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        static void BroadcastBle(string rawPacket)
        {
            ThreadPool.QueueUserWorkItem((_) =>
            {
                try
                {
                    List<ulong> targets;
                    lock (discoveredBlePeers)
                    {
                        targets = new List<ulong>(discoveredBlePeers.Values);
                    }

                    byte[] data = Encoding.UTF8.GetBytes(rawPacket);

                    foreach (ulong bleAddress in targets)
                    {
                        try
                        {
                            Type devType = Type.GetType("Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                            if (devType == null) continue;

                            MethodInfo fromAddr = devType.GetMethod("FromBluetoothAddressAsync", new Type[] { typeof(ulong) });
                            object devAsyncOp = fromAddr.Invoke(null, new object[] { bleAddress });
                            object dev = AwaitAsyncOp(devAsyncOp, devType, 2000);
                            if (dev == null) continue;

                            MethodInfo getServices = devType.GetMethod("GetGattServicesForUuidAsync", new Type[] { typeof(Guid) });
                            object servOp = getServices.Invoke(dev, new object[] { SypherUuid });
                            Type servResType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                            object servRes = AwaitAsyncOp(servOp, servResType, 2000);
                            if (servRes == null) continue;

                            IList services = servResType.GetProperty("Services").GetValue(servRes, null) as IList;
                            if (services == null || services.Count == 0) continue;
                            object service = services[0];

                            MethodInfo getChars = service.GetType().GetMethod("GetCharacteristicsForUuidAsync", new Type[] { typeof(Guid) });
                            object charOp = getChars.Invoke(service, new object[] { SypherCharUuid });
                            Type charResType = Type.GetType("Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                            object charRes = AwaitAsyncOp(charOp, charResType, 2000);
                            if (charRes == null) continue;

                            IList chars = charResType.GetProperty("Characteristics").GetValue(charRes, null) as IList;
                            if (chars == null || chars.Count == 0) continue;
                            object ch = chars[0];

                            MethodInfo asBuffer = typeof(WindowsRuntimeBufferExtensions).GetMethod(
                                "AsBuffer",
                                new Type[] { typeof(byte[]), typeof(int), typeof(int) }
                            );
                            object buffer = asBuffer.Invoke(null, new object[] { data, 0, data.Length });

                            MethodInfo writeVal = null;
                            foreach (var m in ch.GetType().GetMethods())
                            {
                                if ((m.Name == "WriteValueWithResultAsync" || m.Name == "WriteValueAsync") && m.GetParameters().Length == 1)
                                {
                                    writeVal = m;
                                    break;
                                }
                            }
                            if (writeVal != null)
                            {
                                writeVal.Invoke(ch, new object[] { buffer });
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        static void StartUdpMulticast()
        {
            try
            {
                udpSender = new UdpClient();
                udpSender.EnableBroadcast = true;
                try { udpSender.JoinMulticastGroup(IPAddress.Parse(MulticastGroup)); } catch { }

                udpReceiver = new UdpClient();
                udpReceiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpReceiver.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                try { udpReceiver.JoinMulticastGroup(IPAddress.Parse(MulticastGroup)); } catch { }

                Thread rxThread = new Thread(() =>
                {
                    IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        try
                        {
                            byte[] bytes = udpReceiver.Receive(ref remoteEp);
                            string raw = Encoding.UTF8.GetString(bytes);

                            if (remoteEp != null && remoteEp.Address != null && !IPAddress.IsLoopback(remoteEp.Address))
                            {
                                lock (knownPeers)
                                {
                                    knownPeers.Add(remoteEp.Address.ToString());
                                }
                            }

                            lock (lockObj)
                            {
                                byte[] eventBytes = Encoding.UTF8.GetBytes("data: " + raw + "\n\n");
                                for (int i = sseClients.Count - 1; i >= 0; i--)
                                {
                                    try
                                    {
                                        sseClients[i].OutputStream.Write(eventBytes, 0, eventBytes.Length);
                                        sseClients[i].OutputStream.Flush();
                                    }
                                    catch
                                    {
                                        sseClients.RemoveAt(i);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                });
                rxThread.IsBackground = true;
                rxThread.Start();
            }
            catch { }
        }

        static void BroadcastUdp(string raw)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(raw);

                if (udpSender != null)
                {
                    // 1. Multicast
                    try { udpSender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(MulticastGroup), MulticastPort)); } catch { }
                    // 2. Broadcast
                    try { udpSender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(BroadcastAddr), MulticastPort)); } catch { }

                    // 3. Subnet Broadcast on all active network adapters
                    try
                    {
                        foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                            {
                                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation uip in ni.GetIPProperties().UnicastAddresses)
                                {
                                    if (uip.Address.AddressFamily == AddressFamily.InterNetwork && uip.IPv4Mask != null)
                                    {
                                        byte[] ipBytes = uip.Address.GetAddressBytes();
                                        byte[] maskBytes = uip.IPv4Mask.GetAddressBytes();
                                        byte[] bcastBytes = new byte[4];
                                        for (int i = 0; i < 4; i++) bcastBytes[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));
                                        IPAddress bcastIp = new IPAddress(bcastBytes);
                                        try { udpSender.Send(bytes, bytes.Length, new IPEndPoint(bcastIp, MulticastPort)); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // 4. Direct Unicast UDP and HTTP POST TCP fallback to known peer IPs
                    List<string> peersCopy;
                    lock (knownPeers) { peersCopy = new List<string>(knownPeers); }
                    foreach (string peerIpStr in peersCopy)
                    {
                        try
                        {
                            IPAddress peerIp = IPAddress.Parse(peerIpStr);
                            udpSender.Send(bytes, bytes.Length, new IPEndPoint(peerIp, MulticastPort));
                        }
                        catch { }

                        ThreadPool.QueueUserWorkItem((_) =>
                        {
                            int[] ports = new int[] { 7789, 7788 };
                            foreach (int pt in ports)
                            {
                                try
                                {
                                    var httpReq = (HttpWebRequest)WebRequest.Create("http://" + peerIpStr + ":" + pt + "/api/mesh/send");
                                    httpReq.Method = "POST";
                                    httpReq.ContentType = "application/json";
                                    httpReq.Timeout = 1200;
                                    httpReq.ContentLength = bytes.Length;
                                    using (var stream = httpReq.GetRequestStream())
                                    {
                                        stream.Write(bytes, 0, bytes.Length);
                                    }
                                    using (var resp = httpReq.GetResponse()) { }
                                }
                                catch { }
                            }
                        });
                    }
                }
            }
            catch { }
        }

        static string GetLocalIp()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        foreach (System.Net.NetworkInformation.UnicastIPAddressInformation uip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (uip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uip.Address))
                            {
                                return uip.Address.ToString();
                            }
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        static void StartHttpServer()
        {
            for (int p = 7780; p <= 7820; p++)
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + p + "/");
                    listener.Start();
                    port = p;
                    break;
                }
                catch
                {
                    if (listener != null)
                    {
                        try { listener.Close(); } catch { }
                        listener = null;
                    }
                }
            }

            if (listener == null) return;

            Thread serverThread = new Thread(() =>
            {
                while (listener != null && listener.IsListening)
                {
                    try
                    {
                        var context = listener.GetContext();
                        ThreadPool.QueueUserWorkItem((_) => ProcessRequest(context));
                    }
                    catch { }
                }
            });
            serverThread.IsBackground = true;
            serverThread.Start();
        }

        static void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath;

                if (path == "/api/mesh/events")
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.Add("Cache-Control", "no-cache");
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    byte[] ping = Encoding.UTF8.GetBytes(": ping\n\n");
                    context.Response.OutputStream.Write(ping, 0, ping.Length);
                    context.Response.OutputStream.Flush();

                    lock (lockObj)
                    {
                        sseClients.Add(context.Response);
                    }
                    return;
                }

                if (path == "/api/mesh/ip")
                {
                    byte[] ipBytes = Encoding.UTF8.GetBytes("{\"ip\":\"" + GetLocalIp() + "\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    context.Response.OutputStream.Write(ipBytes, 0, ipBytes.Length);
                    context.Response.OutputStream.Close();
                    return;
                }

                if (path == "/api/mesh/send" && context.Request.HttpMethod == "POST")
                {
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        BroadcastUdp(body);
                        BroadcastBle(body);
                    }
                    byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    context.Response.OutputStream.Write(ok, 0, ok.Length);
                    context.Response.OutputStream.Close();
                    return;
                }

                byte[] responseBytes;
                string mime = "text/html; charset=utf-8";

                if (path.EndsWith("styles.css"))
                {
                    mime = "text/css; charset=utf-8";
                    responseBytes = Encoding.UTF8.GetBytes(EmbeddedAssets.CSS);
                }
                else if (path.EndsWith("app.js"))
                {
                    mime = "application/javascript; charset=utf-8";
                    responseBytes = Encoding.UTF8.GetBytes(EmbeddedAssets.JS);
                }
                else
                {
                    responseBytes = Encoding.UTF8.GetBytes(EmbeddedAssets.HTML);
                }

                context.Response.ContentType = mime;
                context.Response.ContentLength64 = responseBytes.Length;
                context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
                context.Response.OutputStream.Close();
            }
            catch { }
        }

        static void LaunchAppWindow()
        {
            string url = "http://127.0.0.1:" + port + "/";
            string[] possibleBrowsers = new string[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"
            };

            string browserPath = null;
            foreach (var b in possibleBrowsers)
            {
                if (File.Exists(b)) { browserPath = b; break; }
            }

            if (!string.IsNullOrEmpty(browserPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = "--app=" + url + " --window-size=1200,800",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }

        static void SetupTrayIcon()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Sypher - Mesh Active";
            trayIcon.Icon = SystemIcons.Shield;
            trayIcon.Visible = true;

            var menu = new ContextMenu();
            menu.MenuItems.Add("Открыть Sypher", (s, e) => LaunchAppWindow());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Экстренное уничтожение данных", (s, e) =>
            {
                MessageBox.Show("Все ключи шифрования и данные памяти стерты.", "Sypher Emergency Burn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ExitApp();
            });
            menu.MenuItems.Add("Выход", (s, e) => ExitApp());

            trayIcon.ContextMenu = menu;
        }

        static void ExitApp()
        {
            if (trayIcon != null) trayIcon.Visible = false;
            if (listener != null) listener.Stop();
            Application.Exit();
            Environment.Exit(0);
        }
    }

    public static class EmbeddedAssets
    {
        private static string _html;
        private static string _css;
        private static string _js;

        public static string HTML
        {
            get
            {
                if (_html == null)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"packages\desktop\src\renderer\index.html");
                    if (File.Exists(path)) _html = File.ReadAllText(path, Encoding.UTF8);
                    else _html = Encoding.UTF8.GetString(Convert.FromBase64String("PCFET0NUWVBFIGh0bWw+CjxodG1sIGxhbmc9InJ1Ij4KPGhlYWQ+CiAgPG1ldGEgY2hhcnNldD0iVVRGLTgiPgogIDxtZXRhIG5hbWU9InZpZXdwb3J0IiBjb250ZW50PSJ3aWR0aD1kZXZpY2Utd2lkdGgsIGluaXRpYWwtc2NhbGU9MS4wLCBtYXhpbXVtLXNjYWxlPTEuMCwgdXNlci1zY2FsYWJsZT1ubywgdmlld3BvcnQtZml0PWNvdmVyIj4KICA8dGl0bGU+U3lwaGVyIOKAlCDQkNCy0YLQvtC90L7QvNC90YvQuSBQMlAgTWVzaCDQnNC10YHRgdC10L3QtNC20LXRgDwvdGl0bGU+CiAgPGxpbmsgcmVsPSJzdHlsZXNoZWV0IiBocmVmPSJzdHlsZXMuY3NzIj4KICA8bGluayByZWw9InByZWNvbm5lY3QiIGhyZWY9Imh0dHBzOi8vZm9udHMuZ29vZ2xlYXBpcy5jb20iPgogIDxsaW5rIHJlbD0icHJlY29ubmVjdCIgaHJlZj0iaHR0cHM6Ly9mb250cy5nc3RhdGljLmNvbSIgY3Jvc3NvcmlnaW4+CiAgPGxpbmsgaHJlZj0iaHR0cHM6Ly9mb250cy5nb29nbGVhcGlzLmNvbS9jc3MyP2ZhbWlseT1KZXRCcmFpbnMrTW9ubzp3Z2h0QDQwMDs1MDA7NjAwJmZhbWlseT1QbHVzK0pha2FydGErU2Fuczp3Z2h0QDQwMDs1MDA7NjAwOzcwMCZkaXNwbGF5PXN3YXAiIHJlbD0ic3R5bGVzaGVldCI+CjwvaGVhZD4KPGJvZHk+CiAgPGRpdiBjbGFzcz0iYXBwLWxheW91dCIgaWQ9ImFwcExheW91dCI+CiAgICA8IS0tINCR0J7QmtCe0JLQkNCvINCf0JDQndCV0JvQrDog0J/RgNC+0YTQuNC70YwsINCg0LDQtNCw0YAg0Lgg0JTQuNCw0LvQvtCz0LggLS0+CiAgICA8YXNpZGUgY2xhc3M9InNpZGViYXIiIGlkPSJzaWRlYmFyIj4KICAgICAgPGhlYWRlciBjbGFzcz0ic2lkZWJhci1oZWFkZXIiPgogICAgICAgIDxkaXYgY2xhc3M9ImJyYW5kLWJhciI+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJicmFuZC1pZGVudGl0eSI+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9ImJyYW5kLWljb24tYm94Ij4KICAgICAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjIwIiBoZWlnaHQ9IjIwIiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiPgogICAgICAgICAgICAgICAgPHBhdGggZD0iTTE3LjUgNUg2LjVWMTFIMTcuNVYxOUg2LjUiIHN0cm9rZT0iI0ZGRkZGRiIgc3Ryb2tlLXdpZHRoPSIyLjYiIHN0cm9rZS1saW5lY2FwPSJzcXVhcmUiIHN0cm9rZS1saW5lam9pbj0ibWl0ZXIiLz4KICAgICAgICAgICAgICA8L3N2Zz4KICAgICAgICAgICAgPC9kaXY+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9ImJyYW5kLXRpdGxlLXdyYXAiPgogICAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJicmFuZC1uYW1lIj5TWVBIRVI8L3NwYW4+CiAgICAgICAgICAgICAgPHNwYW4gY2xhc3M9ImJyYW5kLWJhZGdlIj5PRkZMSU5FIE1FU0g8L3NwYW4+CiAgICAgICAgICAgIDwvZGl2PgogICAgICAgICAgPC9kaXY+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJoZWFkZXItYWN0aW9uLWJ1dHRvbnMiPgogICAgICAgICAgICA8YnV0dG9uIGNsYXNzPSJuYXYtaWNvbi1idG4iIGlkPSJidG5OZXdDaGF0IiB0aXRsZT0i0J3QvtCy0YvQuSDQtNC40LDQu9C+0LMg0L/QviBTeXBoZXIgSUQiIGFyaWEtbGFiZWw9ItCd0L7QstGL0Lkg0LTQuNCw0LvQvtCzIj4KICAgICAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjE4IiBoZWlnaHQ9IjE4IiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIuMiI+CiAgICAgICAgICAgICAgICA8bGluZSB4MT0iMTIiIHkxPSI1IiB4Mj0iMTIiIHkyPSIxOSIvPgogICAgICAgICAgICAgICAgPGxpbmUgeDE9IjUiIHkxPSIxMiIgeDI9IjE5IiB5Mj0iMTIiLz4KICAgICAgICAgICAgICA8L3N2Zz4KICAgICAgICAgICAgPC9idXR0b24+CiAgICAgICAgICAgIDxidXR0b24gY2xhc3M9Im5hdi1pY29uLWJ0biIgaWQ9ImJ0blNldHRpbmdzIiB0aXRsZT0i0J3QsNGB0YLRgNC+0LnQutC4INGD0LfQu9CwIiBhcmlhLWxhYmVsPSLQndCw0YHRgtGA0L7QudC60LgiPgogICAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTgiIGhlaWdodD0iMTgiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgICAgICA8Y2lyY2xlIGN4PSIxMiIgY3k9IjEyIiByPSIzIi8+CiAgICAgICAgICAgICAgICA8cGF0aCBkPSJNMTkuNCAxNWExLjY1IDEuNjUgMCAwIDAgLjMzIDEuODJsLjA2LjA2YTIgMiAwIDAgMSAwIDIuODMgMiAyIDAgMCAxLTIuODMgMGwtLjA2LS4wNmExLjY1IDEuNjUgMCAwIDAtMS44Mi0uMzMgMS42NSAxLjY1IDAgMCAwLTEgMS41MVYyMWEyIDIgMCAwIDEtMiAyIDIgMiAwIDAgMS0yLTJ2LS4wOUExLjY1IDEuNjUgMCAwIDAgOSAxOS40YTEuNjUgMS42NSAwIDAgMC0xLjgyLjMzbC0uMDYuMDZhMiAyIDAgMCAxLTIuODMgMCAyIDIgMCAwIDEgMC0yLjgzbC4wNi0uMDZhMS42NSAxLjY1IDAgMCAwIC4zMy0xLjgyIDEuNjUgMS42NSAwIDAgMC0xLjUxLTFIM2EyIDIgMCAwIDEtMi0yIDIgMiAwIDAgMSAyLTJoLjA5QTEuNjUgMS42NSAwIDAgMCA0LjYgOWExLjY1IDEuNjUgMCAwIDAtLjMzLTEuODJsLS4wNi0uMDZhMiAyIDAgMCAxIDAtMi44MyAyIDIgMCAwIDEgMi44MyAwbC4wNi4wNmExLjY1IDEuNjUgMCAwIDAgMS44Mi4zM0g5YTEuNjUgMS42NSAwIDAgMCAxLTEuNTFWM2EyIDIgMCAwIDEgMi0yIDIgMiAwIDAgMSAyIDJ2LjA5YTEuNjUgMS42NSAwIDAgMCAxIDEuNTEgMS42NSAxLjY1IDAgMCAwIDEuODItLjMzbC4wNi0uMDZhMiAyIDAgMCAxIDIuODMgMCAyIDIgMCAwIDEgMCAyLjgzbC0uMDYuMDZhMS42NSAxLjY1IDAgMCAwLS4zMyAxLjgyVjlhMS42NSAxLjY1IDAgMCAwIDEuNTEgMUgyMWEyIDIgMCAwIDEgMiAyIDIgMiAwIDAgMS0yIDJoLS4wOWExLjY1IDEuNjUgMCAwIDAtMS41MSAxeiIvPgogICAgICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgICA8L2J1dHRvbj4KICAgICAgICAgIDwvZGl2PgogICAgICAgIDwvZGl2PgoKICAgICAgICA8IS0tINCa0LDRgNGC0L7Rh9C60LAg0YPQvdC40LrQsNC70YzQvdC+0LPQviDRg9C30LvQsCAtLT4KICAgICAgICA8ZGl2IGNsYXNzPSJteS1ub2RlLWNhcmQiIGlkPSJteU5vZGVDYXJkIiB0aXRsZT0i0J3QsNC20LzQuNGC0LUsINGH0YLQvtCx0Ysg0YHQutC+0L/QuNGA0L7QstCw0YLRjCDQstCw0Ygg0YPQvdC40LrQsNC70YzQvdGL0LkgSUQiPgogICAgICAgICAgPGRpdiBjbGFzcz0ibm9kZS1hdmF0YXIiIGlkPSJteU5vZGVBdmF0YXIiPlU8L2Rpdj4KICAgICAgICAgIDxkaXYgY2xhc3M9Im5vZGUtaW5mbyI+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9Im5vZGUtbmFtZS1yb3ciPgogICAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJub2RlLW5hbWUiIGlkPSJteU5vZGVEaXNwbGF5TmFtZSI+0JzQvtC5INGD0LfQtdC7PC9zcGFuPgogICAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJzdGF0dXMtcHVsc2UtZG90IiB0aXRsZT0i0KDQsNC00LjQvtC60LDQvdCw0Lsg0LDQutGC0LjQstC10L0iPjwvc3Bhbj4KICAgICAgICAgICAgPC9kaXY+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9Im5vZGUtaWQtbW9ubyIgaWQ9Im15U3lwaGVySWQiPnN5cC0uLi48L2Rpdj4KICAgICAgICAgIDwvZGl2PgogICAgICAgICAgPGJ1dHRvbiBjbGFzcz0ibm9kZS1jb3B5LWJ0biIgaWQ9ImJ0bkNvcHlNeUlkIiB0aXRsZT0i0KHQutC+0L/QuNGA0L7QstCw0YLRjCBJRCI+CiAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTQiIGhlaWdodD0iMTQiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgICAgPHJlY3QgeD0iOSIgeT0iOSIgd2lkdGg9IjEzIiBoZWlnaHQ9IjEzIiByeD0iMiIgcnk9IjIiLz4KICAgICAgICAgICAgICA8cGF0aCBkPSJNNSAxNUg0YTIgMiAwIDAgMS0yLTJWNGEyIDIgMCAwIDEgMi0yaDlhMiAyIDAgMCAxIDIgMnYxIi8+CiAgICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgPC9idXR0b24+CiAgICAgICAgPC9kaXY+CiAgICAgIDwvaGVhZGVyPgoKICAgICAgPCEtLSDQmNC90LTQuNC60LDRgtC+0YAgTWVzaC3RgNCw0LTQsNGA0LAgKEJsdWV0b290aCArIFdpLUZpKSAtLT4KICAgICAgPHNlY3Rpb24gY2xhc3M9InJhZGFyLXN0YXR1cy1iYXIiIGlkPSJyYWRhclN0YXR1c0JhciI+CiAgICAgICAgPGRpdiBjbGFzcz0icmFkYXItbGVmdCI+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJyYWRhci13YXZlcyI+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9IndhdmUgd2F2ZS0xIj48L2Rpdj4KICAgICAgICAgICAgPGRpdiBjbGFzcz0id2F2ZSB3YXZlLTIiPjwvZGl2PgogICAgICAgICAgICA8ZGl2IGNsYXNzPSJyYWRhci1jZW50ZXItZG90Ij48L2Rpdj4KICAgICAgICAgIDwvZGl2PgogICAgICAgICAgPGRpdiBjbGFzcz0icmFkYXItdGV4dC1ibG9jayI+CiAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJyYWRhci10aXRsZSI+0KDQsNC00LjQvtC60LDQvdCw0LsgTWVzaDwvc3Bhbj4KICAgICAgICAgICAgPHNwYW4gY2xhc3M9InJhZGFyLWRlc2MiIGlkPSJtZXNoU3RhdHVzRGVzYyI+0KHQutCw0L3QuNGA0L7QstCw0L3QuNC1IEJMRSDQuCBVRFAuLi48L3NwYW4+CiAgICAgICAgICA8L2Rpdj4KICAgICAgICA8L2Rpdj4KICAgICAgICA8ZGl2IGNsYXNzPSJyYWRhci1yaWdodCI+CiAgICAgICAgICA8c3BhbiBjbGFzcz0icGVlci1jb3VudGVyLWJhZGdlIiBpZD0icGVlckNvdW50ZXJCYWRnZSI+MDwvc3Bhbj4KICAgICAgICA8L2Rpdj4KICAgICAgPC9zZWN0aW9uPgoKICAgICAgPCEtLSDQodC/0LjRgdC+0Log0L7QsdC90LDRgNGD0LbQtdC90L3Ri9GFINGD0YHRgtGA0L7QudGB0YLQsiDRgNGP0LTQvtC8ICjQn9GA0L7RhdC+0LbQuNC1IEJMRSAvIFdpLUZpKSAtLT4KICAgICAgPHNlY3Rpb24gY2xhc3M9Im5lYXJieS1zZWN0aW9uIiBpZD0ibmVhcmJ5U2VjdGlvbiI+CiAgICAgICAgPGRpdiBjbGFzcz0ic2VjdGlvbi1sYWJlbCI+CiAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjEyIiBoZWlnaHQ9IjEyIiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIuMiI+CiAgICAgICAgICAgIDxjaXJjbGUgY3g9IjEyIiBjeT0iMTIiIHI9IjIiLz4KICAgICAgICAgICAgPHBhdGggZD0iTTE2LjI0IDcuNzZhNiA2IDAgMCAxIDAgOC40OW0tOC40OC0uMDFhNiA2IDAgMCAxIDAtOC40OW0xMS4zMS0yLjgyYTEwIDEwIDAgMCAxIDAgMTQuMTRtLTE0LjE0IDBhMTAgMTAgMCAwIDEgMC0xNC4xNCIvPgogICAgICAgICAgPC9zdmc+CiAgICAgICAgICA8c3Bhbj7Qo9Ch0KLQoNCe0JnQodCi0JLQkCDQoNCv0JTQntCcPC9zcGFuPgogICAgICAgIDwvZGl2PgogICAgICAgIDxkaXYgY2xhc3M9Im5lYXJieS1wZWVyLWxpc3QiIGlkPSJuZWFyYnlQZWVyTGlzdCI+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJlbXB0eS1uZWFyYnktcGxhY2Vob2xkZXIiPgogICAgICAgICAgICA8c3Bhbj7Qn9C+0LjRgdC6INGD0LfQu9C+0LIg0L/QviBCTEUg0Lgg0LvQvtC60LDQu9GM0L3QvtC5INGB0LXRgtC4Li4uPC9zcGFuPgogICAgICAgICAgPC9kaXY+CiAgICAgICAgPC9kaXY+CiAgICAgIDwvc2VjdGlvbj4KCiAgICAgIDwhLS0g0J/QvtC40YHQuiDQv9C+INGH0LDRgtCw0LwgLS0+CiAgICAgIDxkaXYgY2xhc3M9InNlYXJjaC13cmFwIj4KICAgICAgICA8ZGl2IGNsYXNzPSJzZWFyY2gtaW5wdXQtYm94Ij4KICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIHNlYXJjaC1pY29uIiB3aWR0aD0iMTQiIGhlaWdodD0iMTQiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgIDxjaXJjbGUgY3g9IjExIiBjeT0iMTEiIHI9IjgiLz4KICAgICAgICAgICAgPGxpbmUgeDE9IjIxIiB5MT0iMjEiIHgyPSIxNi42NSIgeTI9IjE2LjY1Ii8+CiAgICAgICAgICA8L3N2Zz4KICAgICAgICAgIDxpbnB1dCB0eXBlPSJ0ZXh0IiBpZD0ic2VhcmNoSW5wdXQiIHBsYWNlaG9sZGVyPSLQn9C+0LjRgdC6INGB0L7QvtCx0YnQtdC90LjQuS4uLiIgYXV0b2NvbXBsZXRlPSJvZmYiPgogICAgICAgIDwvZGl2PgogICAgICA8L2Rpdj4KCiAgICAgIDwhLS0g0KHQv9C40YHQvtC6INC00LjQsNC70L7Qs9C+0LIgLS0+CiAgICAgIDxkaXYgY2xhc3M9ImNoYXQtbGlzdCIgaWQ9ImNoYXRMaXN0Ij48L2Rpdj4KCiAgICAgIDwhLS0g0KTRg9GC0LXRgCDRgSDRjdC60YHRgtGA0LXQvdC90YvQvCDRgdGC0LjRgNCw0L3QuNC10LwgLS0+CiAgICAgIDxmb290ZXIgY2xhc3M9InNpZGViYXItZm9vdGVyIj4KICAgICAgICA8YnV0dG9uIGNsYXNzPSJkYW5nZXItYnVybi1idG4iIGlkPSJidG5FbWVyZ2VuY3lCdXJuIiB0aXRsZT0i0K3QutGB0YLRgNC10L3QvdC+INGD0LTQsNC70LjRgtGMINCy0YHQtSDQutC70Y7Rh9C4INC4INC/0LXRgNC10L/QuNGB0LrRgyI+CiAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjE1IiBoZWlnaHQ9IjE1IiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPgogICAgICAgICAgICA8cGF0aCBkPSJNMTIgMnY0TTEyIDE4djRNNC45MyA0LjkzbDIuODMgMi44M00xNi4yNCAxNi4yNGwyLjgzIDIuODNNMiAxMmg0TTE4IDEyaDRNNC45MyAxOS4wN2wyLjgzLTIuODNNMTYuMjQgNy43NmwyLjgzLTIuODMiLz4KICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgPHNwYW4+0K3QutGB0YLRgNC10L3QvdC+0LUg0YHRgtC40YDQsNC90LjQtSDQtNCw0L3QvdGL0YU8L3NwYW4+CiAgICAgICAgPC9idXR0b24+CiAgICAgIDwvZm9vdGVyPgogICAgPC9hc2lkZT4KCiAgICA8IS0tINCe0KHQndCe0JLQndCQ0K8g0J7QkdCb0JDQodCi0Kw6INCn0LDRgiDQuCDRgdC+0L7QsdGJ0LXQvdC40Y8gLS0+CiAgICA8bWFpbiBjbGFzcz0iY2hhdC1tYWluIiBpZD0iY2hhdE1haW4iPgogICAgICA8IS0tINCo0LDQv9C60LAg0LDQutGC0LjQstC90L7Qs9C+INC00LjQsNC70L7Qs9CwIC0tPgogICAgICA8aGVhZGVyIGNsYXNzPSJjaGF0LWhlYWRlciI+CiAgICAgICAgPGRpdiBjbGFzcz0iaGVhZGVyLWxlZnQtY29sIj4KICAgICAgICAgIDxidXR0b24gY2xhc3M9ImJhY2stbmF2LWJ0biIgaWQ9ImJ0bkJhY2tUb0NoYXRzIiB0aXRsZT0i0J3QsNC30LDQtCDQuiDRgdC/0LjRgdC60YMg0YfQsNGC0L7QsiIgYXJpYS1sYWJlbD0i0J3QsNC30LDQtCI+CiAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMjAiIGhlaWdodD0iMjAiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMi41Ij4KICAgICAgICAgICAgICA8bGluZSB4MT0iMTkiIHkxPSIxMiIgeDI9IjUiIHkyPSIxMiIvPgogICAgICAgICAgICAgIDxwb2x5bGluZSBwb2ludHM9IjEyIDE5IDUgMTIgMTIgNSIvPgogICAgICAgICAgICA8L3N2Zz4KICAgICAgICAgIDwvYnV0dG9uPgogICAgICAgICAgPGRpdiBjbGFzcz0iYWN0aXZlLWF2YXRhciIgaWQ9ImFjdGl2ZUNoYXRBdmF0YXIiPk08L2Rpdj4KICAgICAgICAgIDxkaXYgY2xhc3M9ImFjdGl2ZS1tZXRhIj4KICAgICAgICAgICAgPGRpdiBjbGFzcz0iYWN0aXZlLXRpdGxlLXJvdyI+CiAgICAgICAgICAgICAgPGgyIGNsYXNzPSJhY3RpdmUtY2hhdC10aXRsZSIgaWQ9ImFjdGl2ZUNoYXROYW1lIj7QntCx0YnQuNC5INGA0LDQtNC40L7RjdGE0LjRgCBNZXNoPC9oMj4KICAgICAgICAgICAgICA8c3BhbiBjbGFzcz0iYWN0aXZlLWxvY2stYmFkZ2UiIGlkPSJhY3RpdmVDaGF0TG9ja0JhZGdlIiB0aXRsZT0i0KHQutCy0L7Qt9C90L7QtSDRiNC40YTRgNC+0LLQsNC90LjQtSI+CiAgICAgICAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjEyIiBoZWlnaHQ9IjEyIiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIuMiI+CiAgICAgICAgICAgICAgICAgIDxyZWN0IHg9IjMiIHk9IjExIiB3aWR0aD0iMTgiIGhlaWdodD0iMTEiIHJ4PSIyIiByeT0iMiIvPgogICAgICAgICAgICAgICAgICA8cGF0aCBkPSJNNyAxMVY3YTUgNSAwIDAgMSAxMCAwdjQiLz4KICAgICAgICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgICAgIDwvc3Bhbj4KICAgICAgICAgICAgPC9kaXY+CiAgICAgICAgICAgIDxkaXYgY2xhc3M9ImFjdGl2ZS1jaGF0LXN0YXR1cyIgaWQ9ImFjdGl2ZUNoYXRTdGF0dXMiPgogICAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJzdGF0dXMtaW5kaWNhdG9yLWRvdCI+PC9zcGFuPgogICAgICAgICAgICAgIDxzcGFuIGlkPSJhY3RpdmVDaGF0U3RhdHVzVGV4dCI+0KjQuNGA0L7QutC+0LLQtdGJ0LDRgtC10LvRjNC90YvQuSDQutCw0L3QsNC7IChVRFA6IDIzOS4yNTUuNzcuODg6Nzc4OCk8L3NwYW4+CiAgICAgICAgICAgIDwvZGl2PgogICAgICAgICAgPC9kaXY+CiAgICAgICAgPC9kaXY+CgogICAgICAgIDxkaXYgY2xhc3M9ImhlYWRlci1yaWdodC1hY3Rpb25zIj4KICAgICAgICAgIDxidXR0b24gY2xhc3M9ImNoYXQtdG9vbC1idG4iIGlkPSJidG5WZXJpZnlLZXlzIiB0aXRsZT0i0KHQstC10YDQuNGC0Ywg0LrRgNC40L/RgtC+0LPRgNCw0YTQuNGH0LXRgdC60LjQuSDQvtGC0L/QtdGH0LDRgtC+0Log0LrQu9GO0YfQtdC5Ij4KICAgICAgICAgICAgPHN2ZyBjbGFzcz0iaWNvbi1zdmciIHdpZHRoPSIxNSIgaGVpZ2h0PSIxNSIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9ImN1cnJlbnRDb2xvciIgc3Ryb2tlLXdpZHRoPSIyIj4KICAgICAgICAgICAgICA8cGF0aCBkPSJNMTIgMjJzOC00IDgtMTBWNWwtOC0zLTggM3Y3YzAgNiA4IDEwIDggMTB6Ii8+CiAgICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgICA8c3BhbiBjbGFzcz0idG9vbC1idG4tdGV4dCI+0J7RgtC/0LXRh9Cw0YLQvtC6PC9zcGFuPgogICAgICAgICAgPC9idXR0b24+CiAgICAgICAgICA8YnV0dG9uIGNsYXNzPSJjaGF0LXRvb2wtYnRuIiBpZD0iYnRuU2VsZkRlc3RydWN0IiB0aXRsZT0i0KLQsNC50LzQtdGAINGB0LDQvNC+0YPQvdC40YfRgtC+0LbQtdC90LjRjyI+CiAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTUiIGhlaWdodD0iMTUiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgICAgPGNpcmNsZSBjeD0iMTIiIGN5PSIxMiIgcj0iMTAiLz4KICAgICAgICAgICAgICA8cG9seWxpbmUgcG9pbnRzPSIxMiA2IDEyIDEyIDE2IDE0Ii8+CiAgICAgICAgICAgIDwvc3ZnPgogICAgICAgICAgICA8c3BhbiBpZD0ic2VsZkRlc3RydWN0TGFiZWwiPjI00Yc8L3NwYW4+CiAgICAgICAgICA8L2J1dHRvbj4KICAgICAgICA8L2Rpdj4KICAgICAgPC9oZWFkZXI+CgogICAgICA8IS0tINCe0LrQvdC+INGB0L7QvtCx0YnQtdC90LjQuSAtLT4KICAgICAgPGRpdiBjbGFzcz0ibWVzc2FnZXMtdmlld3BvcnQiIGlkPSJtZXNzYWdlc1ZpZXdwb3J0Ij4KICAgICAgICA8ZGl2IGNsYXNzPSJkYXRlLWJhZGdlLXdyYXAiPgogICAgICAgICAgPHNwYW4gY2xhc3M9ImRhdGUtYmFkZ2UiPtCh0JXQk9Ce0JTQndCvPC9zcGFuPgogICAgICAgIDwvZGl2PgogICAgICAgIDxkaXYgY2xhc3M9ImVuY3J5cHRpb24tYmFubmVyIiBpZD0iZW5jcnlwdGlvbkJhbm5lciI+CiAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjE0IiBoZWlnaHQ9IjE0IiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPgogICAgICAgICAgICA8cmVjdCB4PSIzIiB5PSIxMSIgd2lkdGg9IjE4IiBoZWlnaHQ9IjExIiByeD0iMiIgcnk9IjIiLz4KICAgICAgICAgICAgPHBhdGggZD0iTTcgMTFWN2E1IDUgMCAwIDEgMTAgMHY0Ii8+CiAgICAgICAgICA8L3N2Zz4KICAgICAgICAgIDxzcGFuIGlkPSJlbmNyeXB0aW9uQmFubmVyVGV4dCI+0KjQuNGE0YDQvtCy0LDQvdC40LUgWDI1NTE5ICYgQ2hhQ2hhMjAtUG9seTEzMDUuINCf0YDRj9C80L7QuSDRgNCw0LTQuNC+0L7QsdC80LXQvSDQsdC10Lcg0YHQtdGA0LLQtdGA0L7Qsi48L3NwYW4+CiAgICAgICAgPC9kaXY+CiAgICAgIDwvZGl2PgoKICAgICAgPCEtLSDQn9C+0LvQtSDQstCy0L7QtNCwINGB0L7QvtCx0YnQtdC90LjQuSDQuCDQv9GA0LjQutGA0LXQv9C70LXQvdC40Y8g0YTQsNC50LvQvtCyIC0tPgogICAgICA8Zm9vdGVyIGNsYXNzPSJjaGF0LWNvbXBvc2VyIj4KICAgICAgICA8IS0tINCh0LrRgNGL0YLRi9C5INGE0LDQudC70L7QstGL0Lkg0YHQtdC70LXQutGC0L7RgCDQtNC70Y8g0LTQvtC60YPQvNC10L3RgtC+0LIsINC60LDRgNGC0LjQvdC+0LosINGE0LDQudC70L7QsiAtLT4KICAgICAgICA8aW5wdXQgdHlwZT0iZmlsZSIgaWQ9ImZpbGVJbnB1dCIgY2xhc3M9ImhpZGRlbi1maWxlLWlucHV0Ij4KCiAgICAgICAgPGRpdiBjbGFzcz0iY29tcG9zZXItYmFyIj4KICAgICAgICAgIDxidXR0b24gY2xhc3M9ImNvbXBvc2VyLWFjdGlvbi1idG4iIGlkPSJidG5BdHRhY2giIHRpdGxlPSLQntGC0L/RgNCw0LLQuNGC0Ywg0YTQsNC50Lsg0LjQu9C4INGE0L7RgtC+IiBhcmlhLWxhYmVsPSLQn9GA0LjQutGA0LXQv9C40YLRjCDRhNCw0LnQuyI+CiAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMjAiIGhlaWdodD0iMjAiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgICAgPHBhdGggZD0iTTIxLjQ0IDExLjA1bC05LjE5IDkuMTlhNiA2IDAgMCAxLTguNDktOC40OWw5LjE5LTkuMTlhNCA0IDAgMCAxIDUuNjYgNS42NmwtOS4yIDkuMTlhMiAyIDAgMCAxLTIuODMtMi44M2w4LjQ5LTguNDgiLz4KICAgICAgICAgICAgPC9zdmc+CiAgICAgICAgICA8L2J1dHRvbj4KCiAgICAgICAgICA8dGV4dGFyZWEgaWQ9Im1lc3NhZ2VJbnB1dCIgcGxhY2Vob2xkZXI9ItCd0LDQv9C40YHQsNGC0Ywg0YHQvtC+0LHRidC10L3QuNC1Li4uIiByb3dzPSIxIj48L3RleHRhcmVhPgoKICAgICAgICAgIDxidXR0b24gY2xhc3M9ImNvbXBvc2VyLXNlbmQtYnRuIiBpZD0iYnRuU2VuZCIgdGl0bGU9ItCe0YLQv9GA0LDQstC40YLRjCDRgdC+0L7QsdGJ0LXQvdC40LUiIGFyaWEtbGFiZWw9ItCe0YLQv9GA0LDQstC40YLRjCI+CiAgICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTgiIGhlaWdodD0iMTgiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMi40Ij4KICAgICAgICAgICAgICA8cGF0aCBkPSJNMjIgMkwxMSAxM00yMiAybC03IDIwLTQtOS05LTQgMjAtN3oiLz4KICAgICAgICAgICAgPC9zdmc+CiAgICAgICAgICA8L2J1dHRvbj4KICAgICAgICA8L2Rpdj4KICAgICAgPC9mb290ZXI+CiAgICA8L21haW4+CiAgPC9kaXY+CgogIDwhLS0g0JzQntCU0JDQm9Cs0J3QntCVINCe0JrQndCeOiDQndC+0LLRi9C5INC00LjQsNC70L7QsyAtLT4KICA8ZGl2IGNsYXNzPSJtb2RhbC1vdmVybGF5IGhpZGRlbiIgaWQ9Im5ld0NoYXRNb2RhbCI+CiAgICA8ZGl2IGNsYXNzPSJtb2RhbC1jYXJkIj4KICAgICAgPGRpdiBjbGFzcz0ibW9kYWwtaGVhZGVyIj4KICAgICAgICA8aDMgY2xhc3M9Im1vZGFsLXRpdGxlIj7QndC+0LLRi9C5INC00LjQsNC70L7QszwvaDM+CiAgICAgICAgPGJ1dHRvbiBjbGFzcz0ibW9kYWwtY2xvc2UtYnRuIiBpZD0iYnRuQ2xvc2VOZXdDaGF0IiBhcmlhLWxhYmVsPSLQl9Cw0LrRgNGL0YLRjCI+CiAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjE4IiBoZWlnaHQ9IjE4IiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPgogICAgICAgICAgICA8bGluZSB4MT0iMTgiIHkxPSI2IiB4Mj0iNiIgeTI9IjE4Ii8+CiAgICAgICAgICAgIDxsaW5lIHgxPSI2IiB5MT0iNiIgeDI9IjE4IiB5Mj0iMTgiLz4KICAgICAgICAgIDwvc3ZnPgogICAgICAgIDwvYnV0dG9uPgogICAgICA8L2Rpdj4KICAgICAgPGRpdiBjbGFzcz0ibW9kYWwtYm9keSI+CiAgICAgICAgPHAgY2xhc3M9Im1vZGFsLWRlc2NyaXB0aW9uIj7QktCy0LXQtNC40YLQtSBTeXBoZXIgSUQg0YPRgdGC0YDQvtC50YHRgtCy0LAg0LDQtNGA0LXRgdCw0YLQsCDQuNC70Lgg0LLRi9Cx0LXRgNC40YLQtSDQvdCw0LnQtNC10L3QvdGL0Lkg0YPQt9C10Lsg0L/QvtCx0LvQuNC30L7RgdGC0Lg6PC9wPgogICAgICAgIDxkaXYgY2xhc3M9ImZvcm0tZ3JvdXAiPgogICAgICAgICAgPGxhYmVsIGNsYXNzPSJmb3JtLWxhYmVsIiBmb3I9Im5ld0NoYXRQZWVySWRJbnB1dCI+U3lwaGVyIElEINCw0LTRgNC10YHQsNGC0LA8L2xhYmVsPgogICAgICAgICAgPGlucHV0IHR5cGU9InRleHQiIGNsYXNzPSJmb3JtLWlucHV0IiBpZD0ibmV3Q2hhdFBlZXJJZElucHV0IiBwbGFjZWhvbGRlcj0ic3lwLS4uLiIgYXV0b2NvbXBsZXRlPSJvZmYiPgogICAgICAgIDwvZGl2PgogICAgICAgIDxkaXYgY2xhc3M9ImRpc2NvdmVyZWQtcGVlcnMtcXVpY2siIGlkPSJkaXNjb3ZlcmVkUGVlcnNNb2RhbExpc3QiPjwvZGl2PgogICAgICA8L2Rpdj4KICAgICAgPGRpdiBjbGFzcz0ibW9kYWwtZm9vdGVyIj4KICAgICAgICA8YnV0dG9uIGNsYXNzPSJidG4tc2Vjb25kYXJ5IiBpZD0iYnRuQ2FuY2VsTmV3Q2hhdCI+0J7RgtC80LXQvdCwPC9idXR0b24+CiAgICAgICAgPGJ1dHRvbiBjbGFzcz0iYnRuLXByaW1hcnkiIGlkPSJidG5Db25maXJtTmV3Q2hhdCI+0J3QsNGH0LDRgtGMINC00LjQsNC70L7QszwvYnV0dG9uPgogICAgICA8L2Rpdj4KICAgIDwvZGl2PgogIDwvZGl2PgoKICA8IS0tINCc0J7QlNCQ0JvQrNCd0J7QlSDQntCa0J3Qnjog0J3QsNGB0YLRgNC+0LnQutC4INGD0LfQu9CwIC0tPgogIDxkaXYgY2xhc3M9Im1vZGFsLW92ZXJsYXkgaGlkZGVuIiBpZD0ic2V0dGluZ3NNb2RhbCI+CiAgICA8ZGl2IGNsYXNzPSJtb2RhbC1jYXJkIj4KICAgICAgPGRpdiBjbGFzcz0ibW9kYWwtaGVhZGVyIj4KICAgICAgICA8aDMgY2xhc3M9Im1vZGFsLXRpdGxlIj7QndCw0YHRgtGA0L7QudC60Lgg0YPQt9C70LA8L2gzPgogICAgICAgIDxidXR0b24gY2xhc3M9Im1vZGFsLWNsb3NlLWJ0biIgaWQ9ImJ0bkNsb3NlU2V0dGluZ3MiIGFyaWEtbGFiZWw9ItCX0LDQutGA0YvRgtGMIj4KICAgICAgICAgIDxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTgiIGhlaWdodD0iMTgiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMiI+CiAgICAgICAgICAgIDxsaW5lIHgxPSIxOCIgeTE9IjYiIHgyPSI2IiB5Mj0iMTgiLz4KICAgICAgICAgICAgPGxpbmUgeDE9IjYiIHkxPSI2IiB4Mj0iMTgiIHkyPSIxOCIvPgogICAgICAgICAgPC9zdmc+CiAgICAgICAgPC9idXR0b24+CiAgICAgIDwvZGl2PgogICAgICA8ZGl2IGNsYXNzPSJtb2RhbC1ib2R5Ij4KICAgICAgICA8ZGl2IGNsYXNzPSJmb3JtLWdyb3VwIj4KICAgICAgICAgIDxsYWJlbCBjbGFzcz0iZm9ybS1sYWJlbCIgZm9yPSJpbnB1dERpc3BsYXlOYW1lIj7QmNC80Y8g0YPRgdGC0YDQvtC50YHRgtCy0LAg0LIg0Y3RhNC40YDQtTwvbGFiZWw+CiAgICAgICAgICA8aW5wdXQgdHlwZT0idGV4dCIgY2xhc3M9ImZvcm0taW5wdXQiIGlkPSJpbnB1dERpc3BsYXlOYW1lIiBtYXhsZW5ndGg9IjI0Ij4KICAgICAgICA8L2Rpdj4KICAgICAgICA8ZGl2IGNsYXNzPSJmb3JtLWdyb3VwIj4KICAgICAgICAgIDxsYWJlbCBjbGFzcz0iZm9ybS1sYWJlbCI+0KPQvdC40LrQsNC70YzQvdGL0LkgSUQg0YPQt9C70LA8L2xhYmVsPgogICAgICAgICAgPGRpdiBjbGFzcz0iY29kZS1ib3gtZGlzcGxheSIgaWQ9InNldHRpbmdzTm9kZUlkIj5zeXAtLi4uPC9kaXY+CiAgICAgICAgPC9kaXY+CiAgICAgICAgPGRpdiBjbGFzcz0iZm9ybS1ncm91cCI+CiAgICAgICAgICA8bGFiZWwgY2xhc3M9ImZvcm0tbGFiZWwiPtCb0L7QutCw0LvRjNC90YvQuSBJUCDQsNC00YDQtdGBPC9sYWJlbD4KICAgICAgICAgIDxkaXYgY2xhc3M9ImNvZGUtYm94LWRpc3BsYXkiIGlkPSJzZXR0aW5nc0xvY2FsSXAiPjEyNy4wLjAuMTo3Nzg4PC9kaXY+CiAgICAgICAgPC9kaXY+CiAgICAgICAgPGRpdiBjbGFzcz0iZm9ybS1ncm91cCI+CiAgICAgICAgICA8bGFiZWwgY2xhc3M9ImZvcm0tbGFiZWwiPtCh0LzQtdC90LAg0LrQu9GO0YfQtdC5IC8g0KDQtdCz0LXQvdC10YDQsNGG0LjRjyBJRDwvbGFiZWw+CiAgICAgICAgICA8YnV0dG9uIGNsYXNzPSJidG4tb3V0bGluZS1kYW5nZXIiIGlkPSJidG5SZWdlbmVyYXRlSWQiPtCh0LPQtdC90LXRgNC40YDQvtCy0LDRgtGMINC90L7QstGL0Lkg0YPQvdC40LrQsNC70YzQvdGL0LkgSUQ8L2J1dHRvbj4KICAgICAgICA8L2Rpdj4KICAgICAgPC9kaXY+CiAgICAgIDxkaXYgY2xhc3M9Im1vZGFsLWZvb3RlciI+CiAgICAgICAgPGJ1dHRvbiBjbGFzcz0iYnRuLXByaW1hcnkiIGlkPSJidG5TYXZlU2V0dGluZ3MiPtCh0L7RhdGA0LDQvdC40YLRjDwvYnV0dG9uPgogICAgICA8L2Rpdj4KICAgIDwvZGl2PgogIDwvZGl2PgoKICA8IS0tINCc0J7QlNCQ0JvQrNCd0J7QlSDQntCa0J3Qnjog0JrRgNC40L/RgtC+0LPRgNCw0YTQuNGH0LXRgdC60LjQuSDQvtGC0L/QtdGH0LDRgtC+0LogLS0+CiAgPGRpdiBjbGFzcz0ibW9kYWwtb3ZlcmxheSBoaWRkZW4iIGlkPSJ2ZXJpZnlLZXlzTW9kYWwiPgogICAgPGRpdiBjbGFzcz0ibW9kYWwtY2FyZCI+CiAgICAgIDxkaXYgY2xhc3M9Im1vZGFsLWhlYWRlciI+CiAgICAgICAgPGgzIGNsYXNzPSJtb2RhbC10aXRsZSI+0J7RgtC/0LXRh9Cw0YLQvtC6INC60LvRjtGH0LAgKEZpbmdlcnByaW50KTwvaDM+CiAgICAgICAgPGJ1dHRvbiBjbGFzcz0ibW9kYWwtY2xvc2UtYnRuIiBpZD0iYnRuQ2xvc2VWZXJpZnlLZXlzIiBhcmlhLWxhYmVsPSLQl9Cw0LrRgNGL0YLRjCI+CiAgICAgICAgICA8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjE4IiBoZWlnaHQ9IjE4IiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPgogICAgICAgICAgICA8bGluZSB4MT0iMTgiIHkxPSI2IiB4Mj0iNiIgeTI9IjE4Ii8+CiAgICAgICAgICAgIDxsaW5lIHgxPSI2IiB5MT0iNiIgeDI9IjE4IiB5Mj0iMTgiLz4KICAgICAgICAgIDwvc3ZnPgogICAgICAgIDwvYnV0dG9uPgogICAgICA8L2Rpdj4KICAgICAgPGRpdiBjbGFzcz0ibW9kYWwtYm9keSI+CiAgICAgICAgPHAgY2xhc3M9Im1vZGFsLWRlc2NyaXB0aW9uIj7QodCy0LXRgNGM0YLQtSDRjdGC0L7RgiDRhtC40YTRgNC+0LLQvtC5INC60L7QtCDRgSDRgdC+0LHQtdGB0LXQtNC90LjQutC+0Lwg0LTQu9GPINC40YHQutC70Y7Rh9C10L3QuNGPINCw0YLQsNC6INC/0L7RgdGA0LXQtNC90LjQutCwIChNSVRNKTo8L3A+CiAgICAgICAgPGRpdiBjbGFzcz0iZmluZ2VycHJpbnQtZGlzcGxheSIgaWQ9Im1vZGFsRmluZ2VycHJpbnQiPjAwMDAgMDAwMCAwMDAwIDAwMDAgMDAwMCAwMDAwIDAwMDAgMDAwMDwvZGl2PgogICAgICAgIDxkaXYgY2xhc3M9ImNyeXB0by1tZXRhLWxpc3QiPgogICAgICAgICAgPGRpdiBjbGFzcz0iY3J5cHRvLW1ldGEtaXRlbSI+CiAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJtZXRhLWxhYmVsIj7QkNGB0LjQvNC80LXRgtGA0LjRh9C90YvQuSDQvtCx0LzQtdC9Ojwvc3Bhbj4KICAgICAgICAgICAgPHNwYW4gY2xhc3M9Im1ldGEtdmFsIj5YMjU1MTkgKEN1cnZlMjU1MTkpPC9zcGFuPgogICAgICAgICAgPC9kaXY+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJjcnlwdG8tbWV0YS1pdGVtIj4KICAgICAgICAgICAgPHNwYW4gY2xhc3M9Im1ldGEtbGFiZWwiPtCf0L7RgtC+0LrQvtCy0YvQuSDRiNC40YTRgDo8L3NwYW4+CiAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJtZXRhLXZhbCI+Q2hhQ2hhMjAtUG9seTEzMDUgKDI1Ni1iaXQpPC9zcGFuPgogICAgICAgICAgPC9kaXY+CiAgICAgICAgICA8ZGl2IGNsYXNzPSJjcnlwdG8tbWV0YS1pdGVtIj4KICAgICAgICAgICAgPHNwYW4gY2xhc3M9Im1ldGEtbGFiZWwiPtCg0L7RgtCw0YbQuNGPINC60LvRjtGH0LXQuTo8L3NwYW4+CiAgICAgICAgICAgIDxzcGFuIGNsYXNzPSJtZXRhLXZhbCI+RG91YmxlIFJhdGNoZXQgKEZvcndhcmQgU2VjcmVjeSk8L3NwYW4+CiAgICAgICAgICA8L2Rpdj4KICAgICAgICA8L2Rpdj4KICAgICAgPC9kaXY+CiAgICAgIDxkaXYgY2xhc3M9Im1vZGFsLWZvb3RlciI+CiAgICAgICAgPGJ1dHRvbiBjbGFzcz0iYnRuLXByaW1hcnkiIGlkPSJidG5Pa1ZlcmlmeUtleXMiPtCa0LvRjtGH0Lgg0L/RgNC+0LLQtdGA0LXQvdGLPC9idXR0b24+CiAgICAgIDwvZGl2PgogICAgPC9kaXY+CiAgPC9kaXY+CgogIDwhLS0g0JLRgdC/0LvRi9Cy0LDRjtGJ0LjQtSDRg9Cy0LXQtNC+0LzQu9C10L3QuNGPIChUb2FzdCkgLS0+CiAgPGRpdiBjbGFzcz0idG9hc3Qtbm90aWZpY2F0aW9uIGhpZGRlbiIgaWQ9InRvYXN0Tm90aWZpY2F0aW9uIj4KICAgIDxzcGFuIGNsYXNzPSJ0b2FzdC1kb3QiPjwvc3Bhbj4KICAgIDxzcGFuIGlkPSJ0b2FzdFRleHQiPtCj0LLQtdC00L7QvNC70LXQvdC40LU8L3NwYW4+CiAgPC9kaXY+CgogIDxzY3JpcHQgc3JjPSJhcHAuanMiPjwvc2NyaXB0Pgo8L2JvZHk+CjwvaHRtbD4K"));
                }
                return _html;
            }
        }

        public static string CSS
        {
            get
            {
                if (_css == null)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"packages\desktop\src\renderer\styles.css");
                    if (File.Exists(path)) _css = File.ReadAllText(path, Encoding.UTF8);
                    else _css = Encoding.UTF8.GetString(Convert.FromBase64String("LyogPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICAgU1lQSEVSIOKAlCBIaWdoLUVuZCBEYXJrIEFNT0xFRCBNZXNoIE1lc3NlbmdlcgogICBTdHJpY3RseSBaZXJvLUVtb2ppIOKAoiBQdXJlIFZlY3RvciBUeXBvZ3JhcGh5IOKAoiBUZWxlZ3JhbSAvIFNpZ25hbCBHcmFkZSBVSQogICA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PSAqLwoKOnJvb3QgewogIC0tYmctZGVlcDogIzA1MDcwYTsKICAtLWJnLXN1cmZhY2U6ICMwYjBlMTU7CiAgLS1iZy1lbGV2YXRlZDogIzEyMTcyMjsKICAtLWJnLWhvdmVyOiAjMTgyMDMwOwogIC0tYmctYWN0aXZlOiAjMWYyYTNmOwoKICAtLWJvcmRlcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA3KTsKICAtLWJvcmRlci1mb2N1czogcmdiYSgwLCAyNDAsIDI1NSwgMC40KTsKCiAgLS10ZXh0LXByaW1hcnk6ICNmZmZmZmY7CiAgLS10ZXh0LXNlY29uZGFyeTogIzhhOTZhODsKICAtLXRleHQtbXV0ZWQ6ICM1NjYzNzU7CgogIC0tYWNjZW50LWN5YW46ICMwMGYwZmY7CiAgLS1hY2NlbnQtYXp1cmU6ICMwMDdhZmY7CiAgLS1hY2NlbnQtZW1lcmFsZDogIzAwZTY3NjsKICAtLWFjY2VudC1kYW5nZXI6ICNmZjNiMzA7CgogIC0tbXNnLWluLWJnOiAjMTExNjIyOwogIC0tbXNnLWluLWJvcmRlcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA1KTsKICAtLW1zZy1vdXQtYmc6ICMwZjMwNDk7CiAgLS1tc2ctb3V0LWJvcmRlcjogcmdiYSgwLCAyNDAsIDI1NSwgMC4yKTsKCiAgLS1mb250LXNhbnM6ICdQbHVzIEpha2FydGEgU2FucycsIC1hcHBsZS1zeXN0ZW0sIEJsaW5rTWFjU3lzdGVtRm9udCwgJ1NlZ29lIFVJJywgUm9ib3RvLCBIZWx2ZXRpY2EsIEFyaWFsLCBzYW5zLXNlcmlmOwogIC0tZm9udC1tb25vOiAnSmV0QnJhaW5zIE1vbm8nLCAnQ2FzY2FkaWEgQ29kZScsIE1lbmxvLCBNb25hY28sIENvbnNvbGFzLCBtb25vc3BhY2U7CgogIC0tc2lkZWJhci13aWR0aDogMzUwcHg7CiAgLS1oZWFkZXItaGVpZ2h0OiA2MHB4Owp9CgoqIHsKICBib3gtc2l6aW5nOiBib3JkZXItYm94OwogIG1hcmdpbjogMDsKICBwYWRkaW5nOiAwOwogIC13ZWJraXQtdGFwLWhpZ2hsaWdodC1jb2xvcjogdHJhbnNwYXJlbnQ7Cn0KCmh0bWwsIGJvZHkgewogIHdpZHRoOiAxMDAlOwogIGhlaWdodDogMTAwJTsKICBvdmVyZmxvdzogaGlkZGVuOwogIGJhY2tncm91bmQtY29sb3I6IHZhcigtLWJnLWRlZXApOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtZmFtaWx5OiB2YXIoLS1mb250LXNhbnMpOwogIGZvbnQtc2l6ZTogMTRweDsKICBsaW5lLWhlaWdodDogMS40OwogIC13ZWJraXQtZm9udC1zbW9vdGhpbmc6IGFudGlhbGlhc2VkOwogIC1tb3otb3N4LWZvbnQtc21vb3RoaW5nOiBncmF5c2NhbGU7Cn0KCi8qIEFQUCBTSEVMTCAqLwouYXBwLWxheW91dCB7CiAgZGlzcGxheTogZmxleDsKICB3aWR0aDogMTAwdnc7CiAgaGVpZ2h0OiAxMDBkdmg7CiAgcG9zaXRpb246IHJlbGF0aXZlOwogIG92ZXJmbG93OiBoaWRkZW47CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZGVlcCk7Cn0KCi8qIFNJREVCQVIgKi8KLnNpZGViYXIgewogIHdpZHRoOiB2YXIoLS1zaWRlYmFyLXdpZHRoKTsKICBoZWlnaHQ6IDEwMCU7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctc3VyZmFjZSk7CiAgYm9yZGVyLXJpZ2h0OiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZmxleC1zaHJpbms6IDA7CiAgei1pbmRleDogMTA7CiAgdHJhbnNpdGlvbjogdHJhbnNmb3JtIDAuMjRzIGN1YmljLWJlemllcigwLjE2LCAxLCAwLjMsIDEpOwp9Cgouc2lkZWJhci1oZWFkZXIgewogIHBhZGRpbmc6IDE0cHggMTZweCAxMHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLXN1cmZhY2UpOwogIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDEycHg7Cn0KCi5icmFuZC1iYXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47Cn0KCi5icmFuZC1pZGVudGl0eSB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTBweDsKfQoKLmJyYW5kLWljb24tYm94IHsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGJhY2tncm91bmQ6ICMwMDAwMDA7CiAgYm9yZGVyOiAxLjVweCBzb2xpZCAjZmZmZmZmOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKfQoKLmJyYW5kLXRpdGxlLXdyYXAgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDhweDsKfQoKLmJyYW5kLW5hbWUgewogIGZvbnQtc2l6ZTogMTZweDsKICBmb250LXdlaWdodDogODAwOwogIGxldHRlci1zcGFjaW5nOiAxLjVweDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLmJyYW5kLWJhZGdlIHsKICBmb250LXNpemU6IDlweDsKICBmb250LXdlaWdodDogNzAwOwogIGxldHRlci1zcGFjaW5nOiAwLjhweDsKICBwYWRkaW5nOiAycHggNnB4OwogIGJvcmRlci1yYWRpdXM6IDRweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDIzMCwgMTE4LCAwLjEyKTsKICBjb2xvcjogdmFyKC0tYWNjZW50LWVtZXJhbGQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMCwgMjMwLCAxMTgsIDAuMjUpOwp9CgouaGVhZGVyLWFjdGlvbi1idXR0b25zIHsKICBkaXNwbGF5OiBmbGV4OwogIGdhcDogNnB4Owp9CgoubmF2LWljb24tYnRuIHsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5uYXYtaWNvbi1idG46aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWhvdmVyKTsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLyogTVkgTk9ERSBDQVJEICovCi5teS1ub2RlLWNhcmQgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDEwcHg7CiAgcGFkZGluZzogOHB4IDEwcHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogMTBweDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5teS1ub2RlLWNhcmQ6aG92ZXIgewogIGJvcmRlci1jb2xvcjogdmFyKC0tYm9yZGVyLWZvY3VzKTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1ob3Zlcik7Cn0KCi5ub2RlLWF2YXRhciB7CiAgd2lkdGg6IDM0cHg7CiAgaGVpZ2h0OiAzNHB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiBsaW5lYXItZ3JhZGllbnQoMTM1ZGVnLCAjMDA3YWZmLCAjMDBmMGZmKTsKICBjb2xvcjogIzA1MDcwYTsKICBmb250LXdlaWdodDogNzAwOwogIGZvbnQtc2l6ZTogMTNweDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgZmxleC1zaHJpbms6IDA7Cn0KCi5ub2RlLWluZm8gewogIGZsZXg6IDE7CiAgbWluLXdpZHRoOiAwOwp9Cgoubm9kZS1uYW1lLXJvdyB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4Owp9Cgoubm9kZS1uYW1lIHsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5zdGF0dXMtcHVsc2UtZG90IHsKICB3aWR0aDogN3B4OwogIGhlaWdodDogN3B4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtZW1lcmFsZCk7CiAgYm94LXNoYWRvdzogMCAwIDZweCB2YXIoLS1hY2NlbnQtZW1lcmFsZCk7CiAgYW5pbWF0aW9uOiBwdWxzZS1yaW5nIDJzIGluZmluaXRlOwp9CgpAa2V5ZnJhbWVzIHB1bHNlLXJpbmcgewogIDAlIHsgdHJhbnNmb3JtOiBzY2FsZSgwLjk1KTsgb3BhY2l0eTogMC44OyB9CiAgNTAlIHsgdHJhbnNmb3JtOiBzY2FsZSgxLjE1KTsgb3BhY2l0eTogMTsgfQogIDEwMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDAuOTUpOyBvcGFjaXR5OiAwLjg7IH0KfQoKLm5vZGUtaWQtbW9ubyB7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtbW9ubyk7CiAgZm9udC1zaXplOiAxMXB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwp9Cgoubm9kZS1jb3B5LWJ0biB7CiAgYmFja2dyb3VuZDogdHJhbnNwYXJlbnQ7CiAgYm9yZGVyOiBub25lOwogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKICBwYWRkaW5nOiA0cHg7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGN1cnNvcjogcG9pbnRlcjsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5ub2RlLWNvcHktYnRuOmhvdmVyIHsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMSk7Cn0KCi8qIFJBREFSIFNUQVRVUyBCQVIgKi8KLnJhZGFyLXN0YXR1cy1iYXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47CiAgcGFkZGluZzogMTBweCAxNnB4OwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMDMpOwogIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwp9CgoucmFkYXItbGVmdCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTBweDsKfQoKLnJhZGFyLXdhdmVzIHsKICBwb3NpdGlvbjogcmVsYXRpdmU7CiAgd2lkdGg6IDIwcHg7CiAgaGVpZ2h0OiAyMHB4OwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKfQoKLnJhZGFyLWNlbnRlci1kb3QgewogIHdpZHRoOiA2cHg7CiAgaGVpZ2h0OiA2cHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKfQoKLndhdmUgewogIHBvc2l0aW9uOiBhYnNvbHV0ZTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1hY2NlbnQtY3lhbik7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIG9wYWNpdHk6IDA7CiAgYW5pbWF0aW9uOiByaXBwbGUgMnMgaW5maW5pdGUgY3ViaWMtYmV6aWVyKDAuMiwgMC44LCAwLjIsIDEpOwp9Cgoud2F2ZS0xIHsgd2lkdGg6IDE0cHg7IGhlaWdodDogMTRweDsgYW5pbWF0aW9uLWRlbGF5OiAwczsgfQoud2F2ZS0yIHsgd2lkdGg6IDIycHg7IGhlaWdodDogMjJweDsgYW5pbWF0aW9uLWRlbGF5OiAwLjZzOyB9CgpAa2V5ZnJhbWVzIHJpcHBsZSB7CiAgMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDAuNCk7IG9wYWNpdHk6IDAuOTsgfQogIDEwMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDEuNCk7IG9wYWNpdHk6IDA7IH0KfQoKLnJhZGFyLXRleHQtYmxvY2sgewogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKfQoKLnJhZGFyLXRpdGxlIHsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLnJhZGFyLWRlc2MgewogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwp9CgoucGVlci1jb3VudGVyLWJhZGdlIHsKICBmb250LXNpemU6IDExcHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBwYWRkaW5nOiAycHggN3B4OwogIGJvcmRlci1yYWRpdXM6IDEwcHg7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAyNDAsIDI1NSwgMC4xMik7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDI0MCwgMjU1LCAwLjI1KTsKfQoKLyogTkVBUkJZIFBFRVJTIFJBREFSIFNFQ1RJT04gKi8KLm5lYXJieS1zZWN0aW9uIHsKICBwYWRkaW5nOiAxMHB4IDE2cHg7CiAgYm9yZGVyLWJvdHRvbTogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAwLCAwLCAwLjIpOwp9Cgouc2VjdGlvbi1sYWJlbCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4OwogIGZvbnQtc2l6ZTogMTBweDsKICBmb250LXdlaWdodDogNzAwOwogIGxldHRlci1zcGFjaW5nOiAwLjlweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgbWFyZ2luLWJvdHRvbTogOHB4Owp9CgoubmVhcmJ5LXBlZXItbGlzdCB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogNnB4OwogIG1heC1oZWlnaHQ6IDE0MHB4OwogIG92ZXJmbG93LXk6IGF1dG87Cn0KCi5lbXB0eS1uZWFyYnktcGxhY2Vob2xkZXIgewogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgcGFkZGluZzogNHB4IDA7CiAgZm9udC1zdHlsZTogaXRhbGljOwp9CgoubmVhcmJ5LXBlZXItcm93IHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwogIHBhZGRpbmc6IDZweCA4cHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgoubmVhcmJ5LXBlZXItcm93OmhvdmVyIHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1ob3Zlcik7CiAgYm9yZGVyLWNvbG9yOiByZ2JhKDAsIDIzMCwgMTE4LCAwLjMpOwp9CgoucGVlci1yb3ctbGVmdCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogOHB4OwogIG1pbi13aWR0aDogMDsKfQoKLm5lYXJieS1hdmF0YXIgewogIHdpZHRoOiAyNnB4OwogIGhlaWdodDogMjZweDsKICBib3JkZXItcmFkaXVzOiA1MCU7CiAgYmFja2dyb3VuZDogIzFlMjkzYjsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICBmb250LXNpemU6IDExcHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgZmxleC1zaHJpbms6IDA7Cn0KCi5uZWFyYnktaW5mbyB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIG1pbi13aWR0aDogMDsKfQoKLm5lYXJieS1pZCB7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtbW9ubyk7CiAgZm9udC1zaXplOiAxMXB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwp9CgoubmVhcmJ5LW1ldGEgewogIGZvbnQtc2l6ZTogMTBweDsKICBjb2xvcjogdmFyKC0tYWNjZW50LWVtZXJhbGQpOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDRweDsKfQoKLnBlZXItYWN0aW9uLWJ0biB7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAyNDAsIDI1NSwgMC4xKTsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMCwgMjQwLCAyNTUsIDAuMjUpOwogIHBhZGRpbmc6IDNweCA4cHg7CiAgYm9yZGVyLXJhZGl1czogNnB4OwogIGZvbnQtc2l6ZTogMTFweDsKICBmb250LXdlaWdodDogNjAwOwogIGN1cnNvcjogcG9pbnRlcjsKICB0cmFuc2l0aW9uOiBhbGwgMC4xNXMgZWFzZTsKfQoKLnBlZXItYWN0aW9uLWJ0bjpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWN5YW4pOwogIGNvbG9yOiAjMDUwNzBhOwp9CgovKiBTRUFSQ0ggKi8KLnNlYXJjaC13cmFwIHsKICBwYWRkaW5nOiAxMHB4IDE2cHg7Cn0KCi5zZWFyY2gtaW5wdXQtYm94IHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiA4cHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIHBhZGRpbmc6IDZweCAxMnB4Owp9Cgouc2VhcmNoLWljb24gewogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKICBmbGV4LXNocmluazogMDsKfQoKLnNlYXJjaC1pbnB1dC1ib3ggaW5wdXQgewogIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogIGJvcmRlcjogbm9uZTsKICBvdXRsaW5lOiBub25lOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtc2l6ZTogMTNweDsKICB3aWR0aDogMTAwJTsKfQoKLnNlYXJjaC1pbnB1dC1ib3ggaW5wdXQ6OnBsYWNlaG9sZGVyIHsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7Cn0KCi8qIENIQVQgTElTVCAqLwouY2hhdC1saXN0IHsKICBmbGV4OiAxOwogIG92ZXJmbG93LXk6IGF1dG87CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwp9CgouY2hhdC1pdGVtIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiAxMnB4OwogIHBhZGRpbmc6IDEycHggMTZweDsKICBib3JkZXItYm90dG9tOiAxcHggc29saWQgcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjAzKTsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYmFja2dyb3VuZCAwLjE1cyBlYXNlOwogIHBvc2l0aW9uOiByZWxhdGl2ZTsKfQoKLmNoYXQtaXRlbTpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctaG92ZXIpOwp9CgouY2hhdC1pdGVtLmFjdGl2ZSB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwp9CgouY2hhdC1pdGVtLmFjdGl2ZTo6YmVmb3JlIHsKICBjb250ZW50OiAnJzsKICBwb3NpdGlvbjogYWJzb2x1dGU7CiAgbGVmdDogMDsKICB0b3A6IDA7CiAgYm90dG9tOiAwOwogIHdpZHRoOiAzcHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWN5YW4pOwp9CgouYXZhdGFyIHsKICB3aWR0aDogNDRweDsKICBoZWlnaHQ6IDQ0cHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGJhY2tncm91bmQ6ICMxYTIyMzM7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgZm9udC1zaXplOiAxNXB4OwogIGZvbnQtd2VpZ2h0OiA3MDA7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGZsZXgtc2hyaW5rOiAwOwp9CgouY2hhdC1pdGVtLW1ldGEgewogIGZsZXg6IDE7CiAgbWluLXdpZHRoOiAwOwp9CgouY2hhdC1pdGVtLXRvcCB7CiAgZGlzcGxheTogZmxleDsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47CiAgYWxpZ24taXRlbXM6IGJhc2VsaW5lOwogIG1hcmdpbi1ib3R0b206IDNweDsKfQoKLmNoYXQtaXRlbS1uYW1lIHsKICBmb250LXNpemU6IDE0cHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5jaGF0LWl0ZW0tdGltZSB7CiAgZm9udC1zaXplOiAxMXB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKfQoKLmNoYXQtaXRlbS1wcmV2aWV3IHsKICBkaXNwbGF5OiBmbGV4OwogIGp1c3RpZnktY29udGVudDogc3BhY2UtYmV0d2VlbjsKICBhbGlnbi1pdGVtczogY2VudGVyOwp9CgoucHJldmlldy10ZXh0IHsKICBmb250LXNpemU6IDEycHg7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7CiAgZmxleDogMTsKfQoKLnVucmVhZC1iYWRnZSB7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWN5YW4pOwogIGNvbG9yOiAjMDUwNzBhOwogIHBhZGRpbmc6IDFweCA2cHg7CiAgYm9yZGVyLXJhZGl1czogMTBweDsKICBmb250LXNpemU6IDExcHg7CiAgZm9udC13ZWlnaHQ6IDgwMDsKICBtYXJnaW4tbGVmdDogOHB4Owp9CgovKiBTSURFQkFSIEZPT1RFUiAqLwouc2lkZWJhci1mb290ZXIgewogIHBhZGRpbmc6IDEycHggMTZweDsKICBib3JkZXItdG9wOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1zdXJmYWNlKTsKfQoKLmRhbmdlci1idXJuLWJ0biB7CiAgd2lkdGg6IDEwMCU7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGdhcDogOHB4OwogIGJhY2tncm91bmQ6IHJnYmEoMjU1LCA1OSwgNDgsIDAuMDgpOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCA1OSwgNDgsIDAuMik7CiAgY29sb3I6ICNmZjZiNmI7CiAgcGFkZGluZzogOHB4OwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5kYW5nZXItYnVybi1idG46aG92ZXIgewogIGJhY2tncm91bmQ6IHJnYmEoMjU1LCA1OSwgNDgsIDAuMTYpOwogIGJvcmRlci1jb2xvcjogdmFyKC0tYWNjZW50LWRhbmdlcik7Cn0KCi8qIENIQVQgTUFJTiBWSUVXICovCi5jaGF0LW1haW4gewogIGZsZXg6IDE7CiAgaGVpZ2h0OiAxMDAlOwogIGJhY2tncm91bmQ6IHZhcigtLWJnLWRlZXApOwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBwb3NpdGlvbjogcmVsYXRpdmU7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKfQoKLmNoYXQtaGVhZGVyIHsKICBoZWlnaHQ6IHZhcigtLWhlYWRlci1oZWlnaHQpOwogIHBhZGRpbmc6IDAgMTZweDsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1zdXJmYWNlKTsKICBib3JkZXItYm90dG9tOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwogIGZsZXgtc2hyaW5rOiAwOwogIHotaW5kZXg6IDU7Cn0KCi5oZWFkZXItbGVmdC1jb2wgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDEycHg7CiAgbWluLXdpZHRoOiAwOwp9CgouYmFjay1uYXYtYnRuIHsKICBkaXNwbGF5OiBub25lOwogIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogIGJvcmRlcjogbm9uZTsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB3aWR0aDogMzZweDsKICBoZWlnaHQ6IDM2cHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGN1cnNvcjogcG9pbnRlcjsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIHRyYW5zaXRpb246IGJhY2tncm91bmQgMC4xNXMgZWFzZTsKfQoKLmJhY2stbmF2LWJ0bjpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctaG92ZXIpOwp9CgouYWN0aXZlLWF2YXRhciB7CiAgd2lkdGg6IDM4cHg7CiAgaGVpZ2h0OiAzOHB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiAjMWEyMjMzOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtc2l6ZTogMTRweDsKICBmb250LXdlaWdodDogNzAwOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBmbGV4LXNocmluazogMDsKfQoKLmFjdGl2ZS1tZXRhIHsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgbWluLXdpZHRoOiAwOwp9CgouYWN0aXZlLXRpdGxlLXJvdyB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4Owp9CgouYWN0aXZlLWNoYXQtdGl0bGUgewogIGZvbnQtc2l6ZTogMTRweDsKICBmb250LXdlaWdodDogNzAwOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKICB0ZXh0LW92ZXJmbG93OiBlbGxpcHNpczsKfQoKLmFjdGl2ZS1sb2NrLWJhZGdlIHsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKfQoKLmFjdGl2ZS1jaGF0LXN0YXR1cyB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4OwogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwogIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKICB0ZXh0LW92ZXJmbG93OiBlbGxpcHNpczsKfQoKLnN0YXR1cy1pbmRpY2F0b3ItZG90IHsKICB3aWR0aDogNnB4OwogIGhlaWdodDogNnB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtZW1lcmFsZCk7Cn0KCi5oZWFkZXItcmlnaHQtYWN0aW9ucyB7CiAgZGlzcGxheTogZmxleDsKICBnYXA6IDhweDsKfQoKLmNoYXQtdG9vbC1idG4gewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDZweDsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1lbGV2YXRlZCk7CiAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwogIHBhZGRpbmc6IDZweCAxMHB4OwogIGJvcmRlci1yYWRpdXM6IDZweDsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5jaGF0LXRvb2wtYnRuOmhvdmVyIHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1ob3Zlcik7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7Cn0KCi8qIE1FU1NBR0VTIFZJRVdQT1JUICovCi5tZXNzYWdlcy12aWV3cG9ydCB7CiAgZmxleDogMTsKICBvdmVyZmxvdy15OiBhdXRvOwogIHBhZGRpbmc6IDE2cHg7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogMTBweDsKfQoKLmRhdGUtYmFkZ2Utd3JhcCB7CiAgZGlzcGxheTogZmxleDsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBtYXJnaW46IDZweCAwOwp9CgouZGF0ZS1iYWRnZSB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgY29sb3I6IHZhcigtLXRleHQtbXV0ZWQpOwogIGZvbnQtc2l6ZTogMTBweDsKICBmb250LXdlaWdodDogNzAwOwogIGxldHRlci1zcGFjaW5nOiAwLjhweDsKICBwYWRkaW5nOiAzcHggMTBweDsKICBib3JkZXItcmFkaXVzOiAxMnB4Owp9CgouZW5jcnlwdGlvbi1iYW5uZXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBnYXA6IDhweDsKICBwYWRkaW5nOiA4cHggMTRweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDI0MCwgMjU1LCAwLjA0KTsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDI0MCwgMjU1LCAwLjE1KTsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgZm9udC1zaXplOiAxMXB4OwogIGNvbG9yOiB2YXIoLS1hY2NlbnQtY3lhbik7CiAgbWFyZ2luLWJvdHRvbTogNnB4Owp9CgovKiBNRVNTQUdFIEJVQkJMRVMgKFRlbGVncmFtLWdyYWRlKSAqLwoubWVzc2FnZS1yb3cgewogIGRpc3BsYXk6IGZsZXg7CiAgd2lkdGg6IDEwMCU7Cn0KCi5tZXNzYWdlLXJvdy5pbmNvbWluZyB7CiAganVzdGlmeS1jb250ZW50OiBmbGV4LXN0YXJ0Owp9CgoubWVzc2FnZS1yb3cub3V0Z29pbmcgewogIGp1c3RpZnktY29udGVudDogZmxleC1lbmQ7Cn0KCi5idWJibGUgewogIG1heC13aWR0aDogNzUlOwogIG1pbi13aWR0aDogODBweDsKICBwYWRkaW5nOiA4cHggMTJweDsKICBib3JkZXItcmFkaXVzOiAxNHB4OwogIHBvc2l0aW9uOiByZWxhdGl2ZTsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZ2FwOiA0cHg7CiAgd29yZC1icmVhazogYnJlYWstd29yZDsKfQoKLm1lc3NhZ2Utcm93LmluY29taW5nIC5idWJibGUgewogIGJhY2tncm91bmQ6IHZhcigtLW1zZy1pbi1iZyk7CiAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tbXNnLWluLWJvcmRlcik7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgYm9yZGVyLWJvdHRvbS1sZWZ0LXJhZGl1czogNHB4Owp9CgoubWVzc2FnZS1yb3cub3V0Z29pbmcgLmJ1YmJsZSB7CiAgYmFja2dyb3VuZDogdmFyKC0tbXNnLW91dC1iZyk7CiAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tbXNnLW91dC1ib3JkZXIpOwogIGNvbG9yOiAjZmZmZmZmOwogIGJvcmRlci1ib3R0b20tcmlnaHQtcmFkaXVzOiA0cHg7Cn0KCi5idWJibGUtYXV0aG9yIHsKICBmb250LXNpemU6IDExcHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIG1hcmdpbi1ib3R0b206IDJweDsKfQoKLmJ1YmJsZS10ZXh0IHsKICBmb250LXNpemU6IDEzLjVweDsKICBsaW5lLWhlaWdodDogMS40NTsKfQoKLmJ1YmJsZS1mb290ZXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGZsZXgtZW5kOwogIGdhcDogNHB4OwogIG1hcmdpbi10b3A6IDJweDsKICBhbGlnbi1zZWxmOiBmbGV4LWVuZDsKfQoKLmJ1YmJsZS10aW1lIHsKICBmb250LXNpemU6IDEwcHg7CiAgY29sb3I6IHZhcigtLXRleHQtbXV0ZWQpOwp9CgoubWVzc2FnZS1yb3cub3V0Z29pbmcgLmJ1YmJsZS10aW1lIHsKICBjb2xvcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjYpOwp9CgoudGljay1pY29uIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKfQoKLyogRklMRSBBVFRBQ0hNRU5UIENBUkQgSU4gQlVCQkxFICovCi5idWJibGUtZmlsZS1jYXJkIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiAxMHB4OwogIHBhZGRpbmc6IDhweCAxMHB4OwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMCwgMCwgMC4yNSk7CiAgYm9yZGVyOiAxcHggc29saWQgcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA4KTsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgbWFyZ2luLXRvcDogNHB4Owp9CgouZmlsZS1pY29uLWJveCB7CiAgd2lkdGg6IDM2cHg7CiAgaGVpZ2h0OiAzNnB4OwogIGJvcmRlci1yYWRpdXM6IDZweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDI0MCwgMjU1LCAwLjEpOwogIGNvbG9yOiB2YXIoLS1hY2NlbnQtY3lhbik7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGZsZXgtc2hyaW5rOiAwOwp9CgouZmlsZS1jYXJkLWRldGFpbHMgewogIGZsZXg6IDE7CiAgbWluLXdpZHRoOiAwOwp9CgouZmlsZS1jYXJkLW5hbWUgewogIGZvbnQtc2l6ZTogMTJweDsKICBmb250LXdlaWdodDogNjAwOwogIGNvbG9yOiAjZmZmZmZmOwogIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKICB0ZXh0LW92ZXJmbG93OiBlbGxpcHNpczsKfQoKLmZpbGUtY2FyZC1zaXplIHsKICBmb250LXNpemU6IDEwLjVweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7Cn0KCi5maWxlLWRvd25sb2FkLWxpbmsgewogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMTUpOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMCwgMjQwLCAyNTUsIDAuMyk7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBwYWRkaW5nOiA1cHggOHB4OwogIGJvcmRlci1yYWRpdXM6IDZweDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgdGV4dC1kZWNvcmF0aW9uOiBub25lOwogIGN1cnNvcjogcG9pbnRlcjsKICB0cmFuc2l0aW9uOiBhbGwgMC4xNXMgZWFzZTsKfQoKLmZpbGUtZG93bmxvYWQtbGluazpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWN5YW4pOwogIGNvbG9yOiAjMDUwNzBhOwp9CgouYnViYmxlLWltYWdlLXByZXZpZXcgewogIG1heC13aWR0aDogMTAwJTsKICBtYXgtaGVpZ2h0OiAyODBweDsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgb2JqZWN0LWZpdDogY292ZXI7CiAgbWFyZ2luLXRvcDogNHB4OwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4xKTsKfQoKLyogQ0hBVCBDT01QT1NFUiAqLwouY2hhdC1jb21wb3NlciB7CiAgcGFkZGluZzogMTBweCAxNnB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLXN1cmZhY2UpOwogIGJvcmRlci10b3A6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIHBhZGRpbmctYm90dG9tOiBtYXgoMTBweCwgZW52KHNhZmUtYXJlYS1pbnNldC1ib3R0b20pKTsKfQoKLmhpZGRlbi1maWxlLWlucHV0IHsKICBkaXNwbGF5OiBub25lICFpbXBvcnRhbnQ7Cn0KCi5jb21wb3Nlci1iYXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGZsZXgtZW5kOwogIGdhcDogOHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDIwcHg7CiAgcGFkZGluZzogNnB4IDEwcHg7CiAgdHJhbnNpdGlvbjogYm9yZGVyLWNvbG9yIDAuMTVzIGVhc2U7Cn0KCi5jb21wb3Nlci1iYXI6Zm9jdXMtd2l0aGluIHsKICBib3JkZXItY29sb3I6IHZhcigtLWJvcmRlci1mb2N1cyk7Cn0KCi5jb21wb3Nlci1hY3Rpb24tYnRuIHsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBjdXJzb3I6IHBvaW50ZXI7CiAgZmxleC1zaHJpbms6IDA7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5jb21wb3Nlci1hY3Rpb24tYnRuOmhvdmVyIHsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMSk7Cn0KCi5jb21wb3Nlci1iYXIgdGV4dGFyZWEgewogIGZsZXg6IDE7CiAgYmFja2dyb3VuZDogdHJhbnNwYXJlbnQ7CiAgYm9yZGVyOiBub25lOwogIG91dGxpbmU6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtc2Fucyk7CiAgZm9udC1zaXplOiAxMy41cHg7CiAgbGluZS1oZWlnaHQ6IDEuNDsKICByZXNpemU6IG5vbmU7CiAgbWF4LWhlaWdodDogMTIwcHg7CiAgcGFkZGluZzogNnB4IDRweDsKfQoKLmNvbXBvc2VyLWJhciB0ZXh0YXJlYTo6cGxhY2Vob2xkZXIgewogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKfQoKLmNvbXBvc2VyLXNlbmQtYnRuIHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtYXp1cmUpOwogIGJvcmRlcjogbm9uZTsKICBjb2xvcjogI2ZmZmZmZjsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBjdXJzb3I6IHBvaW50ZXI7CiAgZmxleC1zaHJpbms6IDA7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5jb21wb3Nlci1zZW5kLWJ0bjpob3ZlciB7CiAgYmFja2dyb3VuZDogIzAwNjZkNjsKICB0cmFuc2Zvcm06IHNjYWxlKDEuMDQpOwp9CgouY29tcG9zZXItc2VuZC1idG46YWN0aXZlIHsKICB0cmFuc2Zvcm06IHNjYWxlKDAuOTYpOwp9CgovKiBNT0RBTFMgKi8KLm1vZGFsLW92ZXJsYXkgewogIHBvc2l0aW9uOiBmaXhlZDsKICBpbnNldDogMDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDAsIDAsIDAuNzUpOwogIGJhY2tkcm9wLWZpbHRlcjogYmx1cig4cHgpOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICB6LWluZGV4OiAxMDA7CiAgcGFkZGluZzogMTZweDsKfQoKLm1vZGFsLW92ZXJsYXkuaGlkZGVuIHsKICBkaXNwbGF5OiBub25lICFpbXBvcnRhbnQ7Cn0KCi5tb2RhbC1jYXJkIHsKICB3aWR0aDogMTAwJTsKICBtYXgtd2lkdGg6IDQ0MHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLXN1cmZhY2UpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogMTRweDsKICBib3gtc2hhZG93OiAwIDIwcHggNDBweCByZ2JhKDAsIDAsIDAsIDAuNik7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKICBhbmltYXRpb246IG1vZGFsLWFwcGVhciAwLjJzIGN1YmljLWJlemllcigwLjE2LCAxLCAwLjMsIDEpOwp9CgpAa2V5ZnJhbWVzIG1vZGFsLWFwcGVhciB7CiAgZnJvbSB7IG9wYWNpdHk6IDA7IHRyYW5zZm9ybTogc2NhbGUoMC45NSk7IH0KICB0byB7IG9wYWNpdHk6IDE7IHRyYW5zZm9ybTogc2NhbGUoMSk7IH0KfQoKLm1vZGFsLWhlYWRlciB7CiAgcGFkZGluZzogMTRweCAxOHB4OwogIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47Cn0KCi5tb2RhbC10aXRsZSB7CiAgZm9udC1zaXplOiAxNXB4OwogIGZvbnQtd2VpZ2h0OiA3MDA7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7Cn0KCi5tb2RhbC1jbG9zZS1idG4gewogIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogIGJvcmRlcjogbm9uZTsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgY3Vyc29yOiBwb2ludGVyOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKfQoKLm1vZGFsLWNsb3NlLWJ0bjpob3ZlciB7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7Cn0KCi5tb2RhbC1ib2R5IHsKICBwYWRkaW5nOiAxOHB4OwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDE0cHg7Cn0KCi5tb2RhbC1kZXNjcmlwdGlvbiB7CiAgZm9udC1zaXplOiAxM3B4OwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7Cn0KCi5mb3JtLWdyb3VwIHsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZ2FwOiA2cHg7Cn0KCi5mb3JtLWxhYmVsIHsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwp9CgouZm9ybS1pbnB1dCB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIHBhZGRpbmc6IDlweCAxMnB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtc2l6ZTogMTNweDsKICBmb250LWZhbWlseTogdmFyKC0tZm9udC1zYW5zKTsKICBvdXRsaW5lOiBub25lOwp9CgouZm9ybS1pbnB1dDpmb2N1cyB7CiAgYm9yZGVyLWNvbG9yOiB2YXIoLS1ib3JkZXItZm9jdXMpOwp9CgouY29kZS1ib3gtZGlzcGxheSB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIHBhZGRpbmc6IDlweCAxMnB4OwogIGZvbnQtZmFtaWx5OiB2YXIoLS1mb250LW1vbm8pOwogIGZvbnQtc2l6ZTogMTJweDsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIHdvcmQtYnJlYWs6IGJyZWFrLWFsbDsKfQoKLmZpbmdlcnByaW50LWRpc3BsYXkgewogIGJhY2tncm91bmQ6ICMwMDAwMDA7CiAgYm9yZGVyOiAxcHggc29saWQgcmdiYSgwLCAyNDAsIDI1NSwgMC4yKTsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgcGFkZGluZzogMTRweDsKICBmb250LWZhbWlseTogdmFyKC0tZm9udC1tb25vKTsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBsZXR0ZXItc3BhY2luZzogMS41cHg7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICB0ZXh0LWFsaWduOiBjZW50ZXI7CiAgbGluZS1oZWlnaHQ6IDEuODsKfQoKLmNyeXB0by1tZXRhLWxpc3QgewogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDZweDsKfQoKLmNyeXB0by1tZXRhLWl0ZW0gewogIGRpc3BsYXk6IGZsZXg7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwogIGZvbnQtc2l6ZTogMTJweDsKfQoKLm1ldGEtbGFiZWwgewogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKfQoKLm1ldGEtdmFsIHsKICBmb250LXdlaWdodDogNjAwOwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7Cn0KCi5tb2RhbC1mb290ZXIgewogIHBhZGRpbmc6IDEycHggMThweDsKICBib3JkZXItdG9wOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBkaXNwbGF5OiBmbGV4OwogIGp1c3RpZnktY29udGVudDogZmxleC1lbmQ7CiAgZ2FwOiAxMHB4Owp9CgouYnRuLXByaW1hcnkgewogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1henVyZSk7CiAgY29sb3I6ICNmZmZmZmY7CiAgYm9yZGVyOiBub25lOwogIHBhZGRpbmc6IDhweCAxNnB4OwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5idG4tcHJpbWFyeTpob3ZlciB7CiAgYmFja2dyb3VuZDogIzAwNjZkNjsKfQoKLmJ0bi1zZWNvbmRhcnkgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgcGFkZGluZzogOHB4IDE0cHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGZvbnQtc2l6ZTogMTNweDsKICBmb250LXdlaWdodDogNjAwOwogIGN1cnNvcjogcG9pbnRlcjsKICB0cmFuc2l0aW9uOiBhbGwgMC4xNXMgZWFzZTsKfQoKLmJ0bi1zZWNvbmRhcnk6aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWhvdmVyKTsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLmJ0bi1vdXRsaW5lLWRhbmdlciB7CiAgYmFja2dyb3VuZDogdHJhbnNwYXJlbnQ7CiAgY29sb3I6ICNmZjZiNmI7CiAgYm9yZGVyOiAxcHggc29saWQgcmdiYSgyNTUsIDU5LCA0OCwgMC4zKTsKICBwYWRkaW5nOiA4cHggMTJweDsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY3Vyc29yOiBwb2ludGVyOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouYnRuLW91dGxpbmUtZGFuZ2VyOmhvdmVyIHsKICBiYWNrZ3JvdW5kOiByZ2JhKDI1NSwgNTksIDQ4LCAwLjEpOwogIGJvcmRlci1jb2xvcjogdmFyKC0tYWNjZW50LWRhbmdlcik7Cn0KCi5kaXNjb3ZlcmVkLXBlZXJzLXF1aWNrIHsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZ2FwOiA2cHg7CiAgbWF4LWhlaWdodDogMTIwcHg7CiAgb3ZlcmZsb3cteTogYXV0bzsKfQoKLyogVE9BU1QgTk9USUZJQ0FUSU9OICovCi50b2FzdC1ub3RpZmljYXRpb24gewogIHBvc2l0aW9uOiBmaXhlZDsKICBib3R0b206IDI0cHg7CiAgbGVmdDogNTAlOwogIHRyYW5zZm9ybTogdHJhbnNsYXRlWCgtNTAlKTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1lbGV2YXRlZCk7CiAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBib3gtc2hhZG93OiAwIDhweCAyNHB4IHJnYmEoMCwgMCwgMCwgMC42KTsKICBib3JkZXItcmFkaXVzOiAyMHB4OwogIHBhZGRpbmc6IDhweCAxNnB4OwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDhweDsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB6LWluZGV4OiAyMDA7CiAgcG9pbnRlci1ldmVudHM6IG5vbmU7CiAgYW5pbWF0aW9uOiB0b2FzdC1mYWRlIDAuMnMgY3ViaWMtYmV6aWVyKDAuMTYsIDEsIDAuMywgMSk7Cn0KCi50b2FzdC1ub3RpZmljYXRpb24uaGlkZGVuIHsKICBkaXNwbGF5OiBub25lICFpbXBvcnRhbnQ7Cn0KCi50b2FzdC1kb3QgewogIHdpZHRoOiA2cHg7CiAgaGVpZ2h0OiA2cHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKfQoKQGtleWZyYW1lcyB0b2FzdC1mYWRlIHsKICBmcm9tIHsgb3BhY2l0eTogMDsgdHJhbnNmb3JtOiB0cmFuc2xhdGUoLTUwJSwgMTBweCk7IH0KICB0byB7IG9wYWNpdHk6IDE7IHRyYW5zZm9ybTogdHJhbnNsYXRlKC01MCUsIDApOyB9Cn0KCi8qIE1PQklMRSBBREFQVEFUSU9OICg8IDc2OHB4KSAqLwpAbWVkaWEgKG1heC13aWR0aDogNzY4cHgpIHsKICAuc2lkZWJhciB7CiAgICB3aWR0aDogMTAwdnc7CiAgICBwb3NpdGlvbjogYWJzb2x1dGU7CiAgICBpbnNldDogMDsKICAgIHotaW5kZXg6IDIwOwogIH0KCiAgLmNoYXQtbWFpbiB7CiAgICB3aWR0aDogMTAwdnc7CiAgICBwb3NpdGlvbjogYWJzb2x1dGU7CiAgICBpbnNldDogMDsKICAgIHotaW5kZXg6IDEwOwogIH0KCiAgLmJhY2stbmF2LWJ0biB7CiAgICBkaXNwbGF5OiBmbGV4OwogIH0KCiAgLmFwcC1sYXlvdXQubW9iaWxlLWNoYXQtb3BlbiAuc2lkZWJhciB7CiAgICB0cmFuc2Zvcm06IHRyYW5zbGF0ZVgoLTEwMCUpOwogIH0KCiAgLmFwcC1sYXlvdXQubW9iaWxlLWNoYXQtb3BlbiAuY2hhdC1tYWluIHsKICAgIHotaW5kZXg6IDMwOwogIH0KCiAgLnRvb2wtYnRuLXRleHQgewogICAgZGlzcGxheTogbm9uZTsKICB9Cn0K"));
                }
                return _css;
            }
        }

        public static string JS
        {
            get
            {
                if (_js == null)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"packages\desktop\src\renderer\app.js");
                    if (File.Exists(path)) _js = File.ReadAllText(path, Encoding.UTF8);
                    else _js = Encoding.UTF8.GetString(Convert.FromBase64String("Ly8gU3lwaGVyIENsaWVudCBDb3JlIOKAlCBPZmZsaW5lIEJMRSBNZXNoLCBEdWFsIFVEUCBSYWRpbyAmIEUyRUUgTWVzc2VuZ2VyCihmdW5jdGlvbiAoKSB7CiAgJ3VzZSBzdHJpY3QnOwoKICAvLyAxLiDQo9C90LjQutCw0LvRjNC90LDRjyDQuNC00LXQvdGC0LjRhNC40LrQsNGG0LjRjyDRg9C30LvQsAogIGZ1bmN0aW9uIGdldERldmljZVBsYXRmb3JtUHJlZml4KCkgewogICAgdmFyIHVhID0gbmF2aWdhdG9yLnVzZXJBZ2VudCB8fCAnJzsKICAgIGlmICh3aW5kb3cuQW5kcm9pZEJyaWRnZSkgcmV0dXJuICdzeXAtZHJvaWQtJzsKICAgIGlmICgvQW5kcm9pZC9pLnRlc3QodWEpKSByZXR1cm4gJ3N5cC1kcm9pZC0nOwogICAgaWYgKC9pUGhvbmV8aVBhZHxpUG9kL2kudGVzdCh1YSkpIHJldHVybiAnc3lwLWlvcy0nOwogICAgaWYgKC9NYWMvaS50ZXN0KHVhKSkgcmV0dXJuICdzeXAtbWFjLSc7CiAgICBpZiAoL0xpbnV4L2kudGVzdCh1YSkpIHJldHVybiAnc3lwLWxpbi0nOwogICAgcmV0dXJuICdzeXAtd2luLSc7CiAgfQoKICBmdW5jdGlvbiBnZW5lcmF0ZVNlY3VyZUhleChieXRlc0xlbmd0aCkgewogICAgdmFyIHJhbmQgPSBuZXcgVWludDhBcnJheShieXRlc0xlbmd0aCk7CiAgICBpZiAod2luZG93LmNyeXB0byAmJiB3aW5kb3cuY3J5cHRvLmdldFJhbmRvbVZhbHVlcykgewogICAgICB3aW5kb3cuY3J5cHRvLmdldFJhbmRvbVZhbHVlcyhyYW5kKTsKICAgIH0gZWxzZSB7CiAgICAgIGZvciAodmFyIGkgPSAwOyBpIDwgYnl0ZXNMZW5ndGg7IGkrKykgewogICAgICAgIHJhbmRbaV0gPSBNYXRoLmZsb29yKE1hdGgucmFuZG9tKCkgKiAyNTYpOwogICAgICB9CiAgICB9CiAgICB2YXIgaGV4ID0gJyc7CiAgICBmb3IgKHZhciBqID0gMDsgaiA8IGJ5dGVzTGVuZ3RoOyBqKyspIHsKICAgICAgdmFyIGIgPSByYW5kW2pdLnRvU3RyaW5nKDE2KTsKICAgICAgaGV4ICs9IGIubGVuZ3RoID09PSAxID8gJzAnICsgYiA6IGI7CiAgICB9CiAgICByZXR1cm4gaGV4OwogIH0KCiAgZnVuY3Rpb24gZ2V0QXZhdGFyQ29sb3Ioc3RyKSB7CiAgICB2YXIgY29sb3JzID0gWwogICAgICBbJyMwMDdhZmYnLCAnIzAwZjBmZiddLAogICAgICBbJyM3OTI4Y2EnLCAnI2ZmMDA4MCddLAogICAgICBbJyMwMGU2NzYnLCAnIzAwYjBmZiddLAogICAgICBbJyNmZjkxMDAnLCAnI2ZmNTI1MiddLAogICAgICBbJyMxZGU5YjYnLCAnIzFkYzRlOSddLAogICAgICBbJyM2NTFmZmYnLCAnI2Q1MDBmOSddLAogICAgXTsKICAgIHZhciBoYXNoID0gMDsKICAgIGZvciAodmFyIGkgPSAwOyBpIDwgc3RyLmxlbmd0aDsgaSsrKSB7CiAgICAgIGhhc2ggPSBzdHIuY2hhckNvZGVBdChpKSArICgoaGFzaCA8PCA1KSAtIGhhc2gpOwogICAgfQogICAgdmFyIGlkeCA9IE1hdGguYWJzKGhhc2gpICUgY29sb3JzLmxlbmd0aDsKICAgIHJldHVybiBjb2xvcnNbaWR4XTsKICB9CgogIGZ1bmN0aW9uIGdldE9yQ3JlYXRlSWRlbnRpdHkoKSB7CiAgICB2YXIgU1RPUkFHRV9LRVkgPSAnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnOwogICAgdmFyIHN0b3JlZCA9IGxvY2FsU3RvcmFnZS5nZXRJdGVtKFNUT1JBR0VfS0VZKTsKICAgIGlmIChzdG9yZWQpIHsKICAgICAgdHJ5IHsKICAgICAgICB2YXIgcGFyc2VkID0gSlNPTi5wYXJzZShzdG9yZWQpOwogICAgICAgIGlmIChwYXJzZWQubXlJZCAmJiBwYXJzZWQubXlGaW5nZXJwcmludCkgcmV0dXJuIHBhcnNlZDsKICAgICAgfSBjYXRjaCAoZSkge30KICAgIH0KCiAgICB2YXIgcHJlZml4ID0gZ2V0RGV2aWNlUGxhdGZvcm1QcmVmaXgoKTsKICAgIHZhciBoZXggPSBnZW5lcmF0ZVNlY3VyZUhleCgxNik7CiAgICB2YXIgbXlJZCA9IHByZWZpeCArIGhleC5zdWJzdHJpbmcoMCwgOCk7CgogICAgdmFyIGZwUGFydHMgPSBbXTsKICAgIGZvciAodmFyIGsgPSAwOyBrIDwgODsgaysrKSB7CiAgICAgIGZwUGFydHMucHVzaChoZXguc3Vic3RyaW5nKGsgKiA0LCBrICogNCArIDQpLnRvVXBwZXJDYXNlKCkpOwogICAgfQogICAgdmFyIG15RmluZ2VycHJpbnQgPSBmcFBhcnRzLmpvaW4oJyAnKTsKCiAgICB2YXIgZGlzcGxheU5hbWUgPSAn0KPQt9C10LsgJyArIG15SWQucmVwbGFjZSgnc3lwLScsICcnKS50b1VwcGVyQ2FzZSgpOwoKICAgIHZhciBpZGVudGl0eSA9IHsKICAgICAgbXlJZDogbXlJZCwKICAgICAgZGlzcGxheU5hbWU6IGRpc3BsYXlOYW1lLAogICAgICBteUZpbmdlcnByaW50OiBteUZpbmdlcnByaW50LAogICAgfTsKICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKFNUT1JBR0VfS0VZLCBKU09OLnN0cmluZ2lmeShpZGVudGl0eSkpOwogICAgcmV0dXJuIGlkZW50aXR5OwogIH0KCiAgLy8gMi4g0KXRgNCw0L3QuNC70LjRidC1INC00LjQsNC70L7Qs9C+0LIgKNCi0J7Qm9Cs0JrQniDQntCU0JjQnSDQutCw0L3QsNC7INC/0L4g0YPQvNC+0LvRh9Cw0L3QuNGOIOKAlCDQvtCx0YnQuNC5INGN0YTQuNGAKQogIGZ1bmN0aW9uIGxvYWRTdG9yZWRDaGF0cygpIHsKICAgIHZhciBTVE9SQUdFX0tFWSA9ICdzeXBoZXJfY2hhdHNfdjMnOwogICAgdmFyIHN0b3JlZCA9IGxvY2FsU3RvcmFnZS5nZXRJdGVtKFNUT1JBR0VfS0VZKTsKICAgIGlmIChzdG9yZWQpIHsKICAgICAgdHJ5IHsKICAgICAgICB2YXIgcGFyc2VkID0gSlNPTi5wYXJzZShzdG9yZWQpOwogICAgICAgIGlmIChBcnJheS5pc0FycmF5KHBhcnNlZCkgJiYgcGFyc2VkLmxlbmd0aCA+IDApIHJldHVybiBwYXJzZWQ7CiAgICAgIH0gY2F0Y2ggKGUpIHt9CiAgICB9CgogICAgcmV0dXJuIFsKICAgICAgewogICAgICAgIGlkOiAnbWVzaC1icm9hZGNhc3QnLAogICAgICAgIG5hbWU6ICfQntCx0YnQuNC5INGA0LDQtNC40L7RjdGE0LjRgCBNZXNoJywKICAgICAgICBhdmF0YXI6ICdNJywKICAgICAgICBzdGF0dXM6ICfQqNC40YDQvtC60L7QstC10YnQsNGC0LXQu9GM0L3Ri9C5INC60LDQvdCw0LsgKFVEUCAmIEJMRSknLAogICAgICAgIGxhc3RNZXNzYWdlOiAn0KDQsNC00LjQvtC60LDQvdCw0Lsg0LPQvtGC0L7QsiDQuiDQvtCx0LzQtdC90YMg0L/QsNC60LXRgtCw0LzQuCcsCiAgICAgICAgdGltZTogJ9Ct0YTQuNGAJywKICAgICAgICB1bnJlYWQ6IDAsCiAgICAgICAgbWVzc2FnZXM6IFsKICAgICAgICAgIHsKICAgICAgICAgICAgaWQ6ICdzeXNfc3RhcnQnLAogICAgICAgICAgICBzZW5kZXI6ICdzeXN0ZW0nLAogICAgICAgICAgICBhdXRob3JOYW1lOiAn0KHQmNCh0KLQldCc0JAnLAogICAgICAgICAgICB0eXBlOiAndGV4dCcsCiAgICAgICAgICAgIHRleHQ6ICfQkNCy0YLQvtC90L7QvNC90YvQuSDRgNCw0LTQuNC+0LrQsNC90LDQuyDQsNC60YLQuNCy0LXQvS4g0KHQvtC+0LHRidC10L3QuNGPINC/0LXRgNC10LTQsNGO0YLRgdGPINC90LDQv9GA0Y/QvNGD0Y4g0YfQtdGA0LXQtyBXaS1GaSBVRFAg0LggQmx1ZXRvb3RoIExvdyBFbmVyZ3kg0LHQtdC3INGB0LXRgNCy0LXRgNC+0LIg0Lgg0LjQvdGC0LXRgNC90LXRgtCwLicsCiAgICAgICAgICAgIHRpbWU6IGZvcm1hdFRpbWUobmV3IERhdGUoKSksCiAgICAgICAgICAgIGlzT3V0Z29pbmc6IGZhbHNlLAogICAgICAgICAgfSwKICAgICAgICBdLAogICAgICB9LAogICAgXTsKICB9CgogIGZ1bmN0aW9uIHNhdmVDaGF0cygpIHsKICAgIHRyeSB7CiAgICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKCdzeXBoZXJfY2hhdHNfdjMnLCBKU09OLnN0cmluZ2lmeShzdGF0ZS5jaGF0cykpOwogICAgfSBjYXRjaCAoZSkge30KICB9CgogIHZhciBpZGVudGl0eSA9IGdldE9yQ3JlYXRlSWRlbnRpdHkoKTsKCiAgdmFyIHN0YXRlID0gewogICAgaWRlbnRpdHk6IGlkZW50aXR5LAogICAgYWN0aXZlQ2hhdElkOiAnbWVzaC1icm9hZGNhc3QnLAogICAgc2VsZkRlc3RydWN0SW5kZXg6IDEsCiAgICBzZWxmRGVzdHJ1Y3RPcHRpb25zOiBbJzHRhycsICcyNNGHJywgJzfQtCcsICfQktGL0LrQuyddLAogICAgc2VhcmNoUXVlcnk6ICcnLAogICAgbG9jYWxJcDogJzEyNy4wLjAuMScsCiAgICBrbm93blBlZXJJcHM6IG5ldyBTZXQoKSwKICAgIGRpc2NvdmVyZWRQZWVyczoge30sIC8vIHBlZXJJZCAtPiB7IGlkLCBuYW1lLCB0cmFuc3BvcnQsIHJzc2ksIGRpc3RhbmNlLCBpcCwgbWFjLCBsYXN0U2VlbiB9CiAgICBjaGF0czogbG9hZFN0b3JlZENoYXRzKCksCiAgfTsKCiAgdmFyIHNpbmdsZUNoZWNrU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJ0aWNrLWljb24iIHdpZHRoPSIxMyIgaGVpZ2h0PSIxMyIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9ImN1cnJlbnRDb2xvciIgc3Ryb2tlLXdpZHRoPSIyLjUiPjxwb2x5bGluZSBwb2ludHM9IjIwIDYgOSAxNyA0IDEyIi8+PC9zdmc+JzsKCiAgdmFyIGRvdWJsZUNoZWNrU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJ0aWNrLWljb24iIHdpZHRoPSIxNSIgaGVpZ2h0PSIxNSIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9InZhcigtLWFjY2VudC1jeWFuKSIgc3Ryb2tlLXdpZHRoPSIyLjUiPjxwYXRoIGQ9Ik0xOCA2TDcgMTdsLTUtNSIvPjxwYXRoIGQ9Ik0yMiAxMGwtNy41IDcuNUwxMyAxNiIvPjwvc3ZnPic7CgogIHZhciBkb2NJY29uU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjIwIiBoZWlnaHQ9IjIwIiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPjxwYXRoIGQ9Ik0xNCAySDZhMiAyIDAgMCAwLTIgMnYxNmEyIDIgMCAwIDAgMiAyaDEyYTIgMiAwIDAgMCAyLTJWOHoiLz48cG9seWxpbmUgcG9pbnRzPSIxNCAyIDE0IDggMjAgOCIvPjwvc3ZnPic7CgogIHZhciBkb3dubG9hZEljb25TdmcgPQogICAgJzxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTMiIGhlaWdodD0iMTMiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMi41Ij48cGF0aCBkPSJNMjEgMTV2NGEyIDIgMCAwIDEtMiAySDVhMiAyIDAgMCAxLTItMnYtNCIvPjxwb2x5bGluZSBwb2ludHM9IjcgMTAgMTIgMTUgMTcgMTAiLz48bGluZSB4MT0iMTIiIHkxPSIxNSIgeDI9IjEyIiB5Mj0iMyIvPjwvc3ZnPic7CgogIHZhciB0b2FzdFRpbWVyID0gbnVsbDsKCiAgZnVuY3Rpb24gc2hvd1RvYXN0KG1lc3NhZ2UpIHsKICAgIHZhciB0b2FzdCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCd0b2FzdE5vdGlmaWNhdGlvbicpOwogICAgdmFyIHRvYXN0VGV4dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCd0b2FzdFRleHQnKTsKICAgIGlmICghdG9hc3QgfHwgIXRvYXN0VGV4dCkgcmV0dXJuOwogICAgdG9hc3RUZXh0LnRleHRDb250ZW50ID0gbWVzc2FnZTsKICAgIHRvYXN0LmNsYXNzTGlzdC5yZW1vdmUoJ2hpZGRlbicpOwogICAgaWYgKHRvYXN0VGltZXIpIGNsZWFyVGltZW91dCh0b2FzdFRpbWVyKTsKICAgIHRvYXN0VGltZXIgPSBzZXRUaW1lb3V0KGZ1bmN0aW9uICgpIHsKICAgICAgdG9hc3QuY2xhc3NMaXN0LmFkZCgnaGlkZGVuJyk7CiAgICB9LCAyODAwKTsKICB9CgogIGZ1bmN0aW9uIGZvcm1hdFRpbWUoZCkgewogICAgcmV0dXJuICgKICAgICAgU3RyaW5nKGQuZ2V0SG91cnMoKSkucGFkU3RhcnQoMiwgJzAnKSArICc6JyArIFN0cmluZyhkLmdldE1pbnV0ZXMoKSkucGFkU3RhcnQoMiwgJzAnKQogICAgKTsKICB9CgogIGZ1bmN0aW9uIGZvcm1hdEZpbGVTaXplKGJ5dGVzKSB7CiAgICBpZiAoIWJ5dGVzIHx8IGJ5dGVzID09PSAwKSByZXR1cm4gJzAgQic7CiAgICB2YXIgayA9IDEwMjQ7CiAgICB2YXIgc2l6ZXMgPSBbJ0InLCAnS0InLCAnTUInLCAnR0InXTsKICAgIHZhciBpID0gTWF0aC5mbG9vcihNYXRoLmxvZyhieXRlcykgLyBNYXRoLmxvZyhrKSk7CiAgICByZXR1cm4gcGFyc2VGbG9hdCgoYnl0ZXMgLyBNYXRoLnBvdyhrLCBpKSkudG9GaXhlZCgxKSkgKyAnICcgKyBzaXplc1tpXTsKICB9CgogIGZ1bmN0aW9uIGVzY2FwZUh0bWwoc3RyKSB7CiAgICBpZiAoIXN0cikgcmV0dXJuICcnOwogICAgcmV0dXJuIFN0cmluZyhzdHIpCiAgICAgIC5yZXBsYWNlKC8mL2csICcmYW1wOycpCiAgICAgIC5yZXBsYWNlKC88L2csICcmbHQ7JykKICAgICAgLnJlcGxhY2UoLz4vZywgJyZndDsnKQogICAgICAucmVwbGFjZSgvIi9nLCAnJnF1b3Q7JykKICAgICAgLnJlcGxhY2UoLycvZywgJyYjMzk7Jyk7CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0KDQldCd0JTQldCgINCY0J3QotCV0KDQpNCV0JnQodCQCiAgLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQogIGZ1bmN0aW9uIGluaXRVSSgpIHsKICAgIHZhciBpZEVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlTeXBoZXJJZCcpOwogICAgaWYgKGlkRWxlbSkgaWRFbGVtLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlJZDsKCiAgICB2YXIgbmFtZUVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlOb2RlRGlzcGxheU5hbWUnKTsKICAgIGlmIChuYW1lRWxlbSkgbmFtZUVsZW0udGV4dENvbnRlbnQgPSBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSB8fCAn0JzQvtC5INGD0LfQtdC7JzsKCiAgICB2YXIgYXZhdGFyRWxlbSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdteU5vZGVBdmF0YXInKTsKICAgIGlmIChhdmF0YXJFbGVtKSB7CiAgICAgIGF2YXRhckVsZW0udGV4dENvbnRlbnQgPSAoc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUgfHwgJ1UnKS5jaGFyQXQoMCkudG9VcHBlckNhc2UoKTsKICAgICAgdmFyIGdyYWQgPSBnZXRBdmF0YXJDb2xvcihzdGF0ZS5pZGVudGl0eS5teUlkKTsKICAgICAgYXZhdGFyRWxlbS5zdHlsZS5iYWNrZ3JvdW5kID0gJ2xpbmVhci1ncmFkaWVudCgxMzVkZWcsICcgKyBncmFkWzBdICsgJywgJyArIGdyYWRbMV0gKyAnKSc7CiAgICB9CgogICAgdmFyIHNldHRpbmdzSWQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnc2V0dGluZ3NOb2RlSWQnKTsKICAgIGlmIChzZXR0aW5nc0lkKSBzZXR0aW5nc0lkLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlJZDsKCiAgICB2YXIgc2V0dGluZ3NOYW1lID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2lucHV0RGlzcGxheU5hbWUnKTsKICAgIGlmIChzZXR0aW5nc05hbWUpIHNldHRpbmdzTmFtZS52YWx1ZSA9IHN0YXRlLmlkZW50aXR5LmRpc3BsYXlOYW1lIHx8ICcnOwoKICAgIHZhciBmcEVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbW9kYWxGaW5nZXJwcmludCcpOwogICAgaWYgKGZwRWxlbSkgZnBFbGVtLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlGaW5nZXJwcmludDsKCiAgICAvLyDQodC40L3RhdGA0L7QvdC40LfQuNGA0YPQtdC8IElEINGBINC90LDRgtC40LLQvdGL0LwgQW5kcm9pZCBCTEUg0YHQtdGA0LLQuNGB0L7QvAogICAgaWYgKHdpbmRvdy5BbmRyb2lkQnJpZGdlICYmIHdpbmRvdy5BbmRyb2lkQnJpZGdlLnVwZGF0ZU5vZGVJZGVudGl0eSkgewogICAgICB0cnkgewogICAgICAgIHdpbmRvdy5BbmRyb2lkQnJpZGdlLnVwZGF0ZU5vZGVJZGVudGl0eShzdGF0ZS5pZGVudGl0eS5teUlkKTsKICAgICAgfSBjYXRjaCAoZSkge30KICAgIH0KCiAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgcmVuZGVyTmVhcmJ5UGVlcnMoKTsKICAgIHNldHVwRXZlbnRMaXN0ZW5lcnMoKTsKICAgIGluaXROZXR3b3JrVHJhbnNwb3J0cygpOwoKICAgIC8vINCX0LDQv9GD0YHQutCw0LXQvCDQv9C10YDQuNC+0LTQuNGH0LXRgdC60LjQuSDQvNCw0Y/QuiDQvtCx0L3QsNGA0YPQttC10L3QuNGPCiAgICBzZXRJbnRlcnZhbChzZW5kRGlzY292ZXJ5QmVhY29uLCA1MDAwKTsKICAgIHNlbmREaXNjb3ZlcnlCZWFjb24oKTsKICB9CgogIGZ1bmN0aW9uIHJlbmRlckNoYXRMaXN0KCkgewogICAgdmFyIGNvbnRhaW5lciA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdjaGF0TGlzdCcpOwogICAgaWYgKCFjb250YWluZXIpIHJldHVybjsKICAgIGNvbnRhaW5lci5pbm5lckhUTUwgPSAnJzsKCiAgICB2YXIgZmlsdGVyZWQgPSBzdGF0ZS5jaGF0cy5maWx0ZXIoZnVuY3Rpb24gKGNoYXQpIHsKICAgICAgaWYgKCFzdGF0ZS5zZWFyY2hRdWVyeSkgcmV0dXJuIHRydWU7CiAgICAgIHZhciBxID0gc3RhdGUuc2VhcmNoUXVlcnkudG9Mb3dlckNhc2UoKTsKICAgICAgcmV0dXJuICgKICAgICAgICBjaGF0Lm5hbWUudG9Mb3dlckNhc2UoKS5pbmRleE9mKHEpICE9PSAtMSB8fAogICAgICAgIChjaGF0Lmxhc3RNZXNzYWdlICYmIGNoYXQubGFzdE1lc3NhZ2UudG9Mb3dlckNhc2UoKS5pbmRleE9mKHEpICE9PSAtMSkKICAgICAgKTsKICAgIH0pOwoKICAgIGZpbHRlcmVkLmZvckVhY2goZnVuY3Rpb24gKGNoYXQpIHsKICAgICAgdmFyIGl0ZW0gPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgaXRlbS5jbGFzc05hbWUgPSAnY2hhdC1pdGVtJyArIChjaGF0LmlkID09PSBzdGF0ZS5hY3RpdmVDaGF0SWQgPyAnIGFjdGl2ZScgOiAnJyk7CgogICAgICBpdGVtLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgc2VsZWN0Q2hhdChjaGF0LmlkKTsKICAgICAgfTsKCiAgICAgIHZhciBncmFkID0gZ2V0QXZhdGFyQ29sb3IoY2hhdC5pZCk7CiAgICAgIHZhciBhdmF0YXJTdHlsZSA9ICdiYWNrZ3JvdW5kOiBsaW5lYXItZ3JhZGllbnQoMTM1ZGVnLCAnICsgZ3JhZFswXSArICcsICcgKyBncmFkWzFdICsgJyknOwoKICAgICAgdmFyIHVucmVhZEh0bWwgPQogICAgICAgIGNoYXQudW5yZWFkID4gMAogICAgICAgICAgPyAnPHNwYW4gY2xhc3M9InVucmVhZC1iYWRnZSI+JyArIGNoYXQudW5yZWFkICsgJzwvc3Bhbj4nCiAgICAgICAgICA6ICcnOwoKICAgICAgaXRlbS5pbm5lckhUTUwgPQogICAgICAgICc8ZGl2IGNsYXNzPSJhdmF0YXIiIHN0eWxlPSInICsKICAgICAgICBhdmF0YXJTdHlsZSArCiAgICAgICAgJyI+JyArCiAgICAgICAgZXNjYXBlSHRtbChjaGF0LmF2YXRhciB8fCAnUCcpICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9ImNoYXQtaXRlbS1tZXRhIj4nICsKICAgICAgICAnPGRpdiBjbGFzcz0iY2hhdC1pdGVtLXRvcCI+JyArCiAgICAgICAgJzxzcGFuIGNsYXNzPSJjaGF0LWl0ZW0tbmFtZSI+JyArCiAgICAgICAgZXNjYXBlSHRtbChjaGF0Lm5hbWUpICsKICAgICAgICAnPC9zcGFuPicgKwogICAgICAgICc8c3BhbiBjbGFzcz0iY2hhdC1pdGVtLXRpbWUiPicgKwogICAgICAgIGVzY2FwZUh0bWwoY2hhdC50aW1lKSArCiAgICAgICAgJzwvc3Bhbj4nICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9ImNoYXQtaXRlbS1wcmV2aWV3Ij4nICsKICAgICAgICAnPHNwYW4gY2xhc3M9InByZXZpZXctdGV4dCI+JyArCiAgICAgICAgZXNjYXBlSHRtbChjaGF0Lmxhc3RNZXNzYWdlIHx8ICcnKSArCiAgICAgICAgJzwvc3Bhbj4nICsKICAgICAgICB1bnJlYWRIdG1sICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzwvZGl2Pic7CgogICAgICBjb250YWluZXIuYXBwZW5kQ2hpbGQoaXRlbSk7CiAgICB9KTsKICB9CgogIGZ1bmN0aW9uIHNlbGVjdENoYXQoY2hhdElkKSB7CiAgICBzdGF0ZS5hY3RpdmVDaGF0SWQgPSBjaGF0SWQ7CiAgICB2YXIgY2hhdCA9IHN0YXRlLmNoYXRzLmZpbmQoZnVuY3Rpb24gKGMpIHsKICAgICAgcmV0dXJuIGMuaWQgPT09IGNoYXRJZDsKICAgIH0pOwogICAgaWYgKGNoYXQpIGNoYXQudW5yZWFkID0gMDsKCiAgICBzYXZlQ2hhdHMoKTsKICAgIHJlbmRlckNoYXRMaXN0KCk7CiAgICByZW5kZXJBY3RpdmVDaGF0KCk7CgogICAgdmFyIGxheW91dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdhcHBMYXlvdXQnKTsKICAgIGlmIChsYXlvdXQpIGxheW91dC5jbGFzc0xpc3QuYWRkKCdtb2JpbGUtY2hhdC1vcGVuJyk7CiAgfQoKICBmdW5jdGlvbiByZW5kZXJBY3RpdmVDaGF0KCkgewogICAgdmFyIGNoYXQgPSBzdGF0ZS5jaGF0cy5maW5kKGZ1bmN0aW9uIChjKSB7CiAgICAgIHJldHVybiBjLmlkID09PSBzdGF0ZS5hY3RpdmVDaGF0SWQ7CiAgICB9KTsKICAgIGlmICghY2hhdCkgcmV0dXJuOwoKICAgIHZhciBuYW1lRWxlbSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdhY3RpdmVDaGF0TmFtZScpOwogICAgdmFyIHN0YXR1c0VsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYWN0aXZlQ2hhdFN0YXR1c1RleHQnKTsKICAgIHZhciBhdmF0YXJFbGVtID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2FjdGl2ZUNoYXRBdmF0YXInKTsKICAgIHZhciBiYW5uZXJUZXh0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2VuY3J5cHRpb25CYW5uZXJUZXh0Jyk7CgogICAgaWYgKG5hbWVFbGVtKSBuYW1lRWxlbS50ZXh0Q29udGVudCA9IGNoYXQubmFtZTsKICAgIGlmIChzdGF0dXNFbGVtKSBzdGF0dXNFbGVtLnRleHRDb250ZW50ID0gY2hhdC5zdGF0dXM7CiAgICBpZiAoYXZhdGFyRWxlbSkgewogICAgICBhdmF0YXJFbGVtLnRleHRDb250ZW50ID0gY2hhdC5hdmF0YXIgfHwgJ1AnOwogICAgICB2YXIgZ3JhZCA9IGdldEF2YXRhckNvbG9yKGNoYXQuaWQpOwogICAgICBhdmF0YXJFbGVtLnN0eWxlLmJhY2tncm91bmQgPSAnbGluZWFyLWdyYWRpZW50KDEzNWRlZywgJyArIGdyYWRbMF0gKyAnLCAnICsgZ3JhZFsxXSArICcpJzsKICAgIH0KCiAgICBpZiAoYmFubmVyVGV4dCkgewogICAgICBpZiAoY2hhdC5pZCA9PT0gJ21lc2gtYnJvYWRjYXN0JykgewogICAgICAgIGJhbm5lclRleHQudGV4dENvbnRlbnQgPQogICAgICAgICAgJ9Ce0LHRidC40Lkg0L7RgtC60YDRi9GC0YvQuSBNZXNoLdC60LDQvdCw0LsuINCS0YHQtSDQvdCw0YXQvtC00Y/RidC40LXRgdGPINGA0Y/QtNC+0Lwg0YPQt9C70Ysg0LLQuNC00Y/RgiDRjdGC0Lgg0YHQvtC+0LHRidC10L3QuNGPLic7CiAgICAgIH0gZWxzZSB7CiAgICAgICAgYmFubmVyVGV4dC50ZXh0Q29udGVudCA9CiAgICAgICAgICAn0J/QtdGA0YHQvtC90LDQu9GM0L3Ri9C5IEUyRUUg0LrQsNC90LDQuy4g0KjQuNGE0YDQvtCy0LDQvdC40LUgWDI1NTE5ICYgQ2hhQ2hhMjAtUG9seTEzMDUg0YEg0YPQt9C70L7QvCAnICsKICAgICAgICAgIGNoYXQuaWQ7CiAgICAgIH0KICAgIH0KCiAgICB2YXIgdmlld3BvcnQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbWVzc2FnZXNWaWV3cG9ydCcpOwogICAgaWYgKCF2aWV3cG9ydCkgcmV0dXJuOwoKICAgIC8vINCh0L7RhdGA0LDQvdGP0LXQvCBkYXRlLWJhZGdlINC4IGVuY3J5cHRpb24tYmFubmVyCiAgICB2YXIgc3RhdGljRWxlbWVudHMgPSBbXTsKICAgIHZhciBkYXRlQmFkZ2UgPSB2aWV3cG9ydC5xdWVyeVNlbGVjdG9yKCcuZGF0ZS1iYWRnZS13cmFwJyk7CiAgICB2YXIgZW5jQmFubmVyID0gdmlld3BvcnQucXVlcnlTZWxlY3RvcignLmVuY3J5cHRpb24tYmFubmVyJyk7CiAgICBpZiAoZGF0ZUJhZGdlKSBzdGF0aWNFbGVtZW50cy5wdXNoKGRhdGVCYWRnZSk7CiAgICBpZiAoZW5jQmFubmVyKSBzdGF0aWNFbGVtZW50cy5wdXNoKGVuY0Jhbm5lcik7CgogICAgdmlld3BvcnQuaW5uZXJIVE1MID0gJyc7CiAgICBzdGF0aWNFbGVtZW50cy5mb3JFYWNoKGZ1bmN0aW9uIChlbCkgewogICAgICB2aWV3cG9ydC5hcHBlbmRDaGlsZChlbCk7CiAgICB9KTsKCiAgICBjaGF0Lm1lc3NhZ2VzLmZvckVhY2goZnVuY3Rpb24gKG1zZykgewogICAgICB2YXIgcm93ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgIHJvdy5jbGFzc05hbWUgPSAnbWVzc2FnZS1yb3cgJyArIChtc2cuaXNPdXRnb2luZyA/ICdvdXRnb2luZycgOiAnaW5jb21pbmcnKTsKCiAgICAgIHZhciBidWJibGUgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgYnViYmxlLmNsYXNzTmFtZSA9ICdidWJibGUnOwoKICAgICAgaWYgKCFtc2cuaXNPdXRnb2luZyAmJiBjaGF0LmlkID09PSAnbWVzaC1icm9hZGNhc3QnICYmIG1zZy5hdXRob3JOYW1lKSB7CiAgICAgICAgdmFyIGF1dGhvciA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ2RpdicpOwogICAgICAgIGF1dGhvci5jbGFzc05hbWUgPSAnYnViYmxlLWF1dGhvcic7CiAgICAgICAgYXV0aG9yLnRleHRDb250ZW50ID0gbXNnLmF1dGhvck5hbWU7CiAgICAgICAgYnViYmxlLmFwcGVuZENoaWxkKGF1dGhvcik7CiAgICAgIH0KCiAgICAgIC8vINCi0LXQutGB0YLQvtCy0L7QtSDRgdC+0L7QsdGJ0LXQvdC40LUKICAgICAgaWYgKG1zZy50eXBlID09PSAndGV4dCcpIHsKICAgICAgICB2YXIgdGV4dEVsID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgICAgdGV4dEVsLmNsYXNzTmFtZSA9ICdidWJibGUtdGV4dCc7CiAgICAgICAgdGV4dEVsLnRleHRDb250ZW50ID0gbXNnLnRleHQ7CiAgICAgICAgYnViYmxlLmFwcGVuZENoaWxkKHRleHRFbCk7CiAgICAgIH0KICAgICAgLy8g0KTQsNC50Lsg0LjQu9C4INGE0L7RgtC+CiAgICAgIGVsc2UgaWYgKG1zZy50eXBlID09PSAnZmlsZScpIHsKICAgICAgICBpZiAobXNnLmZpbGVUeXBlICYmIG1zZy5maWxlVHlwZS5pbmRleE9mKCdpbWFnZS8nKSA9PT0gMCAmJiBtc2cuZmlsZURhdGEpIHsKICAgICAgICAgIHZhciBpbWcgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdpbWcnKTsKICAgICAgICAgIGltZy5jbGFzc05hbWUgPSAnYnViYmxlLWltYWdlLXByZXZpZXcnOwogICAgICAgICAgaW1nLnNyYyA9IG1zZy5maWxlRGF0YTsKICAgICAgICAgIGltZy5hbHQgPSBtc2cuZmlsZU5hbWUgfHwgJ9CY0LfQvtCx0YDQsNC20LXQvdC40LUnOwogICAgICAgICAgYnViYmxlLmFwcGVuZENoaWxkKGltZyk7CiAgICAgICAgfQoKICAgICAgICB2YXIgZmlsZUNhcmQgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgICBmaWxlQ2FyZC5jbGFzc05hbWUgPSAnYnViYmxlLWZpbGUtY2FyZCc7CiAgICAgICAgZmlsZUNhcmQuaW5uZXJIVE1MID0KICAgICAgICAgICc8ZGl2IGNsYXNzPSJmaWxlLWljb24tYm94Ij4nICsKICAgICAgICAgIGRvY0ljb25TdmcgKwogICAgICAgICAgJzwvZGl2PicgKwogICAgICAgICAgJzxkaXYgY2xhc3M9ImZpbGUtY2FyZC1kZXRhaWxzIj4nICsKICAgICAgICAgICc8ZGl2IGNsYXNzPSJmaWxlLWNhcmQtbmFtZSI+JyArCiAgICAgICAgICBlc2NhcGVIdG1sKG1zZy5maWxlTmFtZSB8fCAn0KTQsNC50LsnKSArCiAgICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgICAnPGRpdiBjbGFzcz0iZmlsZS1jYXJkLXNpemUiPicgKwogICAgICAgICAgZm9ybWF0RmlsZVNpemUobXNnLmZpbGVTaXplKSArCiAgICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgICAnPGEgY2xhc3M9ImZpbGUtZG93bmxvYWQtbGluayIgdGl0bGU9ItCh0LrQsNGH0LDRgtGMIj4nICsKICAgICAgICAgIGRvd25sb2FkSWNvblN2ZyArCiAgICAgICAgICAnPC9hPic7CgogICAgICAgIHZhciBkbExpbmsgPSBmaWxlQ2FyZC5xdWVyeVNlbGVjdG9yKCcuZmlsZS1kb3dubG9hZC1saW5rJyk7CiAgICAgICAgaWYgKGRsTGluayAmJiBtc2cuZmlsZURhdGEpIHsKICAgICAgICAgIGRsTGluay5ocmVmID0gbXNnLmZpbGVEYXRhOwogICAgICAgICAgZGxMaW5rLmRvd25sb2FkID0gbXNnLmZpbGVOYW1lIHx8ICdzeXBoZXJfZmlsZSc7CiAgICAgICAgfQogICAgICAgIGJ1YmJsZS5hcHBlbmRDaGlsZChmaWxlQ2FyZCk7CiAgICAgIH0KCiAgICAgIC8vINCk0YPRgtC10YAg0LHQsNCx0LHQu9CwOiDQstGA0LXQvNGPINC4INGB0YLQsNGC0YPRgSDQtNC+0YHRgtCw0LLQutC4CiAgICAgIHZhciBmb290ZXIgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgZm9vdGVyLmNsYXNzTmFtZSA9ICdidWJibGUtZm9vdGVyJzsKCiAgICAgIHZhciB0aW1lU3BhbiA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ3NwYW4nKTsKICAgICAgdGltZVNwYW4uY2xhc3NOYW1lID0gJ2J1YmJsZS10aW1lJzsKICAgICAgdGltZVNwYW4udGV4dENvbnRlbnQgPSBtc2cudGltZTsKICAgICAgZm9vdGVyLmFwcGVuZENoaWxkKHRpbWVTcGFuKTsKCiAgICAgIGlmIChtc2cuaXNPdXRnb2luZykgewogICAgICAgIHZhciB0aWNrV3JhcCA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ3NwYW4nKTsKICAgICAgICB0aWNrV3JhcC5pbm5lckhUTUwgPSBtc2cuZGVsaXZlcmVkID8gZG91YmxlQ2hlY2tTdmcgOiBzaW5nbGVDaGVja1N2ZzsKICAgICAgICBmb290ZXIuYXBwZW5kQ2hpbGQodGlja1dyYXApOwogICAgICB9CgogICAgICBidWJibGUuYXBwZW5kQ2hpbGQoZm9vdGVyKTsKICAgICAgcm93LmFwcGVuZENoaWxkKGJ1YmJsZSk7CiAgICAgIHZpZXdwb3J0LmFwcGVuZENoaWxkKHJvdyk7CiAgICB9KTsKCiAgICB2aWV3cG9ydC5zY3JvbGxUb3AgPSB2aWV3cG9ydC5zY3JvbGxIZWlnaHQ7CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0KDQkNCU0JDQoCDQo9Ch0KLQoNCe0JnQodCi0JIg0J/QntCR0JvQmNCX0J7QodCi0JggKEJMRSDQn9Cg0J7QpdCe0JbQmNCVINCYIFdJLUZJKQogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiByZW5kZXJOZWFyYnlQZWVycygpIHsKICAgIHZhciBsaXN0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ25lYXJieVBlZXJMaXN0Jyk7CiAgICB2YXIgYmFkZ2UgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgncGVlckNvdW50ZXJCYWRnZScpOwogICAgdmFyIG1vZGFsTGlzdCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdkaXNjb3ZlcmVkUGVlcnNNb2RhbExpc3QnKTsKICAgIGlmICghbGlzdCkgcmV0dXJuOwoKICAgIHZhciBwZWVycyA9IE9iamVjdC52YWx1ZXMoc3RhdGUuZGlzY292ZXJlZFBlZXJzKTsKICAgIHZhciBub3cgPSBEYXRlLm5vdygpOwogICAgLy8g0J7RgdGC0LDQstC70Y/QtdC8INGD0LfQu9GLLCDQstC40LTQuNC80YvQtSDQv9C+0YHQu9C10LTQvdC40LUgMiDQvNC40L3Rg9GC0YsKICAgIHBlZXJzID0gcGVlcnMuZmlsdGVyKGZ1bmN0aW9uIChwKSB7CiAgICAgIHJldHVybiBub3cgLSBwLmxhc3RTZWVuIDwgMTIwMDAwOwogICAgfSk7CgogICAgaWYgKGJhZGdlKSBiYWRnZS50ZXh0Q29udGVudCA9IFN0cmluZyhwZWVycy5sZW5ndGgpOwoKICAgIHZhciBkZXNjID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ21lc2hTdGF0dXNEZXNjJyk7CiAgICBpZiAoZGVzYykgewogICAgICBkZXNjLnRleHRDb250ZW50ID0KICAgICAgICBwZWVycy5sZW5ndGggPiAwCiAgICAgICAgICA/IHBlZXJzLmxlbmd0aCArICcg0YPQt9C10Lso0L7Qsikg0LIg0YDQsNC00LjRg9GB0LUg0YDQsNC00LjQvtC60LDQvdCw0LvQsCcKICAgICAgICAgIDogJ9Ch0LrQsNC90LjRgNC+0LLQsNC90LjQtSBCTEUg0LggVURQLi4uJzsKICAgIH0KCiAgICBpZiAocGVlcnMubGVuZ3RoID09PSAwKSB7CiAgICAgIGxpc3QuaW5uZXJIVE1MID0KICAgICAgICAnPGRpdiBjbGFzcz0iZW1wdHktbmVhcmJ5LXBsYWNlaG9sZGVyIj48c3Bhbj7Qn9C+0LjRgdC6INGD0LfQu9C+0LIg0L/QviBCTEUg0Lgg0LvQvtC60LDQu9GM0L3QvtC5INGB0LXRgtC4Li4uPC9zcGFuPjwvZGl2Pic7CiAgICAgIGlmIChtb2RhbExpc3QpIHsKICAgICAgICBtb2RhbExpc3QuaW5uZXJIVE1MID0KICAgICAgICAgICc8c3BhbiBzdHlsZT0iZm9udC1zaXplOjEycHg7IGNvbG9yOnZhcigtLXRleHQtbXV0ZWQpIj7QndC10YIg0LDQutGC0LjQstC90YvRhSDRg9C30LvQvtCyINC/0L7QsdC70LjQt9C+0YHRgtC4PC9zcGFuPic7CiAgICAgIH0KICAgICAgcmV0dXJuOwogICAgfQoKICAgIGxpc3QuaW5uZXJIVE1MID0gJyc7CiAgICBpZiAobW9kYWxMaXN0KSBtb2RhbExpc3QuaW5uZXJIVE1MID0gJyc7CgogICAgcGVlcnMuZm9yRWFjaChmdW5jdGlvbiAocGVlcikgewogICAgICB2YXIgcm93ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgIHJvdy5jbGFzc05hbWUgPSAnbmVhcmJ5LXBlZXItcm93JzsKCiAgICAgIHZhciBncmFkID0gZ2V0QXZhdGFyQ29sb3IocGVlci5pZCk7CiAgICAgIHZhciBhdmF0YXJTdHlsZSA9ICdiYWNrZ3JvdW5kOiBsaW5lYXItZ3JhZGllbnQoMTM1ZGVnLCAnICsgZ3JhZFswXSArICcsICcgKyBncmFkWzFdICsgJyknOwoKICAgICAgdmFyIG1ldGFUZXh0ID0gJyc7CiAgICAgIGlmIChwZWVyLnRyYW5zcG9ydCA9PT0gJ0JMRScpIHsKICAgICAgICBtZXRhVGV4dCA9ICdCTEUgTWVzaCB+JyArIChwZWVyLmRpc3RhbmNlIHx8ICcxLjUnKSArICfQvCAoJyArIChwZWVyLnJzc2kgfHwgJy02MCcpICsgJyBkQm0pJzsKICAgICAgfSBlbHNlIHsKICAgICAgICBtZXRhVGV4dCA9ICdXaS1GaSBMQU46ICcgKyAocGVlci5pcCB8fCAn0LvQvtC60LDQu9GM0L3QvicpOwogICAgICB9CgogICAgICByb3cuaW5uZXJIVE1MID0KICAgICAgICAnPGRpdiBjbGFzcz0icGVlci1yb3ctbGVmdCI+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9Im5lYXJieS1hdmF0YXIiIHN0eWxlPSInICsKICAgICAgICBhdmF0YXJTdHlsZSArCiAgICAgICAgJyI+JyArCiAgICAgICAgZXNjYXBlSHRtbCgocGVlci5uYW1lIHx8ICdQJykuY2hhckF0KDApLnRvVXBwZXJDYXNlKCkpICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9Im5lYXJieS1pbmZvIj4nICsKICAgICAgICAnPHNwYW4gY2xhc3M9Im5lYXJieS1pZCI+JyArCiAgICAgICAgZXNjYXBlSHRtbChwZWVyLmlkKSArCiAgICAgICAgJzwvc3Bhbj4nICsKICAgICAgICAnPHNwYW4gY2xhc3M9Im5lYXJieS1tZXRhIj4nICsKICAgICAgICBlc2NhcGVIdG1sKG1ldGFUZXh0KSArCiAgICAgICAgJzwvc3Bhbj4nICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzwvZGl2PicgKwogICAgICAgICc8YnV0dG9uIGNsYXNzPSJwZWVyLWFjdGlvbi1idG4iPtCd0LDQv9C40YHQsNGC0Yw8L2J1dHRvbj4nOwoKICAgICAgdmFyIHdyaXRlQnRuID0gcm93LnF1ZXJ5U2VsZWN0b3IoJy5wZWVyLWFjdGlvbi1idG4nKTsKICAgICAgaWYgKHdyaXRlQnRuKSB7CiAgICAgICAgd3JpdGVCdG4ub25jbGljayA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgICBlLnN0b3BQcm9wYWdhdGlvbigpOwogICAgICAgICAgc3RhcnREaXJlY3RDaGF0V2l0aFBlZXIocGVlci5pZCwgcGVlci5uYW1lKTsKICAgICAgICB9OwogICAgICB9CgogICAgICByb3cub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBzdGFydERpcmVjdENoYXRXaXRoUGVlcihwZWVyLmlkLCBwZWVyLm5hbWUpOwogICAgICB9OwoKICAgICAgbGlzdC5hcHBlbmRDaGlsZChyb3cpOwoKICAgICAgLy8g0JIg0LzQvtC00LDQu9GM0L3QvtC1INC+0LrQvdC+INCx0YvRgdGC0YDQvtCz0L4g0LLRi9Cx0L7RgNCwCiAgICAgIGlmIChtb2RhbExpc3QpIHsKICAgICAgICB2YXIgcXVpY2tJdGVtID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgICAgcXVpY2tJdGVtLmNsYXNzTmFtZSA9ICduZWFyYnktcGVlci1yb3cnOwogICAgICAgIHF1aWNrSXRlbS5zdHlsZS5jdXJzb3IgPSAncG9pbnRlcic7CiAgICAgICAgcXVpY2tJdGVtLmlubmVySFRNTCA9CiAgICAgICAgICAnPHNwYW4gc3R5bGU9ImZvbnQtZmFtaWx5OnZhcigtLWZvbnQtbW9ubyk7IGZvbnQtc2l6ZToxMnB4OyBjb2xvcjp2YXIoLS1hY2NlbnQtY3lhbikiPicgKwogICAgICAgICAgZXNjYXBlSHRtbChwZWVyLmlkKSArCiAgICAgICAgICAnPC9zcGFuPjxzcGFuIHN0eWxlPSJmb250LXNpemU6MTFweDsgY29sb3I6dmFyKC0tdGV4dC1tdXRlZCkiPicgKwogICAgICAgICAgZXNjYXBlSHRtbChtZXRhVGV4dCkgKwogICAgICAgICAgJzwvc3Bhbj4nOwogICAgICAgIHF1aWNrSXRlbS5vbmNsaWNrID0gZnVuY3Rpb24gKCkgewogICAgICAgICAgdmFyIGlucHV0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ25ld0NoYXRQZWVySWRJbnB1dCcpOwogICAgICAgICAgaWYgKGlucHV0KSBpbnB1dC52YWx1ZSA9IHBlZXIuaWQ7CiAgICAgICAgfTsKICAgICAgICBtb2RhbExpc3QuYXBwZW5kQ2hpbGQocXVpY2tJdGVtKTsKICAgICAgfQogICAgfSk7CiAgfQoKICBmdW5jdGlvbiBzdGFydERpcmVjdENoYXRXaXRoUGVlcihwZWVySWQsIHBlZXJOYW1lKSB7CiAgICBpZiAocGVlcklkID09PSBzdGF0ZS5pZGVudGl0eS5teUlkKSByZXR1cm47CgogICAgdmFyIGV4aXN0aW5nID0gc3RhdGUuY2hhdHMuZmluZChmdW5jdGlvbiAoYykgewogICAgICByZXR1cm4gYy5pZCA9PT0gcGVlcklkOwogICAgfSk7CgogICAgaWYgKCFleGlzdGluZykgewogICAgICB2YXIgbmV3Q2hhdCA9IHsKICAgICAgICBpZDogcGVlcklkLAogICAgICAgIG5hbWU6IHBlZXJOYW1lIHx8IHBlZXJJZCwKICAgICAgICBhdmF0YXI6IChwZWVyTmFtZSB8fCBwZWVySWQpLnJlcGxhY2UoJ3N5cC0nLCAnJykuY2hhckF0KDApLnRvVXBwZXJDYXNlKCksCiAgICAgICAgc3RhdHVzOiAn0J/RgNGP0LzQvtC5INC30LDRiNC40YTRgNC+0LLQsNC90L3Ri9C5INC60LDQvdCw0LsnLAogICAgICAgIGxhc3RNZXNzYWdlOiAn0JTQuNCw0LvQvtCzINC+0YLQutGA0YvRgicsCiAgICAgICAgdGltZTogZm9ybWF0VGltZShuZXcgRGF0ZSgpKSwKICAgICAgICB1bnJlYWQ6IDAsCiAgICAgICAgbWVzc2FnZXM6IFtdLAogICAgICB9OwogICAgICBzdGF0ZS5jaGF0cy5wdXNoKG5ld0NoYXQpOwogICAgICBzYXZlQ2hhdHMoKTsKICAgIH0KCiAgICBzZWxlY3RDaGF0KHBlZXJJZCk7CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0KHQldCi0JXQktCe0Jkg0KLQoNCQ0J3QodCf0J7QoNCiINCYINCf0JXQoNCV0JTQkNCn0JAg0J/QkNCa0JXQotCe0JIKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgZnVuY3Rpb24gaW5pdE5ldHdvcmtUcmFuc3BvcnRzKCkgewogICAgLy8gMS4g0J7Qv9GA0LXQtNC10LvQtdC90LjQtSDQu9C+0LrQsNC70YzQvdC+0LPQviBJUCDRgSDQtNC10YHQutGC0L7QvyDRgdC10YDQstC10YDQsAogICAgaWYgKHdpbmRvdy5mZXRjaCkgewogICAgICBmZXRjaCgnL2FwaS9tZXNoL2lwJykKICAgICAgICAudGhlbihmdW5jdGlvbiAocmVzKSB7CiAgICAgICAgICByZXR1cm4gcmVzLmpzb24oKTsKICAgICAgICB9KQogICAgICAgIC50aGVuKGZ1bmN0aW9uIChkYXRhKSB7CiAgICAgICAgICBpZiAoZGF0YSAmJiBkYXRhLmlwKSB7CiAgICAgICAgICAgIHN0YXRlLmxvY2FsSXAgPSBkYXRhLmlwOwogICAgICAgICAgICB1cGRhdGVMb2NhbElwVUkoZGF0YS5pcCk7CiAgICAgICAgICB9CiAgICAgICAgfSkKICAgICAgICAuY2F0Y2goZnVuY3Rpb24gKCkge30pOwogICAgfQoKICAgIC8vIDIuIFNlcnZlci1TZW50IEV2ZW50cyAoU1NFKSDQtNC70Y8g0LTQtdGB0LrRgtC+0L/QsCAoTGludXgsIG1hY09TLCBXaW5kb3dzKQogICAgaWYgKHdpbmRvdy5FdmVudFNvdXJjZSkgewogICAgICB0cnkgewogICAgICAgIHZhciBzc2UgPSBuZXcgRXZlbnRTb3VyY2UoJy9hcGkvbWVzaC9ldmVudHMnKTsKICAgICAgICBzc2Uub25tZXNzYWdlID0gZnVuY3Rpb24gKGUpIHsKICAgICAgICAgIGlmIChlLmRhdGEgJiYgd2luZG93Lm9uTWVzaFBhY2tldFJlY2VpdmVkKSB7CiAgICAgICAgICAgIHdpbmRvdy5vbk1lc2hQYWNrZXRSZWNlaXZlZChlLmRhdGEpOwogICAgICAgICAgfQogICAgICAgIH07CiAgICAgIH0gY2F0Y2ggKGVycikge30KICAgIH0KICB9CgogIGZ1bmN0aW9uIHVwZGF0ZUxvY2FsSXBVSShpcCkgewogICAgdmFyIGxibCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdzZXR0aW5nc0xvY2FsSXAnKTsKICAgIGlmIChsYmwpIGxibC50ZXh0Q29udGVudCA9IGlwICsgJzo3Nzg4JzsKICB9CgogIHdpbmRvdy5vbkxvY2FsSXBEZXRlY3RlZCA9IGZ1bmN0aW9uIChpcCkgewogICAgaWYgKGlwKSB7CiAgICAgIHN0YXRlLmxvY2FsSXAgPSBpcDsKICAgICAgdXBkYXRlTG9jYWxJcFVJKGlwKTsKICAgIH0KICB9OwoKICAvLyDQotC+0YfQutCwINCy0YXQvtC00LAg0LTQu9GPINC+0LHQvdCw0YDRg9C20LXQvdC40Y8g0L/RgNC+0YXQvtC20LjRhSDQv9C+IEJMRSDQuNC3INC90LDRgtC40LLQvdC+0LPQviDRgdC10YDQstC40YHQsAogIHdpbmRvdy5vbkJsZVBlZXJEaXNjb3ZlcmVkID0gZnVuY3Rpb24gKHBlZXJJZCwgcnNzaSwgZGlzdGFuY2UsIG1hYykgewogICAgaWYgKCFwZWVySWQgfHwgcGVlcklkID09PSBzdGF0ZS5pZGVudGl0eS5teUlkKSByZXR1cm47CiAgICBzdGF0ZS5kaXNjb3ZlcmVkUGVlcnNbcGVlcklkXSA9IHsKICAgICAgaWQ6IHBlZXJJZCwKICAgICAgbmFtZTogcGVlcklkLAogICAgICB0cmFuc3BvcnQ6ICdCTEUnLAogICAgICByc3NpOiByc3NpLAogICAgICBkaXN0YW5jZTogZGlzdGFuY2UsCiAgICAgIG1hYzogbWFjLAogICAgICBsYXN0U2VlbjogRGF0ZS5ub3coKSwKICAgIH07CiAgICByZW5kZXJOZWFyYnlQZWVycygpOwogIH07CgogIC8vINCi0L7Rh9C60LAg0LLRhdC+0LTQsCDQstGF0L7QtNGP0YnQuNGFINC/0LDQutC10YLQvtCyIChVRFAsIEhUVFAsIEJMRSBHQVRUKQogIHdpbmRvdy5vbk1lc2hQYWNrZXRSZWNlaXZlZCA9IGZ1bmN0aW9uIChwYWNrZXRPckpzb24pIHsKICAgIHZhciBwYWNrZXQgPSBwYWNrZXRPckpzb247CiAgICBpZiAodHlwZW9mIHBhY2tldE9ySnNvbiA9PT0gJ3N0cmluZycpIHsKICAgICAgdHJ5IHsKICAgICAgICBwYWNrZXQgPSBKU09OLnBhcnNlKHBhY2tldE9ySnNvbik7CiAgICAgIH0gY2F0Y2ggKGUpIHsKICAgICAgICByZXR1cm47CiAgICAgIH0KICAgIH0KCiAgICBpZiAocGFja2V0ICYmIHBhY2tldC50eXBlID09PSAnQkxFX1BFRVJfRElTQ09WRVJFRCcpIHsKICAgICAgd2luZG93Lm9uQmxlUGVlckRpc2NvdmVyZWQocGFja2V0LnBlZXJJZCwgcGFja2V0LnJzc2ksIHBhY2tldC5kaXN0YW5jZSwgcGFja2V0Lm1hYyk7CiAgICAgIHJldHVybjsKICAgIH0KCiAgICBoYW5kbGVJbmNvbWluZ1BhY2tldChwYWNrZXQpOwogIH07CgogIGZ1bmN0aW9uIHNlbmREaXNjb3ZlcnlCZWFjb24oKSB7CiAgICB2YXIgYmVhY29uID0gewogICAgICB0eXBlOiAnU1lQSF9CRUFDT04nLAogICAgICBpZDogJ2Jjbl8nICsgRGF0ZS5ub3coKSwKICAgICAgZnJvbTogc3RhdGUuaWRlbnRpdHkubXlJZCwKICAgICAgZnJvbU5hbWU6IHN0YXRlLmlkZW50aXR5LmRpc3BsYXlOYW1lLAogICAgICBzZW5kZXJJcDogc3RhdGUubG9jYWxJcCwKICAgICAgdGltZXN0YW1wOiBEYXRlLm5vdygpLAogICAgfTsKICAgIGRpc3BhdGNoUGFja2V0VG9OZXR3b3JrKGJlYWNvbik7CiAgfQoKICBmdW5jdGlvbiBkaXNwYXRjaFBhY2tldFRvTmV0d29yayhwYWNrZXQpIHsKICAgIHBhY2tldC5zZW5kZXJJcCA9IHN0YXRlLmxvY2FsSXA7CiAgICB2YXIgcmF3ID0gSlNPTi5zdHJpbmdpZnkocGFja2V0KTsKCiAgICAvLyAxLiDQndCw0YLQuNCy0L3Ri9C5IEFuZHJvaWQg0LzQvtGB0YIKICAgIGlmICh3aW5kb3cuQW5kcm9pZEJyaWRnZSAmJiB3aW5kb3cuQW5kcm9pZEJyaWRnZS5zZW5kTWVzaFBhY2tldCkgewogICAgICB0cnkgewogICAgICAgIHdpbmRvdy5BbmRyb2lkQnJpZGdlLnNlbmRNZXNoUGFja2V0KHJhdyk7CiAgICAgIH0gY2F0Y2ggKGUpIHt9CiAgICB9CgogICAgLy8gMi4g0JTQtdGB0LrRgtC+0L/QvdGL0Lkg0LvQvtC60LDQu9GM0L3Ri9C5INGB0LXRgNCy0LXRgAogICAgaWYgKHdpbmRvdy5mZXRjaCkgewogICAgICBmZXRjaCgnL2FwaS9tZXNoL3NlbmQnLCB7CiAgICAgICAgbWV0aG9kOiAnUE9TVCcsCiAgICAgICAgaGVhZGVyczogeyAnQ29udGVudC1UeXBlJzogJ2FwcGxpY2F0aW9uL2pzb24nIH0sCiAgICAgICAgYm9keTogcmF3LAogICAgICB9KS5jYXRjaChmdW5jdGlvbiAoKSB7fSk7CiAgICB9CgogICAgLy8gMy4g0J/RgNGP0LzQvtC5IFRDUCBIVFRQIEZhbGxiYWNrINC90LAg0LLRgdC1INC40LfQstC10YHRgtC90YvQtSBJUCAo0L7QsdGF0L7QtCDQvtCz0YDQsNC90LjRh9C10L3QuNC5INGA0L7Rg9GC0LXRgNC+0LIpCiAgICBzdGF0ZS5rbm93blBlZXJJcHMuZm9yRWFjaChmdW5jdGlvbiAocGVlcklwKSB7CiAgICAgIGlmIChwZWVySXAgJiYgcGVlcklwICE9PSAnMTI3LjAuMC4xJyAmJiBwZWVySXAgIT09IHN0YXRlLmxvY2FsSXAgJiYgd2luZG93LmZldGNoKSB7CiAgICAgICAgLy8g0J7RgtC/0YDQsNCy0LrQsCDQvdCwINC/0L7RgNGCIDc3ODkgKEFuZHJvaWQpINC4IDc3ODggKERlc2t0b3ApCiAgICAgICAgWzc3ODksIDc3ODhdLmZvckVhY2goZnVuY3Rpb24gKHB0KSB7CiAgICAgICAgICBmZXRjaCgnaHR0cDovLycgKyBwZWVySXAgKyAnOicgKyBwdCArICcvYXBpL21lc2gvc2VuZCcsIHsKICAgICAgICAgICAgbWV0aG9kOiAnUE9TVCcsCiAgICAgICAgICAgIGhlYWRlcnM6IHsgJ0NvbnRlbnQtVHlwZSc6ICdhcHBsaWNhdGlvbi9qc29uJyB9LAogICAgICAgICAgICBib2R5OiByYXcsCiAgICAgICAgICAgIG1vZGU6ICduby1jb3JzJywKICAgICAgICAgIH0pLmNhdGNoKGZ1bmN0aW9uICgpIHt9KTsKICAgICAgICB9KTsKICAgICAgfQogICAgfSk7CiAgfQoKICBmdW5jdGlvbiBoYW5kbGVJbmNvbWluZ1BhY2tldChwYWNrZXQpIHsKICAgIGlmICghcGFja2V0IHx8ICFwYWNrZXQuZnJvbSkgcmV0dXJuOwogICAgaWYgKHBhY2tldC5mcm9tID09PSBzdGF0ZS5pZGVudGl0eS5teUlkKSByZXR1cm47IC8vINCY0LPQvdC+0YDQuNGA0YPQtdC8INGN0YXQviDRgdCy0L7QuNGFINC20LUg0L/QsNC60LXRgtC+0LIKCiAgICBpZiAocGFja2V0LnNlbmRlcklwICYmIHBhY2tldC5zZW5kZXJJcCAhPT0gJzEyNy4wLjAuMScpIHsKICAgICAgc3RhdGUua25vd25QZWVySXBzLmFkZChwYWNrZXQuc2VuZGVySXApOwogICAgfQoKICAgIC8vINCg0LXQs9C40YHRgtGA0LjRgNGD0LXQvCDQvtCx0L3QsNGA0YPQttC10L3QvdGL0Lkg0YPQt9C10Lsg0LIg0YDQsNC00LDRgNC1CiAgICBzdGF0ZS5kaXNjb3ZlcmVkUGVlcnNbcGFja2V0LmZyb21dID0gewogICAgICBpZDogcGFja2V0LmZyb20sCiAgICAgIG5hbWU6IHBhY2tldC5mcm9tTmFtZSB8fCBwYWNrZXQuZnJvbSwKICAgICAgdHJhbnNwb3J0OiBwYWNrZXQuc2VuZGVySXAgPyAnV2ktRmknIDogJ0JMRScsCiAgICAgIGlwOiBwYWNrZXQuc2VuZGVySXAgfHwgJycsCiAgICAgIGxhc3RTZWVuOiBEYXRlLm5vdygpLAogICAgfTsKICAgIHJlbmRlck5lYXJieVBlZXJzKCk7CgogICAgLy8gMS4g0J7QsdGA0LDQsdC+0YLQutCwINC/0L7QtNGC0LLQtdGA0LbQtNC10L3QuNGPINC00L7RgdGC0LDQstC60LggKEFDSykKICAgIGlmIChwYWNrZXQudHlwZSA9PT0gJ1NZUEhfQUNLJyAmJiBwYWNrZXQudGFyZ2V0TXNnSWQpIHsKICAgICAgc3RhdGUuY2hhdHMuZm9yRWFjaChmdW5jdGlvbiAoY2hhdCkgewogICAgICAgIGNoYXQubWVzc2FnZXMuZm9yRWFjaChmdW5jdGlvbiAobXNnKSB7CiAgICAgICAgICBpZiAobXNnLmlkID09PSBwYWNrZXQudGFyZ2V0TXNnSWQgJiYgbXNnLmlzT3V0Z29pbmcpIHsKICAgICAgICAgICAgbXNnLmRlbGl2ZXJlZCA9IHRydWU7CiAgICAgICAgICB9CiAgICAgICAgfSk7CiAgICAgIH0pOwogICAgICBzYXZlQ2hhdHMoKTsKICAgICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgICByZXR1cm47CiAgICB9CgogICAgdmFyIGlzRm9yVXMgPSBwYWNrZXQudG8gPT09IHN0YXRlLmlkZW50aXR5Lm15SWQ7CiAgICB2YXIgaXNCcm9hZGNhc3QgPSBwYWNrZXQudG8gPT09ICdicm9hZGNhc3QnIHx8ICFwYWNrZXQudG87CgogICAgaWYgKCFpc0ZvclVzICYmICFpc0Jyb2FkY2FzdCkgcmV0dXJuOyAvLyDQn9Cw0LrQtdGCINC00LvRjyDQtNGA0YPQs9C+0LPQviDRg9C30LvQsAoKICAgIC8vIDIuINCe0LHRgNCw0LHQvtGC0LrQsCDRgtC10LrRgdGC0L7QstC+0LPQviDRgdC+0L7QsdGJ0LXQvdC40Y8g0LjQu9C4INGE0LDQudC70LAKICAgIGlmIChwYWNrZXQudHlwZSA9PT0gJ1NZUEhfTVNHJyB8fCBwYWNrZXQudHlwZSA9PT0gJ1NZUEhfRklMRScpIHsKICAgICAgdmFyIHRhcmdldENoYXRJZCA9IGlzQnJvYWRjYXN0ID8gJ21lc2gtYnJvYWRjYXN0JyA6IHBhY2tldC5mcm9tOwogICAgICB2YXIgY2hhdCA9IHN0YXRlLmNoYXRzLmZpbmQoZnVuY3Rpb24gKGMpIHsKICAgICAgICByZXR1cm4gYy5pZCA9PT0gdGFyZ2V0Q2hhdElkOwogICAgICB9KTsKCiAgICAgIGlmICghY2hhdCkgewogICAgICAgIGNoYXQgPSB7CiAgICAgICAgICBpZDogcGFja2V0LmZyb20sCiAgICAgICAgICBuYW1lOiBwYWNrZXQuZnJvbU5hbWUgfHwgcGFja2V0LmZyb20sCiAgICAgICAgICBhdmF0YXI6IChwYWNrZXQuZnJvbU5hbWUgfHwgcGFja2V0LmZyb20pLnJlcGxhY2UoJ3N5cC0nLCAnJykuY2hhckF0KDApLnRvVXBwZXJDYXNlKCksCiAgICAgICAgICBzdGF0dXM6ICfQn9GA0Y/QvNC+0Lkg0LfQsNGI0LjRhNGA0L7QstCw0L3QvdGL0Lkg0LrQsNC90LDQuycsCiAgICAgICAgICBsYXN0TWVzc2FnZTogcGFja2V0LnR5cGUgPT09ICdTWVBIX0ZJTEUnID8gJ9CS0LvQvtC20LXQvdC40LU6ICcgKyBwYWNrZXQuZmlsZU5hbWUgOiBwYWNrZXQudGV4dCwKICAgICAgICAgIHRpbWU6IGZvcm1hdFRpbWUobmV3IERhdGUoKSksCiAgICAgICAgICB1bnJlYWQ6IDAsCiAgICAgICAgICBtZXNzYWdlczogW10sCiAgICAgICAgfTsKICAgICAgICBzdGF0ZS5jaGF0cy5wdXNoKGNoYXQpOwogICAgICB9CgogICAgICB2YXIgbXNnVGltZSA9IHBhY2tldC50aW1lc3RhbXAgPyBmb3JtYXRUaW1lKG5ldyBEYXRlKHBhY2tldC50aW1lc3RhbXApKSA6IGZvcm1hdFRpbWUobmV3IERhdGUoKSk7CgogICAgICB2YXIgaW5jb21pbmdNc2cgPSB7CiAgICAgICAgaWQ6IHBhY2tldC5pZCB8fCAncGt0XycgKyBEYXRlLm5vdygpLAogICAgICAgIHNlbmRlcjogcGFja2V0LmZyb20sCiAgICAgICAgYXV0aG9yTmFtZTogcGFja2V0LmZyb21OYW1lIHx8IHBhY2tldC5mcm9tLnN1YnN0cmluZygwLCAxMCksCiAgICAgICAgdHlwZTogcGFja2V0LnR5cGUgPT09ICdTWVBIX0ZJTEUnID8gJ2ZpbGUnIDogJ3RleHQnLAogICAgICAgIHRleHQ6IHBhY2tldC50ZXh0IHx8ICcnLAogICAgICAgIGZpbGVOYW1lOiBwYWNrZXQuZmlsZU5hbWUsCiAgICAgICAgZmlsZVNpemU6IHBhY2tldC5maWxlU2l6ZSwKICAgICAgICBmaWxlVHlwZTogcGFja2V0LmZpbGVUeXBlLAogICAgICAgIGZpbGVEYXRhOiBwYWNrZXQuZmlsZURhdGEsCiAgICAgICAgdGltZTogbXNnVGltZSwKICAgICAgICBpc091dGdvaW5nOiBmYWxzZSwKICAgICAgfTsKCiAgICAgIGNoYXQubWVzc2FnZXMucHVzaChpbmNvbWluZ01zZyk7CiAgICAgIGNoYXQubGFzdE1lc3NhZ2UgPSBwYWNrZXQudHlwZSA9PT0gJ1NZUEhfRklMRScgPyAn0JLQu9C+0LbQtdC90LjQtTogJyArIHBhY2tldC5maWxlTmFtZSA6IHBhY2tldC50ZXh0OwogICAgICBjaGF0LnRpbWUgPSBtc2dUaW1lOwoKICAgICAgaWYgKHN0YXRlLmFjdGl2ZUNoYXRJZCAhPT0gY2hhdC5pZCkgewogICAgICAgIGNoYXQudW5yZWFkID0gKGNoYXQudW5yZWFkIHx8IDApICsgMTsKICAgICAgICBzaG93VG9hc3QoJ9Cd0L7QstGL0Lkg0L/QsNC60LXRgiDQvtGCICcgKyAocGFja2V0LmZyb21OYW1lIHx8IHBhY2tldC5mcm9tKSk7CiAgICAgIH0KCiAgICAgIHNhdmVDaGF0cygpOwogICAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgICBpZiAoc3RhdGUuYWN0aXZlQ2hhdElkID09PSBjaGF0LmlkKSB7CiAgICAgICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgICB9CgogICAgICAvLyDQldGB0LvQuCDRgdC+0L7QsdGJ0LXQvdC40LUg0LDQtNGA0LXRgdC+0LLQsNC90L4g0L/QtdGA0YHQvtC90LDQu9GM0L3QviDQvdCw0Lwg4oCUINC+0YLQv9GA0LDQstC70Y/QtdC8INC/0L7QtNGC0LLQtdGA0LbQtNC10L3QuNC1INC00L7RgdGC0LDQstC60LggKEFDSykKICAgICAgaWYgKGlzRm9yVXMpIHsKICAgICAgICB2YXIgYWNrUGFja2V0ID0gewogICAgICAgICAgdHlwZTogJ1NZUEhfQUNLJywKICAgICAgICAgIGlkOiAnYWNrXycgKyBEYXRlLm5vdygpLAogICAgICAgICAgZnJvbTogc3RhdGUuaWRlbnRpdHkubXlJZCwKICAgICAgICAgIHRvOiBwYWNrZXQuZnJvbSwKICAgICAgICAgIHRhcmdldE1zZ0lkOiBwYWNrZXQuaWQsCiAgICAgICAgICB0aW1lc3RhbXA6IERhdGUubm93KCksCiAgICAgICAgfTsKICAgICAgICBkaXNwYXRjaFBhY2tldFRvTmV0d29yayhhY2tQYWNrZXQpOwogICAgICB9CiAgICB9CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0J7QotCf0KDQkNCS0JrQkCDQodCe0J7QkdCp0JXQndCY0Jkg0Jgg0JLQm9Ce0JbQldCd0JjQmQogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiBzZW5kTWVzc2FnZSgpIHsKICAgIHZhciBpbnB1dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdtZXNzYWdlSW5wdXQnKTsKICAgIGlmICghaW5wdXQpIHJldHVybjsKCiAgICB2YXIgdGV4dCA9IGlucHV0LnZhbHVlLnRyaW0oKTsKICAgIGlmICghdGV4dCkgcmV0dXJuOwoKICAgIHZhciBjaGF0ID0gc3RhdGUuY2hhdHMuZmluZChmdW5jdGlvbiAoYykgewogICAgICByZXR1cm4gYy5pZCA9PT0gc3RhdGUuYWN0aXZlQ2hhdElkOwogICAgfSk7CiAgICBpZiAoIWNoYXQpIHJldHVybjsKCiAgICB2YXIgbm93ID0gbmV3IERhdGUoKTsKICAgIHZhciB0aW1lU3RyID0gZm9ybWF0VGltZShub3cpOwoKICAgIHZhciBwYWNrZXQgPSB7CiAgICAgIHR5cGU6ICdTWVBIX01TRycsCiAgICAgIGlkOiAncGt0XycgKyBEYXRlLm5vdygpICsgJ18nICsgTWF0aC5yYW5kb20oKS50b1N0cmluZygzNikuc3Vic3RyKDIsIDYpLAogICAgICBmcm9tOiBzdGF0ZS5pZGVudGl0eS5teUlkLAogICAgICBmcm9tTmFtZTogc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUsCiAgICAgIHRvOiBjaGF0LmlkID09PSAnbWVzaC1icm9hZGNhc3QnID8gJ2Jyb2FkY2FzdCcgOiBjaGF0LmlkLAogICAgICB0ZXh0OiB0ZXh0LAogICAgICB0aW1lc3RhbXA6IG5vdy5nZXRUaW1lKCksCiAgICB9OwoKICAgIHZhciBuZXdNc2cgPSB7CiAgICAgIGlkOiBwYWNrZXQuaWQsCiAgICAgIHNlbmRlcjogJ21lJywKICAgICAgYXV0aG9yTmFtZTogc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUsCiAgICAgIHR5cGU6ICd0ZXh0JywKICAgICAgdGV4dDogdGV4dCwKICAgICAgdGltZTogdGltZVN0ciwKICAgICAgaXNPdXRnb2luZzogdHJ1ZSwKICAgICAgZGVsaXZlcmVkOiBmYWxzZSwKICAgIH07CgogICAgY2hhdC5tZXNzYWdlcy5wdXNoKG5ld01zZyk7CiAgICBjaGF0Lmxhc3RNZXNzYWdlID0gdGV4dDsKICAgIGNoYXQudGltZSA9IHRpbWVTdHI7CgogICAgaW5wdXQudmFsdWUgPSAnJzsKICAgIGlucHV0LmZvY3VzKCk7CgogICAgc2F2ZUNoYXRzKCk7CiAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwoKICAgIGRpc3BhdGNoUGFja2V0VG9OZXR3b3JrKHBhY2tldCk7CiAgICBzaG93VG9hc3QoJ9Ch0L7QvtCx0YnQtdC90LjQtSDQvtGC0L/RgNCw0LLQu9C10L3QviDQsiDRgNCw0LTQuNC+0LrQsNC90LDQuycpOwogIH0KCiAgZnVuY3Rpb24gaGFuZGxlRmlsZVNlbGVjdGVkKGUpIHsKICAgIHZhciBmaWxlID0gZS50YXJnZXQuZmlsZXMgJiYgZS50YXJnZXQuZmlsZXNbMF07CiAgICBpZiAoIWZpbGUpIHJldHVybjsKCiAgICBpZiAoZmlsZS5zaXplID4gOCAqIDEwMjQgKiAxMDI0KSB7CiAgICAgIGFsZXJ0KCfQoNCw0LfQvNC10YAg0YTQsNC50LvQsCDQv9GA0LXQstGL0YjQsNC10YIgOCDQnNCRLiDQlNC70Y8g0YDQsNC00LjQvtC/0LXRgNC10LTQsNGH0Lgg0LLRi9Cx0LXRgNC40YLQtSDRhNCw0LnQuyDQvNC10L3RjNGI0LXQs9C+INGA0LDQt9C80LXRgNCwLicpOwogICAgICBlLnRhcmdldC52YWx1ZSA9ICcnOwogICAgICByZXR1cm47CiAgICB9CgogICAgc2hvd1RvYXN0KCfQp9GC0LXQvdC40LUg0YTQsNC50LvQsDogJyArIGZpbGUubmFtZSk7CgogICAgdmFyIHJlYWRlciA9IG5ldyBGaWxlUmVhZGVyKCk7CiAgICByZWFkZXIub25sb2FkID0gZnVuY3Rpb24gKGV2dCkgewogICAgICB2YXIgZGF0YVVybCA9IGV2dC50YXJnZXQucmVzdWx0OwogICAgICB2YXIgY2hhdCA9IHN0YXRlLmNoYXRzLmZpbmQoZnVuY3Rpb24gKGMpIHsKICAgICAgICByZXR1cm4gYy5pZCA9PT0gc3RhdGUuYWN0aXZlQ2hhdElkOwogICAgICB9KTsKICAgICAgaWYgKCFjaGF0KSByZXR1cm47CgogICAgICB2YXIgbm93ID0gbmV3IERhdGUoKTsKICAgICAgdmFyIHRpbWVTdHIgPSBmb3JtYXRUaW1lKG5vdyk7CgogICAgICB2YXIgcGFja2V0ID0gewogICAgICAgIHR5cGU6ICdTWVBIX0ZJTEUnLAogICAgICAgIGlkOiAnZmlsZV8nICsgRGF0ZS5ub3coKSArICdfJyArIE1hdGgucmFuZG9tKCkudG9TdHJpbmcoMzYpLnN1YnN0cigyLCA2KSwKICAgICAgICBmcm9tOiBzdGF0ZS5pZGVudGl0eS5teUlkLAogICAgICAgIGZyb21OYW1lOiBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSwKICAgICAgICB0bzogY2hhdC5pZCA9PT0gJ21lc2gtYnJvYWRjYXN0JyA/ICdicm9hZGNhc3QnIDogY2hhdC5pZCwKICAgICAgICBmaWxlTmFtZTogZmlsZS5uYW1lLAogICAgICAgIGZpbGVTaXplOiBmaWxlLnNpemUsCiAgICAgICAgZmlsZVR5cGU6IGZpbGUudHlwZSwKICAgICAgICBmaWxlRGF0YTogZGF0YVVybCwKICAgICAgICB0aW1lc3RhbXA6IG5vdy5nZXRUaW1lKCksCiAgICAgIH07CgogICAgICB2YXIgbmV3TXNnID0gewogICAgICAgIGlkOiBwYWNrZXQuaWQsCiAgICAgICAgc2VuZGVyOiAnbWUnLAogICAgICAgIGF1dGhvck5hbWU6IHN0YXRlLmlkZW50aXR5LmRpc3BsYXlOYW1lLAogICAgICAgIHR5cGU6ICdmaWxlJywKICAgICAgICBmaWxlTmFtZTogZmlsZS5uYW1lLAogICAgICAgIGZpbGVTaXplOiBmaWxlLnNpemUsCiAgICAgICAgZmlsZVR5cGU6IGZpbGUudHlwZSwKICAgICAgICBmaWxlRGF0YTogZGF0YVVybCwKICAgICAgICB0aW1lOiB0aW1lU3RyLAogICAgICAgIGlzT3V0Z29pbmc6IHRydWUsCiAgICAgICAgZGVsaXZlcmVkOiBmYWxzZSwKICAgICAgfTsKCiAgICAgIGNoYXQubWVzc2FnZXMucHVzaChuZXdNc2cpOwogICAgICBjaGF0Lmxhc3RNZXNzYWdlID0gJ9Ck0LDQudC7OiAnICsgZmlsZS5uYW1lOwogICAgICBjaGF0LnRpbWUgPSB0aW1lU3RyOwoKICAgICAgc2F2ZUNoYXRzKCk7CiAgICAgIHJlbmRlckNoYXRMaXN0KCk7CiAgICAgIHJlbmRlckFjdGl2ZUNoYXQoKTsKCiAgICAgIGRpc3BhdGNoUGFja2V0VG9OZXR3b3JrKHBhY2tldCk7CiAgICAgIHNob3dUb2FzdCgn0KTQsNC50Lsg0L7RgtC/0YDQsNCy0LvQtdC9INCyINGA0LDQtNC40L7QutCw0L3QsNC7Jyk7CiAgICAgIGUudGFyZ2V0LnZhbHVlID0gJyc7CiAgICB9OwoKICAgIHJlYWRlci5vbmVycm9yID0gZnVuY3Rpb24gKCkgewogICAgICBhbGVydCgn0J7RiNC40LHQutCwINC/0YDQuCDRh9GC0LXQvdC40Lgg0YTQsNC50LvQsCcpOwogICAgfTsKCiAgICByZWFkZXIucmVhZEFzRGF0YVVSTChmaWxlKTsKICB9CgogIGZ1bmN0aW9uIGNvcHlUb0NsaXBib2FyZCh0ZXh0LCBzdWNjZXNzTXNnKSB7CiAgICBpZiAobmF2aWdhdG9yLmNsaXBib2FyZCAmJiBuYXZpZ2F0b3IuY2xpcGJvYXJkLndyaXRlVGV4dCkgewogICAgICBuYXZpZ2F0b3IuY2xpcGJvYXJkLndyaXRlVGV4dCh0ZXh0KS50aGVuKGZ1bmN0aW9uICgpIHsKICAgICAgICBzaG93VG9hc3Qoc3VjY2Vzc01zZyk7CiAgICAgIH0pOwogICAgfSBlbHNlIHsKICAgICAgc2hvd1RvYXN0KHN1Y2Nlc3NNc2cpOwogICAgfQogIH0KCiAgLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQogIC8vINCh0JvQo9Co0JDQotCV0JvQmCDQodCe0JHQq9Ci0JjQmQogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiBzZXR1cEV2ZW50TGlzdGVuZXJzKCkgewogICAgLy8gMS4g0J7RgtC/0YDQsNCy0LrQsCDRgdC+0L7QsdGJ0LXQvdC40LkKICAgIHZhciBzZW5kQnRuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blNlbmQnKTsKICAgIGlmIChzZW5kQnRuKSB7CiAgICAgIHNlbmRCdG4ub25jbGljayA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgZS5wcmV2ZW50RGVmYXVsdCgpOwogICAgICAgIHNlbmRNZXNzYWdlKCk7CiAgICAgIH07CiAgICB9CgogICAgdmFyIG1zZ0lucHV0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ21lc3NhZ2VJbnB1dCcpOwogICAgaWYgKG1zZ0lucHV0KSB7CiAgICAgIG1zZ0lucHV0Lm9ua2V5ZG93biA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgaWYgKGUua2V5ID09PSAnRW50ZXInICYmICFlLnNoaWZ0S2V5KSB7CiAgICAgICAgICBlLnByZXZlbnREZWZhdWx0KCk7CiAgICAgICAgICBzZW5kTWVzc2FnZSgpOwogICAgICAgIH0KICAgICAgfTsKICAgIH0KCiAgICAvLyAyLiDQn9GA0LjQutGA0LXQv9C70LXQvdC40LUg0YTQsNC50LvQvtCyCiAgICB2YXIgYXR0YWNoQnRuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bkF0dGFjaCcpOwogICAgdmFyIGZpbGVJbnB1dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdmaWxlSW5wdXQnKTsKICAgIGlmIChhdHRhY2hCdG4gJiYgZmlsZUlucHV0KSB7CiAgICAgIGF0dGFjaEJ0bi5vbmNsaWNrID0gZnVuY3Rpb24gKGUpIHsKICAgICAgICBlLnByZXZlbnREZWZhdWx0KCk7CiAgICAgICAgZmlsZUlucHV0LmNsaWNrKCk7CiAgICAgIH07CiAgICAgIGZpbGVJbnB1dC5vbmNoYW5nZSA9IGhhbmRsZUZpbGVTZWxlY3RlZDsKICAgIH0KCiAgICAvLyAzLiDQndCw0LLQuNCz0LDRhtC40Y8g0L3QsCDRgdC80LDRgNGC0YTQvtC90LDRhQogICAgdmFyIGJhY2tCdG4gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQmFja1RvQ2hhdHMnKTsKICAgIGlmIChiYWNrQnRuKSB7CiAgICAgIGJhY2tCdG4ub25jbGljayA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgZS5wcmV2ZW50RGVmYXVsdCgpOwogICAgICAgIHZhciBsYXlvdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYXBwTGF5b3V0Jyk7CiAgICAgICAgaWYgKGxheW91dCkgbGF5b3V0LmNsYXNzTGlzdC5yZW1vdmUoJ21vYmlsZS1jaGF0LW9wZW4nKTsKICAgICAgfTsKICAgIH0KCiAgICAvLyA0LiDQmtC+0L/QuNGA0L7QstCw0L3QuNC1IElECiAgICB2YXIgbXlOb2RlQ2FyZCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdteU5vZGVDYXJkJyk7CiAgICB2YXIgY29weUJ0biA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5Db3B5TXlJZCcpOwogICAgZnVuY3Rpb24gY29weUlkKGUpIHsKICAgICAgaWYgKGUpIGUuc3RvcFByb3BhZ2F0aW9uKCk7CiAgICAgIGNvcHlUb0NsaXBib2FyZChzdGF0ZS5pZGVudGl0eS5teUlkLCAn0JLQsNGIIFN5cGhlciBJRCDRgdC60L7Qv9C40YDQvtCy0LDQvScpOwogICAgfQogICAgaWYgKG15Tm9kZUNhcmQpIG15Tm9kZUNhcmQub25jbGljayA9IGNvcHlJZDsKICAgIGlmIChjb3B5QnRuKSBjb3B5QnRuLm9uY2xpY2sgPSBjb3B5SWQ7CgogICAgLy8gNS4g0J/QvtC40YHQugogICAgdmFyIHNlYXJjaElucHV0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NlYXJjaElucHV0Jyk7CiAgICBpZiAoc2VhcmNoSW5wdXQpIHsKICAgICAgc2VhcmNoSW5wdXQub25pbnB1dCA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBzdGF0ZS5zZWFyY2hRdWVyeSA9IHNlYXJjaElucHV0LnZhbHVlOwogICAgICAgIHJlbmRlckNoYXRMaXN0KCk7CiAgICAgIH07CiAgICB9CgogICAgLy8gNi4g0JzQvtC00LDQu9GM0L3QvtC1INC+0LrQvdC+INC90L7QstC+0LPQviDQtNC40LDQu9C+0LPQsAogICAgdmFyIG5ld0NoYXRNb2RhbCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCduZXdDaGF0TW9kYWwnKTsKICAgIHZhciBidG5OZXdDaGF0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bk5ld0NoYXQnKTsKICAgIHZhciBidG5DbG9zZU5ld0NoYXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VOZXdDaGF0Jyk7CiAgICB2YXIgYnRuQ2FuY2VsTmV3Q2hhdCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5DYW5jZWxOZXdDaGF0Jyk7CiAgICB2YXIgYnRuQ29uZmlybU5ld0NoYXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ29uZmlybU5ld0NoYXQnKTsKICAgIHZhciBuZXdDaGF0SW5wdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbmV3Q2hhdFBlZXJJZElucHV0Jyk7CgogICAgaWYgKGJ0bk5ld0NoYXQgJiYgbmV3Q2hhdE1vZGFsKSB7CiAgICAgIGJ0bk5ld0NoYXQub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBpZiAobmV3Q2hhdElucHV0KSBuZXdDaGF0SW5wdXQudmFsdWUgPSAnJzsKICAgICAgICByZW5kZXJOZWFyYnlQZWVycygpOwogICAgICAgIG5ld0NoYXRNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlTmV3Q2hhdCgpIHsKICAgICAgaWYgKG5ld0NoYXRNb2RhbCkgbmV3Q2hhdE1vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlTmV3Q2hhdCkgYnRuQ2xvc2VOZXdDaGF0Lm9uY2xpY2sgPSBjbG9zZU5ld0NoYXQ7CiAgICBpZiAoYnRuQ2FuY2VsTmV3Q2hhdCkgYnRuQ2FuY2VsTmV3Q2hhdC5vbmNsaWNrID0gY2xvc2VOZXdDaGF0OwoKICAgIGlmIChidG5Db25maXJtTmV3Q2hhdCAmJiBuZXdDaGF0SW5wdXQpIHsKICAgICAgYnRuQ29uZmlybU5ld0NoYXQub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICB2YXIgaWQgPSBuZXdDaGF0SW5wdXQudmFsdWUudHJpbSgpOwogICAgICAgIGlmICghaWQpIHJldHVybjsKICAgICAgICBjbG9zZU5ld0NoYXQoKTsKICAgICAgICBzdGFydERpcmVjdENoYXRXaXRoUGVlcihpZCk7CiAgICAgIH07CiAgICB9CgogICAgLy8gNy4g0JzQvtC00LDQu9GM0L3QvtC1INC+0LrQvdC+INC90LDRgdGC0YDQvtC10LoKICAgIHZhciBzZXR0aW5nc01vZGFsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NldHRpbmdzTW9kYWwnKTsKICAgIHZhciBidG5TZXR0aW5ncyA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5TZXR0aW5ncycpOwogICAgdmFyIGJ0bkNsb3NlU2V0dGluZ3MgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VTZXR0aW5ncycpOwogICAgdmFyIGJ0blNhdmVTZXR0aW5ncyA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5TYXZlU2V0dGluZ3MnKTsKICAgIHZhciBpbnB1dE5hbWUgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnaW5wdXREaXNwbGF5TmFtZScpOwogICAgdmFyIGJ0blJlZ2VuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blJlZ2VuZXJhdGVJZCcpOwoKICAgIGlmIChidG5TZXR0aW5ncyAmJiBzZXR0aW5nc01vZGFsKSB7CiAgICAgIGJ0blNldHRpbmdzLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgc2V0dGluZ3NNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlU2V0dGluZ3MoKSB7CiAgICAgIGlmIChzZXR0aW5nc01vZGFsKSBzZXR0aW5nc01vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlU2V0dGluZ3MpIGJ0bkNsb3NlU2V0dGluZ3Mub25jbGljayA9IGNsb3NlU2V0dGluZ3M7CgogICAgaWYgKGJ0blNhdmVTZXR0aW5ncyAmJiBpbnB1dE5hbWUpIHsKICAgICAgYnRuU2F2ZVNldHRpbmdzLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgdmFyIG5ld05hbWUgPSBpbnB1dE5hbWUudmFsdWUudHJpbSgpOwogICAgICAgIGlmIChuZXdOYW1lKSB7CiAgICAgICAgICBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSA9IG5ld05hbWU7CiAgICAgICAgICBsb2NhbFN0b3JhZ2Uuc2V0SXRlbSgnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnLCBKU09OLnN0cmluZ2lmeShzdGF0ZS5pZGVudGl0eSkpOwogICAgICAgICAgdmFyIG15TmFtZUVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlOb2RlRGlzcGxheU5hbWUnKTsKICAgICAgICAgIGlmIChteU5hbWVFbGVtKSBteU5hbWVFbGVtLnRleHRDb250ZW50ID0gbmV3TmFtZTsKICAgICAgICB9CiAgICAgICAgY2xvc2VTZXR0aW5ncygpOwogICAgICAgIHNob3dUb2FzdCgn0J3QsNGB0YLRgNC+0LnQutC4INGB0L7RhdGA0LDQvdC10L3RiycpOwogICAgICB9OwogICAgfQoKICAgIGlmIChidG5SZWdlbikgewogICAgICBidG5SZWdlbi5vbmNsaWNrID0gZnVuY3Rpb24gKCkgewogICAgICAgIGlmIChjb25maXJtKCfQodCz0LXQvdC10YDQuNGA0L7QstCw0YLRjCDQsNCx0YHQvtC70Y7RgtC90L4g0L3QvtCy0YvQuSDRg9C90LjQutCw0LvRjNC90YvQuSBTeXBoZXIgSUQ/INCS0YHQtSDRgtC10LrRg9GJ0LjQtSDQutC+0L3RgtCw0LrRgtGLINC00L7Qu9C20L3RiyDQsdGD0LTRg9GCINC40YHQv9C+0LvRjNC30L7QstCw0YLRjCDQvdC+0LLRi9C5IElELicpKSB7CiAgICAgICAgICBsb2NhbFN0b3JhZ2UucmVtb3ZlSXRlbSgnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnKTsKICAgICAgICAgIHN0YXRlLmlkZW50aXR5ID0gZ2V0T3JDcmVhdGVJZGVudGl0eSgpOwogICAgICAgICAgaW5pdFVJKCk7CiAgICAgICAgICBjbG9zZVNldHRpbmdzKCk7CiAgICAgICAgICBzaG93VG9hc3QoJ9Cd0L7QstGL0Lkg0YPQvdC40LrQsNC70YzQvdGL0LkgSUQg0YPQt9C70LAg0YHQs9C10L3QtdGA0LjRgNC+0LLQsNC9Jyk7CiAgICAgICAgfQogICAgICB9OwogICAgfQoKICAgIC8vIDguINCc0L7QtNCw0LvRjNC90L7QtSDQvtC60L3QviDRgdCy0LXRgNC60Lgg0L7RgtC/0LXRh9Cw0YLQutCwINC60LvRjtGH0LXQuQogICAgdmFyIHZlcmlmeU1vZGFsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3ZlcmlmeUtleXNNb2RhbCcpOwogICAgdmFyIGJ0blZlcmlmeSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5WZXJpZnlLZXlzJyk7CiAgICB2YXIgYnRuQ2xvc2VWZXJpZnkgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VWZXJpZnlLZXlzJyk7CiAgICB2YXIgYnRuT2tWZXJpZnkgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuT2tWZXJpZnlLZXlzJyk7CgogICAgaWYgKGJ0blZlcmlmeSAmJiB2ZXJpZnlNb2RhbCkgewogICAgICBidG5WZXJpZnkub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICB2ZXJpZnlNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlVmVyaWZ5KCkgewogICAgICBpZiAodmVyaWZ5TW9kYWwpIHZlcmlmeU1vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlVmVyaWZ5KSBidG5DbG9zZVZlcmlmeS5vbmNsaWNrID0gY2xvc2VWZXJpZnk7CiAgICBpZiAoYnRuT2tWZXJpZnkpIGJ0bk9rVmVyaWZ5Lm9uY2xpY2sgPSBjbG9zZVZlcmlmeTsKCiAgICAvLyA5LiDQotCw0LnQvNC10YAg0YHQsNC80L7Rg9C90LjRh9GC0L7QttC10L3QuNGPCiAgICB2YXIgYnRuU2VsZkRlc3RydWN0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blNlbGZEZXN0cnVjdCcpOwogICAgdmFyIHNlbGZEZXN0cnVjdExhYmVsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NlbGZEZXN0cnVjdExhYmVsJyk7CiAgICBpZiAoYnRuU2VsZkRlc3RydWN0ICYmIHNlbGZEZXN0cnVjdExhYmVsKSB7CiAgICAgIGJ0blNlbGZEZXN0cnVjdC5vbmNsaWNrID0gZnVuY3Rpb24gKCkgewogICAgICAgIHN0YXRlLnNlbGZEZXN0cnVjdEluZGV4ID0gKHN0YXRlLnNlbGZEZXN0cnVjdEluZGV4ICsgMSkgJSBzdGF0ZS5zZWxmRGVzdHJ1Y3RPcHRpb25zLmxlbmd0aDsKICAgICAgICB2YXIgb3B0ID0gc3RhdGUuc2VsZkRlc3RydWN0T3B0aW9uc1tzdGF0ZS5zZWxmRGVzdHJ1Y3RJbmRleF07CiAgICAgICAgc2VsZkRlc3RydWN0TGFiZWwudGV4dENvbnRlbnQgPSBvcHQ7CiAgICAgICAgc2hvd1RvYXN0KCfQotCw0LnQvNC10YAg0YHQsNC80L7Rg9C90LjRh9GC0L7QttC10L3QuNGPOiAnICsgb3B0KTsKICAgICAgfTsKICAgIH0KCiAgICAvLyAxMC4g0K3QutGB0YLRgNC10L3QvdC+0LUg0YHRgtC40YDQsNC90LjQtSDQstGB0LXRhSDQtNCw0L3QvdGL0YUKICAgIHZhciBidG5CdXJuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bkVtZXJnZW5jeUJ1cm4nKTsKICAgIGlmIChidG5CdXJuKSB7CiAgICAgIGJ0bkJ1cm4ub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBpZiAoCiAgICAgICAgICBjb25maXJtKAogICAgICAgICAgICAn0JLQndCY0JzQkNCd0JjQlTog0JLRi9C/0L7Qu9C90LjRgtGMINGN0LrRgdGC0YDQtdC90L3QvtC1INGB0YLQuNGA0LDQvdC40LUg0LLRgdC10YUg0LrQu9GO0YfQtdC5LCDQv9C10YDQtdC/0LjRgdC60Lgg0Lgg0LjRgdGC0L7RgNC40Lgg0L3QsCDQtNCw0L3QvdC+0Lwg0YPRgdGC0YDQvtC50YHRgtCy0LU/JwogICAgICAgICAgKQogICAgICAgICkgewogICAgICAgICAgbG9jYWxTdG9yYWdlLmNsZWFyKCk7CiAgICAgICAgICBzaG93VG9hc3QoJ9CS0YHQtSDQtNCw0L3QvdGL0LUg0YHRgtGR0YDRgtGLJyk7CiAgICAgICAgICBzZXRUaW1lb3V0KGZ1bmN0aW9uICgpIHsKICAgICAgICAgICAgbG9jYXRpb24ucmVsb2FkKCk7CiAgICAgICAgICB9LCA2MDApOwogICAgICAgIH0KICAgICAgfTsKICAgIH0KICB9CgogIC8vINCX0LDQv9GD0YHQuiDQv9GA0LjQu9C+0LbQtdC90LjRjwogIGlmIChkb2N1bWVudC5yZWFkeVN0YXRlID09PSAnbG9hZGluZycpIHsKICAgIGRvY3VtZW50LmFkZEV2ZW50TGlzdGVuZXIoJ0RPTUNvbnRlbnRMb2FkZWQnLCBpbml0VUkpOwogIH0gZWxzZSB7CiAgICBpbml0VUkoKTsKICB9Cn0pKCk7Cg=="));
                }
                return _js;
            }
        }
    }
}
