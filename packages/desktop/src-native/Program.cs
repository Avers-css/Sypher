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

                bool hasSypherService = false;
                if (uuids != null)
                {
                    foreach (var u in uuids)
                    {
                        if (u is Guid && ((Guid)u) == SypherUuid) { hasSypherService = true; break; }
                    }
                }

                string mfgStr = "";
                PropertyInfo mfgProp = ad.GetType().GetProperty("ManufacturerData");
                IEnumerable mfgs = mfgProp != null ? mfgProp.GetValue(ad, null) as IEnumerable : null;
                if (mfgs != null)
                {
                    foreach (var m in mfgs)
                    {
                        try
                        {
                            PropertyInfo cidProp = m.GetType().GetProperty("CompanyId");
                            ushort cid = (ushort)cidProp.GetValue(m, null);
                            if (cid == 0x5950)
                            {
                                PropertyInfo dataProp = m.GetType().GetProperty("Data");
                                object buf = dataProp.GetValue(m, null);
                                MethodInfo toArray = typeof(WindowsRuntimeBufferExtensions).GetMethod("ToArray", BindingFlags.Public | BindingFlags.Static);
                                byte[] mBytes = (byte[])toArray.Invoke(null, new object[] { buf });
                                if (mBytes != null && mBytes.Length > 0)
                                {
                                    string s = Encoding.UTF8.GetString(mBytes);
                                    if (s.StartsWith("SYP"))
                                    {
                                        mfgStr = s;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // STRICT FILTER: Discard any foreign Bluetooth devices (smartwatches, TVs, random Macs, iPhones)
                // A device MUST explicitly advertise Sypher Service UUID or Sypher Manufacturer Data (0x5950 starting with SYP)
                bool isSypher = hasSypherService || (!string.IsNullOrEmpty(mfgStr) && mfgStr.StartsWith("SYP"));
                if (!isSypher) return;

                lock (discoveredBlePeers)
                {
                    discoveredBlePeers[mac] = rawAddr;
                }

                bool isWin = mfgStr.IndexOf("WIN", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isLin = mfgStr.IndexOf("LIN", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMac = mfgStr.IndexOf("MAC", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isDroid = mfgStr.IndexOf("DROID", StringComparison.OrdinalIgnoreCase) >= 0 || hasSypherService;

                string prefix = isWin ? "syp-win-" : isLin ? "syp-lin-" : isMac ? "syp-mac-" : isDroid ? "syp-droid-" : "syp-node-";
                string peerName = isWin ? "Компьютер Windows" : isLin ? "Компьютер Linux" : isMac ? "Компьютер Mac" : isDroid ? "Телефон Android" : "Узел Sypher";

                string shortId = mac.Replace(":", "").ToLower();
                if (shortId.Length > 6) shortId = shortId.Substring(shortId.Length - 6);
                string peerId = prefix + shortId;

                double distance = Math.Round(Math.Pow(10.0, (-59.0 - (double)rssi) / 20.0), 1);

                    string sseJson = "data: {\"type\":\"BLE_PEER_DISCOVERED\",\"peerId\":\"" + peerId +
                        "\",\"name\":\"" + peerName +
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
                    else _css = Encoding.UTF8.GetString(Convert.FromBase64String("LyogPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICAgU1lQSEVSIOKAlCBIaWdoLUVuZCBEYXJrIEFNT0xFRCBNZXNoIE1lc3NlbmdlcgogICBTdHJpY3RseSBaZXJvLUVtb2ppIOKAoiBQdXJlIFZlY3RvciBUeXBvZ3JhcGh5IOKAoiBUZWxlZ3JhbSAvIFNpZ25hbCBHcmFkZSBVSQogICA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PSAqLwoKOnJvb3QgewogIC0tYmctZGVlcDogIzA1MDcwYTsKICAtLWJnLXN1cmZhY2U6ICMwYjBlMTU7CiAgLS1iZy1lbGV2YXRlZDogIzEyMTcyMjsKICAtLWJnLWhvdmVyOiAjMTgyMDMwOwogIC0tYmctYWN0aXZlOiAjMWYyYTNmOwoKICAtLWJvcmRlcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA3KTsKICAtLWJvcmRlci1mb2N1czogcmdiYSgwLCAyNDAsIDI1NSwgMC40KTsKCiAgLS10ZXh0LXByaW1hcnk6ICNmZmZmZmY7CiAgLS10ZXh0LXNlY29uZGFyeTogIzhhOTZhODsKICAtLXRleHQtbXV0ZWQ6ICM1NjYzNzU7CgogIC0tYWNjZW50LWN5YW46ICMwMGYwZmY7CiAgLS1hY2NlbnQtYXp1cmU6ICMwMDdhZmY7CiAgLS1hY2NlbnQtZW1lcmFsZDogIzAwZTY3NjsKICAtLWFjY2VudC1kYW5nZXI6ICNmZjNiMzA7CgogIC0tbXNnLWluLWJnOiAjMTExNjIyOwogIC0tbXNnLWluLWJvcmRlcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA1KTsKICAtLW1zZy1vdXQtYmc6ICMwZjMwNDk7CiAgLS1tc2ctb3V0LWJvcmRlcjogcmdiYSgwLCAyNDAsIDI1NSwgMC4yKTsKCiAgLS1mb250LXNhbnM6ICdQbHVzIEpha2FydGEgU2FucycsIC1hcHBsZS1zeXN0ZW0sIEJsaW5rTWFjU3lzdGVtRm9udCwgJ1NlZ29lIFVJJywgUm9ib3RvLCBIZWx2ZXRpY2EsIEFyaWFsLCBzYW5zLXNlcmlmOwogIC0tZm9udC1tb25vOiAnSmV0QnJhaW5zIE1vbm8nLCAnQ2FzY2FkaWEgQ29kZScsIE1lbmxvLCBNb25hY28sIENvbnNvbGFzLCBtb25vc3BhY2U7CgogIC0tc2lkZWJhci13aWR0aDogMzUwcHg7CiAgLS1oZWFkZXItaGVpZ2h0OiA2MHB4Owp9CgoqIHsKICBib3gtc2l6aW5nOiBib3JkZXItYm94OwogIG1hcmdpbjogMDsKICBwYWRkaW5nOiAwOwogIC13ZWJraXQtdGFwLWhpZ2hsaWdodC1jb2xvcjogdHJhbnNwYXJlbnQ7Cn0KCmh0bWwsIGJvZHkgewogIHdpZHRoOiAxMDAlOwogIGhlaWdodDogMTAwJTsKICBvdmVyZmxvdzogaGlkZGVuOwogIGJhY2tncm91bmQtY29sb3I6IHZhcigtLWJnLWRlZXApOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtZmFtaWx5OiB2YXIoLS1mb250LXNhbnMpOwogIGZvbnQtc2l6ZTogMTRweDsKICBsaW5lLWhlaWdodDogMS40OwogIC13ZWJraXQtZm9udC1zbW9vdGhpbmc6IGFudGlhbGlhc2VkOwogIC1tb3otb3N4LWZvbnQtc21vb3RoaW5nOiBncmF5c2NhbGU7Cn0KCi8qIEFQUCBTSEVMTCAqLwouYXBwLWxheW91dCB7CiAgZGlzcGxheTogZmxleDsKICB3aWR0aDogMTAwdnc7CiAgaGVpZ2h0OiAxMDBkdmg7CiAgcG9zaXRpb246IHJlbGF0aXZlOwogIG92ZXJmbG93OiBoaWRkZW47CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZGVlcCk7Cn0KCi8qIFNJREVCQVIgKi8KLnNpZGViYXIgewogIHdpZHRoOiB2YXIoLS1zaWRlYmFyLXdpZHRoKTsKICBoZWlnaHQ6IDEwMCU7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctc3VyZmFjZSk7CiAgYm9yZGVyLXJpZ2h0OiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZmxleC1zaHJpbms6IDA7CiAgei1pbmRleDogMTA7CiAgdHJhbnNpdGlvbjogdHJhbnNmb3JtIDAuMjRzIGN1YmljLWJlemllcigwLjE2LCAxLCAwLjMsIDEpOwp9Cgouc2lkZWJhci1oZWFkZXIgewogIHBhZGRpbmc6IDE0cHggMTZweCAxMHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLXN1cmZhY2UpOwogIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDEycHg7Cn0KCi5icmFuZC1iYXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47Cn0KCi5icmFuZC1pZGVudGl0eSB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTBweDsKfQoKLmJyYW5kLWljb24tYm94IHsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGJhY2tncm91bmQ6ICMwMDAwMDA7CiAgYm9yZGVyOiAxLjVweCBzb2xpZCAjZmZmZmZmOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKfQoKLmJyYW5kLXRpdGxlLXdyYXAgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDhweDsKfQoKLmJyYW5kLW5hbWUgewogIGZvbnQtc2l6ZTogMTZweDsKICBmb250LXdlaWdodDogODAwOwogIGxldHRlci1zcGFjaW5nOiAxLjVweDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLmJyYW5kLWJhZGdlIHsKICBmb250LXNpemU6IDlweDsKICBmb250LXdlaWdodDogNzAwOwogIGxldHRlci1zcGFjaW5nOiAwLjhweDsKICBwYWRkaW5nOiAycHggNnB4OwogIGJvcmRlci1yYWRpdXM6IDRweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDIzMCwgMTE4LCAwLjEyKTsKICBjb2xvcjogdmFyKC0tYWNjZW50LWVtZXJhbGQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMCwgMjMwLCAxMTgsIDAuMjUpOwp9CgouaGVhZGVyLWFjdGlvbi1idXR0b25zIHsKICBkaXNwbGF5OiBmbGV4OwogIGdhcDogNnB4Owp9CgoubmF2LWljb24tYnRuIHsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICB3aWR0aDogMzJweDsKICBoZWlnaHQ6IDMycHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5uYXYtaWNvbi1idG46aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWhvdmVyKTsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLyogTVkgTk9ERSBDQVJEICovCi5teS1ub2RlLWNhcmQgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDEwcHg7CiAgcGFkZGluZzogOHB4IDEwcHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm9yZGVyLXJhZGl1czogMTBweDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5teS1ub2RlLWNhcmQ6aG92ZXIgewogIGJvcmRlci1jb2xvcjogdmFyKC0tYm9yZGVyLWZvY3VzKTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1ob3Zlcik7Cn0KCi5ub2RlLWF2YXRhciB7CiAgd2lkdGg6IDM0cHg7CiAgaGVpZ2h0OiAzNHB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiBsaW5lYXItZ3JhZGllbnQoMTM1ZGVnLCAjMDA3YWZmLCAjMDBmMGZmKTsKICBjb2xvcjogIzA1MDcwYTsKICBmb250LXdlaWdodDogNzAwOwogIGZvbnQtc2l6ZTogMTNweDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgZmxleC1zaHJpbms6IDA7Cn0KCi5ub2RlLWluZm8gewogIGZsZXg6IDE7CiAgbWluLXdpZHRoOiAwOwp9Cgoubm9kZS1uYW1lLXJvdyB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4Owp9Cgoubm9kZS1uYW1lIHsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5zdGF0dXMtcHVsc2UtZG90IHsKICB3aWR0aDogN3B4OwogIGhlaWdodDogN3B4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtZW1lcmFsZCk7CiAgYm94LXNoYWRvdzogMCAwIDZweCB2YXIoLS1hY2NlbnQtZW1lcmFsZCk7CiAgYW5pbWF0aW9uOiBwdWxzZS1yaW5nIDJzIGluZmluaXRlOwp9CgpAa2V5ZnJhbWVzIHB1bHNlLXJpbmcgewogIDAlIHsgdHJhbnNmb3JtOiBzY2FsZSgwLjk1KTsgb3BhY2l0eTogMC44OyB9CiAgNTAlIHsgdHJhbnNmb3JtOiBzY2FsZSgxLjE1KTsgb3BhY2l0eTogMTsgfQogIDEwMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDAuOTUpOyBvcGFjaXR5OiAwLjg7IH0KfQoKLm5vZGUtaWQtbW9ubyB7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtbW9ubyk7CiAgZm9udC1zaXplOiAxMXB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwp9Cgoubm9kZS1jb3B5LWJ0biB7CiAgYmFja2dyb3VuZDogdHJhbnNwYXJlbnQ7CiAgYm9yZGVyOiBub25lOwogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKICBwYWRkaW5nOiA0cHg7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGN1cnNvcjogcG9pbnRlcjsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5ub2RlLWNvcHktYnRuOmhvdmVyIHsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMSk7Cn0KCi8qIFJBREFSIFNUQVRVUyBCQVIgKi8KLnJhZGFyLXN0YXR1cy1iYXIgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47CiAgcGFkZGluZzogMTBweCAxNnB4OwogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjQwLCAyNTUsIDAuMDMpOwogIGJvcmRlci1ib3R0b206IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwp9CgoucmFkYXItbGVmdCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTBweDsKfQoKLnJhZGFyLXdhdmVzIHsKICBwb3NpdGlvbjogcmVsYXRpdmU7CiAgd2lkdGg6IDIwcHg7CiAgaGVpZ2h0OiAyMHB4OwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKfQoKLnJhZGFyLWNlbnRlci1kb3QgewogIHdpZHRoOiA2cHg7CiAgaGVpZ2h0OiA2cHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKfQoKLndhdmUgewogIHBvc2l0aW9uOiBhYnNvbHV0ZTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1hY2NlbnQtY3lhbik7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIG9wYWNpdHk6IDA7CiAgYW5pbWF0aW9uOiByaXBwbGUgMnMgaW5maW5pdGUgY3ViaWMtYmV6aWVyKDAuMiwgMC44LCAwLjIsIDEpOwp9Cgoud2F2ZS0xIHsgd2lkdGg6IDE0cHg7IGhlaWdodDogMTRweDsgYW5pbWF0aW9uLWRlbGF5OiAwczsgfQoud2F2ZS0yIHsgd2lkdGg6IDIycHg7IGhlaWdodDogMjJweDsgYW5pbWF0aW9uLWRlbGF5OiAwLjZzOyB9CgpAa2V5ZnJhbWVzIHJpcHBsZSB7CiAgMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDAuNCk7IG9wYWNpdHk6IDAuOTsgfQogIDEwMCUgeyB0cmFuc2Zvcm06IHNjYWxlKDEuNCk7IG9wYWNpdHk6IDA7IH0KfQoKLnJhZGFyLXRleHQtYmxvY2sgewogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKfQoKLnJhZGFyLXRpdGxlIHsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKfQoKLnJhZGFyLWRlc2MgewogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwp9CgoucGVlci1jb3VudGVyLWJhZGdlIHsKICBmb250LXNpemU6IDExcHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBwYWRkaW5nOiAycHggN3B4OwogIGJvcmRlci1yYWRpdXM6IDEwcHg7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAyNDAsIDI1NSwgMC4xMik7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDI0MCwgMjU1LCAwLjI1KTsKfQoKLyogTkVBUkJZIFBFRVJTIFJBREFSIFNFQ1RJT04gKi8KLm5lYXJieS1zZWN0aW9uIHsKICBwYWRkaW5nOiAxMHB4IDE2cHg7CiAgYm9yZGVyLWJvdHRvbTogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAwLCAwLCAwLjIpOwp9Cgouc2VjdGlvbi1sYWJlbCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4OwogIGZvbnQtc2l6ZTogMTBweDsKICBmb250LXdlaWdodDogNzAwOwogIGxldHRlci1zcGFjaW5nOiAwLjlweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgbWFyZ2luLWJvdHRvbTogOHB4Owp9CgoubmVhcmJ5LXBlZXItbGlzdCB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogNnB4OwogIG1heC1oZWlnaHQ6IDE0MHB4OwogIG92ZXJmbG93LXk6IGF1dG87Cn0KCi5lbXB0eS1uZWFyYnktcGxhY2Vob2xkZXIgewogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgcGFkZGluZzogNHB4IDA7CiAgZm9udC1zdHlsZTogaXRhbGljOwp9CgoubmVhcmJ5LXBlZXItcm93IHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwogIHBhZGRpbmc6IDhweCAxMHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDhweDsKICB0cmFuc2l0aW9uOiBhbGwgMC4xNXMgZWFzZTsKICBnYXA6IDhweDsKfQoKLm5lYXJieS1wZWVyLXJvdzpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctaG92ZXIpOwogIGJvcmRlci1jb2xvcjogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjI1KTsKfQoKLnBlZXItcm93LWxlZnQgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDEwcHg7CiAgbWluLXdpZHRoOiAwOwogIGZsZXg6IDE7Cn0KCi5uZWFyYnktYXZhdGFyIHsKICB3aWR0aDogMjhweDsKICBoZWlnaHQ6IDI4cHg7CiAgYm9yZGVyLXJhZGl1czogNTAlOwogIGJhY2tncm91bmQ6ICMxZTI5M2I7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgZm9udC1zaXplOiAxMXB4OwogIGZvbnQtd2VpZ2h0OiA4MDA7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIGZsZXgtc2hyaW5rOiAwOwp9CgoubmVhcmJ5LWluZm8gewogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDJweDsKICBtaW4td2lkdGg6IDA7CiAgZmxleDogMTsKfQoKLm5lYXJieS10aXRsZS1yb3cgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDZweDsKICBtaW4td2lkdGg6IDA7Cn0KCi5uZWFyYnktbmFtZSB7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA3MDA7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwp9CgoucGxhdGZvcm0tcGlsbCB7CiAgZm9udC1zaXplOiA5cHg7CiAgZm9udC13ZWlnaHQ6IDgwMDsKICBsZXR0ZXItc3BhY2luZzogMC41cHg7CiAgcGFkZGluZzogMXB4IDVweDsKICBib3JkZXItcmFkaXVzOiA0cHg7CiAgYmFja2dyb3VuZDogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjA4KTsKICBjb2xvcjogI2ZmZmZmZjsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDI1NSwgMjU1LCAyNTUsIDAuMTUpOwogIGZsZXgtc2hyaW5rOiAwOwp9Ci5wbGF0Zm9ybS1waWxsLnBpbGwtd2luIHsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDEyMiwgMjU1LCAwLjE4KTsKICBjb2xvcjogIzYwYTVmYTsKICBib3JkZXItY29sb3I6IHJnYmEoMCwgMTIyLCAyNTUsIDAuMzUpOwp9Ci5wbGF0Zm9ybS1waWxsLnBpbGwtZHJvaWQgewogIGJhY2tncm91bmQ6IHJnYmEoMCwgMjMwLCAxMTgsIDAuMTgpOwogIGNvbG9yOiAjNGFkZTgwOwogIGJvcmRlci1jb2xvcjogcmdiYSgwLCAyMzAsIDExOCwgMC4zNSk7Cn0KLnBsYXRmb3JtLXBpbGwucGlsbC1hcHBsZSB7CiAgYmFja2dyb3VuZDogcmdiYSgyNTUsIDI1NSwgMjU1LCAwLjE4KTsKICBjb2xvcjogI2ZmZmZmZjsKICBib3JkZXItY29sb3I6IHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4zNSk7Cn0KLnBsYXRmb3JtLXBpbGwucGlsbC1saW51eCB7CiAgYmFja2dyb3VuZDogcmdiYSgyNTUsIDE3MCwgMCwgMC4xOCk7CiAgY29sb3I6ICNmYmJmMjQ7CiAgYm9yZGVyLWNvbG9yOiByZ2JhKDI1NSwgMTcwLCAwLCAwLjM1KTsKfQoKLm5lYXJieS1tZXRhLXJvdyB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogNnB4OwogIGZvbnQtc2l6ZTogMTBweDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwogIGZvbnQtZmFtaWx5OiB2YXIoLS1mb250LW1vbm8pOwogIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgb3ZlcmZsb3c6IGhpZGRlbjsKICB0ZXh0LW92ZXJmbG93OiBlbGxpcHNpczsKfQoKLm5lYXJieS1pZCB7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKfQoKLm5lYXJieS1tZXRhIHsKICBjb2xvcjogdmFyKC0tYWNjZW50LWVtZXJhbGQpOwp9CgoucGVlci1hY3Rpb24tYnRuIHsKICBiYWNrZ3JvdW5kOiAjZmZmZmZmOwogIGNvbG9yOiAjMDAwMDAwOwogIGJvcmRlcjogbm9uZTsKICBwYWRkaW5nOiA0cHggMTBweDsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgZm9udC1zaXplOiAxMXB4OwogIGZvbnQtd2VpZ2h0OiA3MDA7CiAgY3Vyc29yOiBwb2ludGVyOwogIHdoaXRlLXNwYWNlOiBub3dyYXA7CiAgZmxleC1zaHJpbms6IDA7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5wZWVyLWFjdGlvbi1idG46aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKICBjb2xvcjogIzAwMDAwMDsKfQoKLyogU0VBUkNIICovCi5zZWFyY2gtd3JhcCB7CiAgcGFkZGluZzogMTBweCAxNnB4Owp9Cgouc2VhcmNoLWlucHV0LWJveCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogOHB4OwogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBwYWRkaW5nOiA2cHggMTJweDsKfQoKLnNlYXJjaC1pY29uIHsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7CiAgZmxleC1zaHJpbms6IDA7Cn0KCi5zZWFyY2gtaW5wdXQtYm94IGlucHV0IHsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgb3V0bGluZTogbm9uZTsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICBmb250LXNpemU6IDEzcHg7CiAgd2lkdGg6IDEwMCU7Cn0KCi5zZWFyY2gtaW5wdXQtYm94IGlucHV0OjpwbGFjZWhvbGRlciB7CiAgY29sb3I6IHZhcigtLXRleHQtbXV0ZWQpOwp9CgovKiBDSEFUIExJU1QgKi8KLmNoYXQtbGlzdCB7CiAgZmxleDogMTsKICBvdmVyZmxvdy15OiBhdXRvOwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKfQoKLmNoYXQtaXRlbSB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTJweDsKICBwYWRkaW5nOiAxMnB4IDE2cHg7CiAgYm9yZGVyLWJvdHRvbTogMXB4IHNvbGlkIHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4wMyk7CiAgY3Vyc29yOiBwb2ludGVyOwogIHRyYW5zaXRpb246IGJhY2tncm91bmQgMC4xNXMgZWFzZTsKICBwb3NpdGlvbjogcmVsYXRpdmU7Cn0KCi5jaGF0LWl0ZW06aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWhvdmVyKTsKfQoKLmNoYXQtaXRlbS5hY3RpdmUgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKfQoKLmNoYXQtaXRlbS5hY3RpdmU6OmJlZm9yZSB7CiAgY29udGVudDogJyc7CiAgcG9zaXRpb246IGFic29sdXRlOwogIGxlZnQ6IDA7CiAgdG9wOiAwOwogIGJvdHRvbTogMDsKICB3aWR0aDogM3B4OwogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKfQoKLmF2YXRhciB7CiAgd2lkdGg6IDQ0cHg7CiAgaGVpZ2h0OiA0NHB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiAjMWEyMjMzOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtc2l6ZTogMTVweDsKICBmb250LXdlaWdodDogNzAwOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBmbGV4LXNocmluazogMDsKfQoKLmNoYXQtaXRlbS1tZXRhIHsKICBmbGV4OiAxOwogIG1pbi13aWR0aDogMDsKfQoKLmNoYXQtaXRlbS10b3AgewogIGRpc3BsYXk6IGZsZXg7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwogIGFsaWduLWl0ZW1zOiBiYXNlbGluZTsKICBtYXJnaW4tYm90dG9tOiAzcHg7Cn0KCi5jaGF0LWl0ZW0tbmFtZSB7CiAgZm9udC1zaXplOiAxNHB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwp9CgouY2hhdC1pdGVtLXRpbWUgewogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7Cn0KCi5jaGF0LWl0ZW0tcHJldmlldyB7CiAgZGlzcGxheTogZmxleDsKICBqdXN0aWZ5LWNvbnRlbnQ6IHNwYWNlLWJldHdlZW47CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKfQoKLnByZXZpZXctdGV4dCB7CiAgZm9udC1zaXplOiAxMnB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7CiAgd2hpdGUtc3BhY2U6IG5vd3JhcDsKICBvdmVyZmxvdzogaGlkZGVuOwogIHRleHQtb3ZlcmZsb3c6IGVsbGlwc2lzOwogIGZsZXg6IDE7Cn0KCi51bnJlYWQtYmFkZ2UgewogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKICBjb2xvcjogIzA1MDcwYTsKICBwYWRkaW5nOiAxcHggNnB4OwogIGJvcmRlci1yYWRpdXM6IDEwcHg7CiAgZm9udC1zaXplOiAxMXB4OwogIGZvbnQtd2VpZ2h0OiA4MDA7CiAgbWFyZ2luLWxlZnQ6IDhweDsKfQoKLyogU0lERUJBUiBGT09URVIgKi8KLnNpZGViYXItZm9vdGVyIHsKICBwYWRkaW5nOiAxMnB4IDE2cHg7CiAgYm9yZGVyLXRvcDogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctc3VyZmFjZSk7Cn0KCi5kYW5nZXItYnVybi1idG4gewogIHdpZHRoOiAxMDAlOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBnYXA6IDhweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDI1NSwgNTksIDQ4LCAwLjA4KTsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDI1NSwgNTksIDQ4LCAwLjIpOwogIGNvbG9yOiAjZmY2YjZiOwogIHBhZGRpbmc6IDhweDsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY3Vyc29yOiBwb2ludGVyOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouZGFuZ2VyLWJ1cm4tYnRuOmhvdmVyIHsKICBiYWNrZ3JvdW5kOiByZ2JhKDI1NSwgNTksIDQ4LCAwLjE2KTsKICBib3JkZXItY29sb3I6IHZhcigtLWFjY2VudC1kYW5nZXIpOwp9CgovKiBDSEFUIE1BSU4gVklFVyAqLwouY2hhdC1tYWluIHsKICBmbGV4OiAxOwogIGhlaWdodDogMTAwJTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1kZWVwKTsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgcG9zaXRpb246IHJlbGF0aXZlOwogIG92ZXJmbG93OiBoaWRkZW47Cn0KCi5jaGF0LWhlYWRlciB7CiAgaGVpZ2h0OiB2YXIoLS1oZWFkZXItaGVpZ2h0KTsKICBwYWRkaW5nOiAwIDE2cHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctc3VyZmFjZSk7CiAgYm9yZGVyLWJvdHRvbTogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogc3BhY2UtYmV0d2VlbjsKICBmbGV4LXNocmluazogMDsKICB6LWluZGV4OiA1Owp9CgouaGVhZGVyLWxlZnQtY29sIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiAxMnB4OwogIG1pbi13aWR0aDogMDsKfQoKLmJhY2stbmF2LWJ0biB7CiAgZGlzcGxheTogbm9uZTsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgd2lkdGg6IDM2cHg7CiAgaGVpZ2h0OiAzNnB4OwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICB0cmFuc2l0aW9uOiBiYWNrZ3JvdW5kIDAuMTVzIGVhc2U7Cn0KCi5iYWNrLW5hdi1idG46aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWhvdmVyKTsKfQoKLmFjdGl2ZS1hdmF0YXIgewogIHdpZHRoOiAzOHB4OwogIGhlaWdodDogMzhweDsKICBib3JkZXItcmFkaXVzOiA1MCU7CiAgYmFja2dyb3VuZDogIzFhMjIzMzsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICBmb250LXNpemU6IDE0cHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgZmxleC1zaHJpbms6IDA7Cn0KCi5hY3RpdmUtbWV0YSB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIG1pbi13aWR0aDogMDsKfQoKLmFjdGl2ZS10aXRsZS1yb3cgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDZweDsKfQoKLmFjdGl2ZS1jaGF0LXRpdGxlIHsKICBmb250LXNpemU6IDE0cHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5hY3RpdmUtbG9jay1iYWRnZSB7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7Cn0KCi5hY3RpdmUtY2hhdC1zdGF0dXMgewogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBnYXA6IDZweDsKICBmb250LXNpemU6IDExcHg7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5zdGF0dXMtaW5kaWNhdG9yLWRvdCB7CiAgd2lkdGg6IDZweDsKICBoZWlnaHQ6IDZweDsKICBib3JkZXItcmFkaXVzOiA1MCU7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWVtZXJhbGQpOwp9CgouaGVhZGVyLXJpZ2h0LWFjdGlvbnMgewogIGRpc3BsYXk6IGZsZXg7CiAgZ2FwOiA4cHg7Cn0KCi5jaGF0LXRvb2wtYnRuIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiA2cHg7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICBwYWRkaW5nOiA2cHggMTBweDsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY3Vyc29yOiBwb2ludGVyOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouY2hhdC10b29sLWJ0bjpob3ZlciB7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctaG92ZXIpOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwp9CgovKiBNRVNTQUdFUyBWSUVXUE9SVCAqLwoubWVzc2FnZXMtdmlld3BvcnQgewogIGZsZXg6IDE7CiAgb3ZlcmZsb3cteTogYXV0bzsKICBwYWRkaW5nOiAxNnB4OwogIGRpc3BsYXk6IGZsZXg7CiAgZmxleC1kaXJlY3Rpb246IGNvbHVtbjsKICBnYXA6IDEwcHg7Cn0KCi5kYXRlLWJhZGdlLXdyYXAgewogIGRpc3BsYXk6IGZsZXg7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgbWFyZ2luOiA2cHggMDsKfQoKLmRhdGUtYmFkZ2UgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKICBmb250LXNpemU6IDEwcHg7CiAgZm9udC13ZWlnaHQ6IDcwMDsKICBsZXR0ZXItc3BhY2luZzogMC44cHg7CiAgcGFkZGluZzogM3B4IDEwcHg7CiAgYm9yZGVyLXJhZGl1czogMTJweDsKfQoKLmVuY3J5cHRpb24tYmFubmVyIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgZ2FwOiA4cHg7CiAgcGFkZGluZzogOHB4IDE0cHg7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAyNDAsIDI1NSwgMC4wNCk7CiAgYm9yZGVyOiAxcHggc29saWQgcmdiYSgwLCAyNDAsIDI1NSwgMC4xNSk7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGZvbnQtc2l6ZTogMTFweDsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIG1hcmdpbi1ib3R0b206IDZweDsKfQoKLyogTUVTU0FHRSBCVUJCTEVTIChUZWxlZ3JhbS1ncmFkZSkgKi8KLm1lc3NhZ2Utcm93IHsKICBkaXNwbGF5OiBmbGV4OwogIHdpZHRoOiAxMDAlOwp9CgoubWVzc2FnZS1yb3cuaW5jb21pbmcgewogIGp1c3RpZnktY29udGVudDogZmxleC1zdGFydDsKfQoKLm1lc3NhZ2Utcm93Lm91dGdvaW5nIHsKICBqdXN0aWZ5LWNvbnRlbnQ6IGZsZXgtZW5kOwp9CgouYnViYmxlIHsKICBtYXgtd2lkdGg6IDc1JTsKICBtaW4td2lkdGg6IDgwcHg7CiAgcGFkZGluZzogOHB4IDEycHg7CiAgYm9yZGVyLXJhZGl1czogMTRweDsKICBwb3NpdGlvbjogcmVsYXRpdmU7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogNHB4OwogIHdvcmQtYnJlYWs6IGJyZWFrLXdvcmQ7Cn0KCi5tZXNzYWdlLXJvdy5pbmNvbWluZyAuYnViYmxlIHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1tc2ctaW4tYmcpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLW1zZy1pbi1ib3JkZXIpOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGJvcmRlci1ib3R0b20tbGVmdC1yYWRpdXM6IDRweDsKfQoKLm1lc3NhZ2Utcm93Lm91dGdvaW5nIC5idWJibGUgewogIGJhY2tncm91bmQ6IHZhcigtLW1zZy1vdXQtYmcpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLW1zZy1vdXQtYm9yZGVyKTsKICBjb2xvcjogI2ZmZmZmZjsKICBib3JkZXItYm90dG9tLXJpZ2h0LXJhZGl1czogNHB4Owp9CgouYnViYmxlLWF1dGhvciB7CiAgZm9udC1zaXplOiAxMXB4OwogIGZvbnQtd2VpZ2h0OiA3MDA7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBtYXJnaW4tYm90dG9tOiAycHg7Cn0KCi5idWJibGUtdGV4dCB7CiAgZm9udC1zaXplOiAxMy41cHg7CiAgbGluZS1oZWlnaHQ6IDEuNDU7Cn0KCi5idWJibGUtZm9vdGVyIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBmbGV4LWVuZDsKICBnYXA6IDRweDsKICBtYXJnaW4tdG9wOiAycHg7CiAgYWxpZ24tc2VsZjogZmxleC1lbmQ7Cn0KCi5idWJibGUtdGltZSB7CiAgZm9udC1zaXplOiAxMHB4OwogIGNvbG9yOiB2YXIoLS10ZXh0LW11dGVkKTsKfQoKLm1lc3NhZ2Utcm93Lm91dGdvaW5nIC5idWJibGUtdGltZSB7CiAgY29sb3I6IHJnYmEoMjU1LCAyNTUsIDI1NSwgMC42KTsKfQoKLnRpY2staWNvbiB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGNvbG9yOiB2YXIoLS1hY2NlbnQtY3lhbik7Cn0KCi8qIEZJTEUgQVRUQUNITUVOVCBDQVJEIElOIEJVQkJMRSAqLwouYnViYmxlLWZpbGUtY2FyZCB7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGdhcDogMTBweDsKICBwYWRkaW5nOiA4cHggMTBweDsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDAsIDAsIDAuMjUpOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCAyNTUsIDI1NSwgMC4wOCk7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIG1hcmdpbi10b3A6IDRweDsKfQoKLmZpbGUtaWNvbi1ib3ggewogIHdpZHRoOiAzNnB4OwogIGhlaWdodDogMzZweDsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAyNDAsIDI1NSwgMC4xKTsKICBjb2xvcjogdmFyKC0tYWNjZW50LWN5YW4pOwogIGRpc3BsYXk6IGZsZXg7CiAgYWxpZ24taXRlbXM6IGNlbnRlcjsKICBqdXN0aWZ5LWNvbnRlbnQ6IGNlbnRlcjsKICBmbGV4LXNocmluazogMDsKfQoKLmZpbGUtY2FyZC1kZXRhaWxzIHsKICBmbGV4OiAxOwogIG1pbi13aWR0aDogMDsKfQoKLmZpbGUtY2FyZC1uYW1lIHsKICBmb250LXNpemU6IDEycHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogI2ZmZmZmZjsKICB3aGl0ZS1zcGFjZTogbm93cmFwOwogIG92ZXJmbG93OiBoaWRkZW47CiAgdGV4dC1vdmVyZmxvdzogZWxsaXBzaXM7Cn0KCi5maWxlLWNhcmQtc2l6ZSB7CiAgZm9udC1zaXplOiAxMC41cHg7CiAgY29sb3I6IHZhcigtLXRleHQtbXV0ZWQpOwp9CgouZmlsZS1kb3dubG9hZC1saW5rIHsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDI0MCwgMjU1LCAwLjE1KTsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDAsIDI0MCwgMjU1LCAwLjMpOwogIGNvbG9yOiB2YXIoLS1hY2NlbnQtY3lhbik7CiAgcGFkZGluZzogNXB4IDhweDsKICBib3JkZXItcmFkaXVzOiA2cHg7CiAgZGlzcGxheTogZmxleDsKICBhbGlnbi1pdGVtczogY2VudGVyOwogIGp1c3RpZnktY29udGVudDogY2VudGVyOwogIHRleHQtZGVjb3JhdGlvbjogbm9uZTsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5maWxlLWRvd25sb2FkLWxpbms6aG92ZXIgewogIGJhY2tncm91bmQ6IHZhcigtLWFjY2VudC1jeWFuKTsKICBjb2xvcjogIzA1MDcwYTsKfQoKLmJ1YmJsZS1pbWFnZS1wcmV2aWV3IHsKICBtYXgtd2lkdGg6IDEwMCU7CiAgbWF4LWhlaWdodDogMjgwcHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIG9iamVjdC1maXQ6IGNvdmVyOwogIG1hcmdpbi10b3A6IDRweDsKICBib3JkZXI6IDFweCBzb2xpZCByZ2JhKDI1NSwgMjU1LCAyNTUsIDAuMSk7Cn0KCi8qIENIQVQgQ09NUE9TRVIgKi8KLmNoYXQtY29tcG9zZXIgewogIHBhZGRpbmc6IDEwcHggMTZweDsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1zdXJmYWNlKTsKICBib3JkZXItdG9wOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBwYWRkaW5nLWJvdHRvbTogbWF4KDEwcHgsIGVudihzYWZlLWFyZWEtaW5zZXQtYm90dG9tKSk7Cn0KCi5oaWRkZW4tZmlsZS1pbnB1dCB7CiAgZGlzcGxheTogbm9uZSAhaW1wb3J0YW50Owp9CgouY29tcG9zZXItYmFyIHsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBmbGV4LWVuZDsKICBnYXA6IDhweDsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1lbGV2YXRlZCk7CiAgYm9yZGVyOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBib3JkZXItcmFkaXVzOiAyMHB4OwogIHBhZGRpbmc6IDZweCAxMHB4OwogIHRyYW5zaXRpb246IGJvcmRlci1jb2xvciAwLjE1cyBlYXNlOwp9CgouY29tcG9zZXItYmFyOmZvY3VzLXdpdGhpbiB7CiAgYm9yZGVyLWNvbG9yOiB2YXIoLS1ib3JkZXItZm9jdXMpOwp9CgouY29tcG9zZXItYWN0aW9uLWJ0biB7CiAgYmFja2dyb3VuZDogdHJhbnNwYXJlbnQ7CiAgYm9yZGVyOiBub25lOwogIGNvbG9yOiB2YXIoLS10ZXh0LXNlY29uZGFyeSk7CiAgd2lkdGg6IDMycHg7CiAgaGVpZ2h0OiAzMnB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgY3Vyc29yOiBwb2ludGVyOwogIGZsZXgtc2hyaW5rOiAwOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouY29tcG9zZXItYWN0aW9uLWJ0bjpob3ZlciB7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICBiYWNrZ3JvdW5kOiByZ2JhKDAsIDI0MCwgMjU1LCAwLjEpOwp9CgouY29tcG9zZXItYmFyIHRleHRhcmVhIHsKICBmbGV4OiAxOwogIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogIGJvcmRlcjogbm9uZTsKICBvdXRsaW5lOiBub25lOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwogIGZvbnQtZmFtaWx5OiB2YXIoLS1mb250LXNhbnMpOwogIGZvbnQtc2l6ZTogMTMuNXB4OwogIGxpbmUtaGVpZ2h0OiAxLjQ7CiAgcmVzaXplOiBub25lOwogIG1heC1oZWlnaHQ6IDEyMHB4OwogIHBhZGRpbmc6IDZweCA0cHg7Cn0KCi5jb21wb3Nlci1iYXIgdGV4dGFyZWE6OnBsYWNlaG9sZGVyIHsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7Cn0KCi5jb21wb3Nlci1zZW5kLWJ0biB7CiAgYmFja2dyb3VuZDogdmFyKC0tYWNjZW50LWF6dXJlKTsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6ICNmZmZmZmY7CiAgd2lkdGg6IDMycHg7CiAgaGVpZ2h0OiAzMnB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgY3Vyc29yOiBwb2ludGVyOwogIGZsZXgtc2hyaW5rOiAwOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouY29tcG9zZXItc2VuZC1idG46aG92ZXIgewogIGJhY2tncm91bmQ6ICMwMDY2ZDY7CiAgdHJhbnNmb3JtOiBzY2FsZSgxLjA0KTsKfQoKLmNvbXBvc2VyLXNlbmQtYnRuOmFjdGl2ZSB7CiAgdHJhbnNmb3JtOiBzY2FsZSgwLjk2KTsKfQoKLyogTU9EQUxTICovCi5tb2RhbC1vdmVybGF5IHsKICBwb3NpdGlvbjogZml4ZWQ7CiAgaW5zZXQ6IDA7CiAgYmFja2dyb3VuZDogcmdiYSgwLCAwLCAwLCAwLjc1KTsKICBiYWNrZHJvcC1maWx0ZXI6IGJsdXIoOHB4KTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7CiAgei1pbmRleDogMTAwOwogIHBhZGRpbmc6IDE2cHg7Cn0KCi5tb2RhbC1vdmVybGF5LmhpZGRlbiB7CiAgZGlzcGxheTogbm9uZSAhaW1wb3J0YW50Owp9CgoubW9kYWwtY2FyZCB7CiAgd2lkdGg6IDEwMCU7CiAgbWF4LXdpZHRoOiA0NDBweDsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1zdXJmYWNlKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDE0cHg7CiAgYm94LXNoYWRvdzogMCAyMHB4IDQwcHggcmdiYSgwLCAwLCAwLCAwLjYpOwogIG92ZXJmbG93OiBoaWRkZW47CiAgYW5pbWF0aW9uOiBtb2RhbC1hcHBlYXIgMC4ycyBjdWJpYy1iZXppZXIoMC4xNiwgMSwgMC4zLCAxKTsKfQoKQGtleWZyYW1lcyBtb2RhbC1hcHBlYXIgewogIGZyb20geyBvcGFjaXR5OiAwOyB0cmFuc2Zvcm06IHNjYWxlKDAuOTUpOyB9CiAgdG8geyBvcGFjaXR5OiAxOyB0cmFuc2Zvcm06IHNjYWxlKDEpOyB9Cn0KCi5tb2RhbC1oZWFkZXIgewogIHBhZGRpbmc6IDE0cHggMThweDsKICBib3JkZXItYm90dG9tOiAxcHggc29saWQgdmFyKC0tYm9yZGVyKTsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBzcGFjZS1iZXR3ZWVuOwp9CgoubW9kYWwtdGl0bGUgewogIGZvbnQtc2l6ZTogMTVweDsKICBmb250LXdlaWdodDogNzAwOwogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwp9CgoubW9kYWwtY2xvc2UtYnRuIHsKICBiYWNrZ3JvdW5kOiB0cmFuc3BhcmVudDsKICBib3JkZXI6IG5vbmU7CiAgY29sb3I6IHZhcigtLXRleHQtbXV0ZWQpOwogIGN1cnNvcjogcG9pbnRlcjsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAganVzdGlmeS1jb250ZW50OiBjZW50ZXI7Cn0KCi5tb2RhbC1jbG9zZS1idG46aG92ZXIgewogIGNvbG9yOiB2YXIoLS10ZXh0LXByaW1hcnkpOwp9CgoubW9kYWwtYm9keSB7CiAgcGFkZGluZzogMThweDsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZ2FwOiAxNHB4Owp9CgoubW9kYWwtZGVzY3JpcHRpb24gewogIGZvbnQtc2l6ZTogMTNweDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwp9CgouZm9ybS1ncm91cCB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogNnB4Owp9CgouZm9ybS1sYWJlbCB7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKfQoKLmZvcm0taW5wdXQgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBwYWRkaW5nOiA5cHggMTJweDsKICBjb2xvcjogdmFyKC0tdGV4dC1wcmltYXJ5KTsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtc2Fucyk7CiAgb3V0bGluZTogbm9uZTsKfQoKLmZvcm0taW5wdXQ6Zm9jdXMgewogIGJvcmRlci1jb2xvcjogdmFyKC0tYm9yZGVyLWZvY3VzKTsKfQoKLmNvZGUtYm94LWRpc3BsYXkgewogIGJhY2tncm91bmQ6IHZhcigtLWJnLWVsZXZhdGVkKTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBwYWRkaW5nOiA5cHggMTJweDsKICBmb250LWZhbWlseTogdmFyKC0tZm9udC1tb25vKTsKICBmb250LXNpemU6IDEycHg7CiAgY29sb3I6IHZhcigtLWFjY2VudC1jeWFuKTsKICB3b3JkLWJyZWFrOiBicmVhay1hbGw7Cn0KCi5maW5nZXJwcmludC1kaXNwbGF5IHsKICBiYWNrZ3JvdW5kOiAjMDAwMDAwOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMCwgMjQwLCAyNTUsIDAuMik7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIHBhZGRpbmc6IDE0cHg7CiAgZm9udC1mYW1pbHk6IHZhcigtLWZvbnQtbW9ubyk7CiAgZm9udC1zaXplOiAxM3B4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgbGV0dGVyLXNwYWNpbmc6IDEuNXB4OwogIGNvbG9yOiB2YXIoLS1hY2NlbnQtY3lhbik7CiAgdGV4dC1hbGlnbjogY2VudGVyOwogIGxpbmUtaGVpZ2h0OiAxLjg7Cn0KCi5jcnlwdG8tbWV0YS1saXN0IHsKICBkaXNwbGF5OiBmbGV4OwogIGZsZXgtZGlyZWN0aW9uOiBjb2x1bW47CiAgZ2FwOiA2cHg7Cn0KCi5jcnlwdG8tbWV0YS1pdGVtIHsKICBkaXNwbGF5OiBmbGV4OwogIGp1c3RpZnktY29udGVudDogc3BhY2UtYmV0d2VlbjsKICBmb250LXNpemU6IDEycHg7Cn0KCi5tZXRhLWxhYmVsIHsKICBjb2xvcjogdmFyKC0tdGV4dC1tdXRlZCk7Cn0KCi5tZXRhLXZhbCB7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjb2xvcjogdmFyKC0tdGV4dC1zZWNvbmRhcnkpOwp9CgoubW9kYWwtZm9vdGVyIHsKICBwYWRkaW5nOiAxMnB4IDE4cHg7CiAgYm9yZGVyLXRvcDogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgZGlzcGxheTogZmxleDsKICBqdXN0aWZ5LWNvbnRlbnQ6IGZsZXgtZW5kOwogIGdhcDogMTBweDsKfQoKLmJ0bi1wcmltYXJ5IHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtYXp1cmUpOwogIGNvbG9yOiAjZmZmZmZmOwogIGJvcmRlcjogbm9uZTsKICBwYWRkaW5nOiA4cHggMTZweDsKICBib3JkZXItcmFkaXVzOiA4cHg7CiAgZm9udC1zaXplOiAxM3B4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY3Vyc29yOiBwb2ludGVyOwogIHRyYW5zaXRpb246IGFsbCAwLjE1cyBlYXNlOwp9CgouYnRuLXByaW1hcnk6aG92ZXIgewogIGJhY2tncm91bmQ6ICMwMDY2ZDY7Cn0KCi5idG4tc2Vjb25kYXJ5IHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1lbGV2YXRlZCk7CiAgY29sb3I6IHZhcigtLXRleHQtc2Vjb25kYXJ5KTsKICBib3JkZXI6IDFweCBzb2xpZCB2YXIoLS1ib3JkZXIpOwogIHBhZGRpbmc6IDhweCAxNHB4OwogIGJvcmRlci1yYWRpdXM6IDhweDsKICBmb250LXNpemU6IDEzcHg7CiAgZm9udC13ZWlnaHQ6IDYwMDsKICBjdXJzb3I6IHBvaW50ZXI7CiAgdHJhbnNpdGlvbjogYWxsIDAuMTVzIGVhc2U7Cn0KCi5idG4tc2Vjb25kYXJ5OmhvdmVyIHsKICBiYWNrZ3JvdW5kOiB2YXIoLS1iZy1ob3Zlcik7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7Cn0KCi5idG4tb3V0bGluZS1kYW5nZXIgewogIGJhY2tncm91bmQ6IHRyYW5zcGFyZW50OwogIGNvbG9yOiAjZmY2YjZiOwogIGJvcmRlcjogMXB4IHNvbGlkIHJnYmEoMjU1LCA1OSwgNDgsIDAuMyk7CiAgcGFkZGluZzogOHB4IDEycHg7CiAgYm9yZGVyLXJhZGl1czogOHB4OwogIGZvbnQtc2l6ZTogMTJweDsKICBmb250LXdlaWdodDogNjAwOwogIGN1cnNvcjogcG9pbnRlcjsKICB0cmFuc2l0aW9uOiBhbGwgMC4xNXMgZWFzZTsKfQoKLmJ0bi1vdXRsaW5lLWRhbmdlcjpob3ZlciB7CiAgYmFja2dyb3VuZDogcmdiYSgyNTUsIDU5LCA0OCwgMC4xKTsKICBib3JkZXItY29sb3I6IHZhcigtLWFjY2VudC1kYW5nZXIpOwp9CgouZGlzY292ZXJlZC1wZWVycy1xdWljayB7CiAgZGlzcGxheTogZmxleDsKICBmbGV4LWRpcmVjdGlvbjogY29sdW1uOwogIGdhcDogNnB4OwogIG1heC1oZWlnaHQ6IDEyMHB4OwogIG92ZXJmbG93LXk6IGF1dG87Cn0KCi8qIFRPQVNUIE5PVElGSUNBVElPTiAqLwoudG9hc3Qtbm90aWZpY2F0aW9uIHsKICBwb3NpdGlvbjogZml4ZWQ7CiAgYm90dG9tOiAyNHB4OwogIGxlZnQ6IDUwJTsKICB0cmFuc2Zvcm06IHRyYW5zbGF0ZVgoLTUwJSk7CiAgYmFja2dyb3VuZDogdmFyKC0tYmctZWxldmF0ZWQpOwogIGJvcmRlcjogMXB4IHNvbGlkIHZhcigtLWJvcmRlcik7CiAgYm94LXNoYWRvdzogMCA4cHggMjRweCByZ2JhKDAsIDAsIDAsIDAuNik7CiAgYm9yZGVyLXJhZGl1czogMjBweDsKICBwYWRkaW5nOiA4cHggMTZweDsKICBkaXNwbGF5OiBmbGV4OwogIGFsaWduLWl0ZW1zOiBjZW50ZXI7CiAgZ2FwOiA4cHg7CiAgZm9udC1zaXplOiAxMnB4OwogIGZvbnQtd2VpZ2h0OiA2MDA7CiAgY29sb3I6IHZhcigtLXRleHQtcHJpbWFyeSk7CiAgei1pbmRleDogMjAwOwogIHBvaW50ZXItZXZlbnRzOiBub25lOwogIGFuaW1hdGlvbjogdG9hc3QtZmFkZSAwLjJzIGN1YmljLWJlemllcigwLjE2LCAxLCAwLjMsIDEpOwp9CgoudG9hc3Qtbm90aWZpY2F0aW9uLmhpZGRlbiB7CiAgZGlzcGxheTogbm9uZSAhaW1wb3J0YW50Owp9CgoudG9hc3QtZG90IHsKICB3aWR0aDogNnB4OwogIGhlaWdodDogNnB4OwogIGJvcmRlci1yYWRpdXM6IDUwJTsKICBiYWNrZ3JvdW5kOiB2YXIoLS1hY2NlbnQtY3lhbik7Cn0KCkBrZXlmcmFtZXMgdG9hc3QtZmFkZSB7CiAgZnJvbSB7IG9wYWNpdHk6IDA7IHRyYW5zZm9ybTogdHJhbnNsYXRlKC01MCUsIDEwcHgpOyB9CiAgdG8geyBvcGFjaXR5OiAxOyB0cmFuc2Zvcm06IHRyYW5zbGF0ZSgtNTAlLCAwKTsgfQp9CgovKiBNT0JJTEUgQURBUFRBVElPTiAoPCA3NjhweCkgKi8KQG1lZGlhIChtYXgtd2lkdGg6IDc2OHB4KSB7CiAgLnNpZGViYXIgewogICAgd2lkdGg6IDEwMHZ3OwogICAgcG9zaXRpb246IGFic29sdXRlOwogICAgaW5zZXQ6IDA7CiAgICB6LWluZGV4OiAyMDsKICB9CgogIC5jaGF0LW1haW4gewogICAgd2lkdGg6IDEwMHZ3OwogICAgcG9zaXRpb246IGFic29sdXRlOwogICAgaW5zZXQ6IDA7CiAgICB6LWluZGV4OiAxMDsKICB9CgogIC5iYWNrLW5hdi1idG4gewogICAgZGlzcGxheTogZmxleDsKICB9CgogIC5hcHAtbGF5b3V0Lm1vYmlsZS1jaGF0LW9wZW4gLnNpZGViYXIgewogICAgdHJhbnNmb3JtOiB0cmFuc2xhdGVYKC0xMDAlKTsKICB9CgogIC5hcHAtbGF5b3V0Lm1vYmlsZS1jaGF0LW9wZW4gLmNoYXQtbWFpbiB7CiAgICB6LWluZGV4OiAzMDsKICB9CgogIC50b29sLWJ0bi10ZXh0IHsKICAgIGRpc3BsYXk6IG5vbmU7CiAgfQp9Cg=="));
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
                    else _js = Encoding.UTF8.GetString(Convert.FromBase64String("Ly8gU3lwaGVyIENsaWVudCBDb3JlIOKAlCBPZmZsaW5lIEJMRSBNZXNoLCBEdWFsIFVEUCBSYWRpbyAmIEUyRUUgTWVzc2VuZ2VyCihmdW5jdGlvbiAoKSB7CiAgJ3VzZSBzdHJpY3QnOwoKICAvLyAxLiDQo9C90LjQutCw0LvRjNC90LDRjyDQuNC00LXQvdGC0LjRhNC40LrQsNGG0LjRjyDRg9C30LvQsAogIGZ1bmN0aW9uIGdldERldmljZVBsYXRmb3JtUHJlZml4KCkgewogICAgdmFyIHVhID0gbmF2aWdhdG9yLnVzZXJBZ2VudCB8fCAnJzsKICAgIGlmICh3aW5kb3cuQW5kcm9pZEJyaWRnZSkgcmV0dXJuICdzeXAtZHJvaWQtJzsKICAgIGlmICgvQW5kcm9pZC9pLnRlc3QodWEpKSByZXR1cm4gJ3N5cC1kcm9pZC0nOwogICAgaWYgKC9pUGhvbmV8aVBhZHxpUG9kL2kudGVzdCh1YSkpIHJldHVybiAnc3lwLWlvcy0nOwogICAgaWYgKC9NYWMvaS50ZXN0KHVhKSkgcmV0dXJuICdzeXAtbWFjLSc7CiAgICBpZiAoL0xpbnV4L2kudGVzdCh1YSkpIHJldHVybiAnc3lwLWxpbi0nOwogICAgcmV0dXJuICdzeXAtd2luLSc7CiAgfQoKICBmdW5jdGlvbiBnZW5lcmF0ZVNlY3VyZUhleChieXRlc0xlbmd0aCkgewogICAgdmFyIHJhbmQgPSBuZXcgVWludDhBcnJheShieXRlc0xlbmd0aCk7CiAgICBpZiAod2luZG93LmNyeXB0byAmJiB3aW5kb3cuY3J5cHRvLmdldFJhbmRvbVZhbHVlcykgewogICAgICB3aW5kb3cuY3J5cHRvLmdldFJhbmRvbVZhbHVlcyhyYW5kKTsKICAgIH0gZWxzZSB7CiAgICAgIGZvciAodmFyIGkgPSAwOyBpIDwgYnl0ZXNMZW5ndGg7IGkrKykgewogICAgICAgIHJhbmRbaV0gPSBNYXRoLmZsb29yKE1hdGgucmFuZG9tKCkgKiAyNTYpOwogICAgICB9CiAgICB9CiAgICB2YXIgaGV4ID0gJyc7CiAgICBmb3IgKHZhciBqID0gMDsgaiA8IGJ5dGVzTGVuZ3RoOyBqKyspIHsKICAgICAgdmFyIGIgPSByYW5kW2pdLnRvU3RyaW5nKDE2KTsKICAgICAgaGV4ICs9IGIubGVuZ3RoID09PSAxID8gJzAnICsgYiA6IGI7CiAgICB9CiAgICByZXR1cm4gaGV4OwogIH0KCiAgZnVuY3Rpb24gZ2V0QXZhdGFyQ29sb3Ioc3RyKSB7CiAgICB2YXIgY29sb3JzID0gWwogICAgICBbJyMwMDdhZmYnLCAnIzAwZjBmZiddLAogICAgICBbJyM3OTI4Y2EnLCAnI2ZmMDA4MCddLAogICAgICBbJyMwMGU2NzYnLCAnIzAwYjBmZiddLAogICAgICBbJyNmZjkxMDAnLCAnI2ZmNTI1MiddLAogICAgICBbJyMxZGU5YjYnLCAnIzFkYzRlOSddLAogICAgICBbJyM2NTFmZmYnLCAnI2Q1MDBmOSddLAogICAgXTsKICAgIHZhciBoYXNoID0gMDsKICAgIGZvciAodmFyIGkgPSAwOyBpIDwgc3RyLmxlbmd0aDsgaSsrKSB7CiAgICAgIGhhc2ggPSBzdHIuY2hhckNvZGVBdChpKSArICgoaGFzaCA8PCA1KSAtIGhhc2gpOwogICAgfQogICAgdmFyIGlkeCA9IE1hdGguYWJzKGhhc2gpICUgY29sb3JzLmxlbmd0aDsKICAgIHJldHVybiBjb2xvcnNbaWR4XTsKICB9CgogIGZ1bmN0aW9uIGdldE9yQ3JlYXRlSWRlbnRpdHkoKSB7CiAgICB2YXIgU1RPUkFHRV9LRVkgPSAnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnOwogICAgdmFyIHN0b3JlZCA9IGxvY2FsU3RvcmFnZS5nZXRJdGVtKFNUT1JBR0VfS0VZKTsKICAgIGlmIChzdG9yZWQpIHsKICAgICAgdHJ5IHsKICAgICAgICB2YXIgcGFyc2VkID0gSlNPTi5wYXJzZShzdG9yZWQpOwogICAgICAgIGlmIChwYXJzZWQubXlJZCAmJiBwYXJzZWQubXlGaW5nZXJwcmludCkgcmV0dXJuIHBhcnNlZDsKICAgICAgfSBjYXRjaCAoZSkge30KICAgIH0KCiAgICB2YXIgcHJlZml4ID0gZ2V0RGV2aWNlUGxhdGZvcm1QcmVmaXgoKTsKICAgIHZhciBoZXggPSBnZW5lcmF0ZVNlY3VyZUhleCgxNik7CiAgICB2YXIgbXlJZCA9IHByZWZpeCArIGhleC5zdWJzdHJpbmcoMCwgOCk7CgogICAgdmFyIGZwUGFydHMgPSBbXTsKICAgIGZvciAodmFyIGsgPSAwOyBrIDwgODsgaysrKSB7CiAgICAgIGZwUGFydHMucHVzaChoZXguc3Vic3RyaW5nKGsgKiA0LCBrICogNCArIDQpLnRvVXBwZXJDYXNlKCkpOwogICAgfQogICAgdmFyIG15RmluZ2VycHJpbnQgPSBmcFBhcnRzLmpvaW4oJyAnKTsKCiAgICB2YXIgZGlzcGxheU5hbWUgPSAn0KPQt9C10LsgJyArIG15SWQucmVwbGFjZSgnc3lwLScsICcnKS50b1VwcGVyQ2FzZSgpOwoKICAgIHZhciBpZGVudGl0eSA9IHsKICAgICAgbXlJZDogbXlJZCwKICAgICAgZGlzcGxheU5hbWU6IGRpc3BsYXlOYW1lLAogICAgICBteUZpbmdlcnByaW50OiBteUZpbmdlcnByaW50LAogICAgfTsKICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKFNUT1JBR0VfS0VZLCBKU09OLnN0cmluZ2lmeShpZGVudGl0eSkpOwogICAgcmV0dXJuIGlkZW50aXR5OwogIH0KCiAgLy8gMi4g0KXRgNCw0L3QuNC70LjRidC1INC00LjQsNC70L7Qs9C+0LIgKNCi0J7Qm9Cs0JrQniDQntCU0JjQnSDQutCw0L3QsNC7INC/0L4g0YPQvNC+0LvRh9Cw0L3QuNGOIOKAlCDQvtCx0YnQuNC5INGN0YTQuNGAKQogIGZ1bmN0aW9uIGxvYWRTdG9yZWRDaGF0cygpIHsKICAgIHZhciBTVE9SQUdFX0tFWSA9ICdzeXBoZXJfY2hhdHNfdjMnOwogICAgdmFyIHN0b3JlZCA9IGxvY2FsU3RvcmFnZS5nZXRJdGVtKFNUT1JBR0VfS0VZKTsKICAgIGlmIChzdG9yZWQpIHsKICAgICAgdHJ5IHsKICAgICAgICB2YXIgcGFyc2VkID0gSlNPTi5wYXJzZShzdG9yZWQpOwogICAgICAgIGlmIChBcnJheS5pc0FycmF5KHBhcnNlZCkgJiYgcGFyc2VkLmxlbmd0aCA+IDApIHJldHVybiBwYXJzZWQ7CiAgICAgIH0gY2F0Y2ggKGUpIHt9CiAgICB9CgogICAgcmV0dXJuIFsKICAgICAgewogICAgICAgIGlkOiAnbWVzaC1icm9hZGNhc3QnLAogICAgICAgIG5hbWU6ICfQntCx0YnQuNC5INGA0LDQtNC40L7RjdGE0LjRgCBNZXNoJywKICAgICAgICBhdmF0YXI6ICdNJywKICAgICAgICBzdGF0dXM6ICfQqNC40YDQvtC60L7QstC10YnQsNGC0LXQu9GM0L3Ri9C5INC60LDQvdCw0LsgKFVEUCAmIEJMRSknLAogICAgICAgIGxhc3RNZXNzYWdlOiAn0KDQsNC00LjQvtC60LDQvdCw0Lsg0LPQvtGC0L7QsiDQuiDQvtCx0LzQtdC90YMg0L/QsNC60LXRgtCw0LzQuCcsCiAgICAgICAgdGltZTogJ9Ct0YTQuNGAJywKICAgICAgICB1bnJlYWQ6IDAsCiAgICAgICAgbWVzc2FnZXM6IFsKICAgICAgICAgIHsKICAgICAgICAgICAgaWQ6ICdzeXNfc3RhcnQnLAogICAgICAgICAgICBzZW5kZXI6ICdzeXN0ZW0nLAogICAgICAgICAgICBhdXRob3JOYW1lOiAn0KHQmNCh0KLQldCc0JAnLAogICAgICAgICAgICB0eXBlOiAndGV4dCcsCiAgICAgICAgICAgIHRleHQ6ICfQkNCy0YLQvtC90L7QvNC90YvQuSDRgNCw0LTQuNC+0LrQsNC90LDQuyDQsNC60YLQuNCy0LXQvS4g0KHQvtC+0LHRidC10L3QuNGPINC/0LXRgNC10LTQsNGO0YLRgdGPINC90LDQv9GA0Y/QvNGD0Y4g0YfQtdGA0LXQtyBXaS1GaSBVRFAg0LggQmx1ZXRvb3RoIExvdyBFbmVyZ3kg0LHQtdC3INGB0LXRgNCy0LXRgNC+0LIg0Lgg0LjQvdGC0LXRgNC90LXRgtCwLicsCiAgICAgICAgICAgIHRpbWU6IGZvcm1hdFRpbWUobmV3IERhdGUoKSksCiAgICAgICAgICAgIGlzT3V0Z29pbmc6IGZhbHNlLAogICAgICAgICAgfSwKICAgICAgICBdLAogICAgICB9LAogICAgXTsKICB9CgogIGZ1bmN0aW9uIHNhdmVDaGF0cygpIHsKICAgIHRyeSB7CiAgICAgIGxvY2FsU3RvcmFnZS5zZXRJdGVtKCdzeXBoZXJfY2hhdHNfdjMnLCBKU09OLnN0cmluZ2lmeShzdGF0ZS5jaGF0cykpOwogICAgfSBjYXRjaCAoZSkge30KICB9CgogIHZhciBpZGVudGl0eSA9IGdldE9yQ3JlYXRlSWRlbnRpdHkoKTsKCiAgdmFyIHN0YXRlID0gewogICAgaWRlbnRpdHk6IGlkZW50aXR5LAogICAgYWN0aXZlQ2hhdElkOiAnbWVzaC1icm9hZGNhc3QnLAogICAgc2VsZkRlc3RydWN0SW5kZXg6IDEsCiAgICBzZWxmRGVzdHJ1Y3RPcHRpb25zOiBbJzHRhycsICcyNNGHJywgJzfQtCcsICfQktGL0LrQuyddLAogICAgc2VhcmNoUXVlcnk6ICcnLAogICAgbG9jYWxJcDogJzEyNy4wLjAuMScsCiAgICBrbm93blBlZXJJcHM6IG5ldyBTZXQoKSwKICAgIGRpc2NvdmVyZWRQZWVyczoge30sIC8vIHBlZXJJZCAtPiB7IGlkLCBuYW1lLCB0cmFuc3BvcnQsIHJzc2ksIGRpc3RhbmNlLCBpcCwgbWFjLCBsYXN0U2VlbiB9CiAgICBjaGF0czogbG9hZFN0b3JlZENoYXRzKCksCiAgfTsKCiAgdmFyIHNpbmdsZUNoZWNrU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJ0aWNrLWljb24iIHdpZHRoPSIxMyIgaGVpZ2h0PSIxMyIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9ImN1cnJlbnRDb2xvciIgc3Ryb2tlLXdpZHRoPSIyLjUiPjxwb2x5bGluZSBwb2ludHM9IjIwIDYgOSAxNyA0IDEyIi8+PC9zdmc+JzsKCiAgdmFyIGRvdWJsZUNoZWNrU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJ0aWNrLWljb24iIHdpZHRoPSIxNSIgaGVpZ2h0PSIxNSIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9InZhcigtLWFjY2VudC1jeWFuKSIgc3Ryb2tlLXdpZHRoPSIyLjUiPjxwYXRoIGQ9Ik0xOCA2TDcgMTdsLTUtNSIvPjxwYXRoIGQ9Ik0yMiAxMGwtNy41IDcuNUwxMyAxNiIvPjwvc3ZnPic7CgogIHZhciBkb2NJY29uU3ZnID0KICAgICc8c3ZnIGNsYXNzPSJpY29uLXN2ZyIgd2lkdGg9IjIwIiBoZWlnaHQ9IjIwIiB2aWV3Qm94PSIwIDAgMjQgMjQiIGZpbGw9Im5vbmUiIHN0cm9rZT0iY3VycmVudENvbG9yIiBzdHJva2Utd2lkdGg9IjIiPjxwYXRoIGQ9Ik0xNCAySDZhMiAyIDAgMCAwLTIgMnYxNmEyIDIgMCAwIDAgMiAyaDEyYTIgMiAwIDAgMCAyLTJWOHoiLz48cG9seWxpbmUgcG9pbnRzPSIxNCAyIDE0IDggMjAgOCIvPjwvc3ZnPic7CgogIHZhciBkb3dubG9hZEljb25TdmcgPQogICAgJzxzdmcgY2xhc3M9Imljb24tc3ZnIiB3aWR0aD0iMTMiIGhlaWdodD0iMTMiIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0ibm9uZSIgc3Ryb2tlPSJjdXJyZW50Q29sb3IiIHN0cm9rZS13aWR0aD0iMi41Ij48cGF0aCBkPSJNMjEgMTV2NGEyIDIgMCAwIDEtMiAySDVhMiAyIDAgMCAxLTItMnYtNCIvPjxwb2x5bGluZSBwb2ludHM9IjcgMTAgMTIgMTUgMTcgMTAiLz48bGluZSB4MT0iMTIiIHkxPSIxNSIgeDI9IjEyIiB5Mj0iMyIvPjwvc3ZnPic7CgogIHZhciB0b2FzdFRpbWVyID0gbnVsbDsKCiAgZnVuY3Rpb24gc2hvd1RvYXN0KG1lc3NhZ2UpIHsKICAgIHZhciB0b2FzdCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCd0b2FzdE5vdGlmaWNhdGlvbicpOwogICAgdmFyIHRvYXN0VGV4dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCd0b2FzdFRleHQnKTsKICAgIGlmICghdG9hc3QgfHwgIXRvYXN0VGV4dCkgcmV0dXJuOwogICAgdG9hc3RUZXh0LnRleHRDb250ZW50ID0gbWVzc2FnZTsKICAgIHRvYXN0LmNsYXNzTGlzdC5yZW1vdmUoJ2hpZGRlbicpOwogICAgaWYgKHRvYXN0VGltZXIpIGNsZWFyVGltZW91dCh0b2FzdFRpbWVyKTsKICAgIHRvYXN0VGltZXIgPSBzZXRUaW1lb3V0KGZ1bmN0aW9uICgpIHsKICAgICAgdG9hc3QuY2xhc3NMaXN0LmFkZCgnaGlkZGVuJyk7CiAgICB9LCAyODAwKTsKICB9CgogIGZ1bmN0aW9uIGZvcm1hdFRpbWUoZCkgewogICAgcmV0dXJuICgKICAgICAgU3RyaW5nKGQuZ2V0SG91cnMoKSkucGFkU3RhcnQoMiwgJzAnKSArICc6JyArIFN0cmluZyhkLmdldE1pbnV0ZXMoKSkucGFkU3RhcnQoMiwgJzAnKQogICAgKTsKICB9CgogIGZ1bmN0aW9uIGZvcm1hdEZpbGVTaXplKGJ5dGVzKSB7CiAgICBpZiAoIWJ5dGVzIHx8IGJ5dGVzID09PSAwKSByZXR1cm4gJzAgQic7CiAgICB2YXIgayA9IDEwMjQ7CiAgICB2YXIgc2l6ZXMgPSBbJ0InLCAnS0InLCAnTUInLCAnR0InXTsKICAgIHZhciBpID0gTWF0aC5mbG9vcihNYXRoLmxvZyhieXRlcykgLyBNYXRoLmxvZyhrKSk7CiAgICByZXR1cm4gcGFyc2VGbG9hdCgoYnl0ZXMgLyBNYXRoLnBvdyhrLCBpKSkudG9GaXhlZCgxKSkgKyAnICcgKyBzaXplc1tpXTsKICB9CgogIGZ1bmN0aW9uIGVzY2FwZUh0bWwoc3RyKSB7CiAgICBpZiAoIXN0cikgcmV0dXJuICcnOwogICAgcmV0dXJuIFN0cmluZyhzdHIpCiAgICAgIC5yZXBsYWNlKC8mL2csICcmYW1wOycpCiAgICAgIC5yZXBsYWNlKC88L2csICcmbHQ7JykKICAgICAgLnJlcGxhY2UoLz4vZywgJyZndDsnKQogICAgICAucmVwbGFjZSgvIi9nLCAnJnF1b3Q7JykKICAgICAgLnJlcGxhY2UoLycvZywgJyYjMzk7Jyk7CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0KDQldCd0JTQldCgINCY0J3QotCV0KDQpNCV0JnQodCQCiAgLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQogIGZ1bmN0aW9uIGluaXRVSSgpIHsKICAgIHZhciBpZEVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlTeXBoZXJJZCcpOwogICAgaWYgKGlkRWxlbSkgaWRFbGVtLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlJZDsKCiAgICB2YXIgbmFtZUVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlOb2RlRGlzcGxheU5hbWUnKTsKICAgIGlmIChuYW1lRWxlbSkgbmFtZUVsZW0udGV4dENvbnRlbnQgPSBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSB8fCAn0JzQvtC5INGD0LfQtdC7JzsKCiAgICB2YXIgYXZhdGFyRWxlbSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdteU5vZGVBdmF0YXInKTsKICAgIGlmIChhdmF0YXJFbGVtKSB7CiAgICAgIGF2YXRhckVsZW0udGV4dENvbnRlbnQgPSAoc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUgfHwgJ1UnKS5jaGFyQXQoMCkudG9VcHBlckNhc2UoKTsKICAgICAgdmFyIGdyYWQgPSBnZXRBdmF0YXJDb2xvcihzdGF0ZS5pZGVudGl0eS5teUlkKTsKICAgICAgYXZhdGFyRWxlbS5zdHlsZS5iYWNrZ3JvdW5kID0gJ2xpbmVhci1ncmFkaWVudCgxMzVkZWcsICcgKyBncmFkWzBdICsgJywgJyArIGdyYWRbMV0gKyAnKSc7CiAgICB9CgogICAgdmFyIHNldHRpbmdzSWQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnc2V0dGluZ3NOb2RlSWQnKTsKICAgIGlmIChzZXR0aW5nc0lkKSBzZXR0aW5nc0lkLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlJZDsKCiAgICB2YXIgc2V0dGluZ3NOYW1lID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2lucHV0RGlzcGxheU5hbWUnKTsKICAgIGlmIChzZXR0aW5nc05hbWUpIHNldHRpbmdzTmFtZS52YWx1ZSA9IHN0YXRlLmlkZW50aXR5LmRpc3BsYXlOYW1lIHx8ICcnOwoKICAgIHZhciBmcEVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbW9kYWxGaW5nZXJwcmludCcpOwogICAgaWYgKGZwRWxlbSkgZnBFbGVtLnRleHRDb250ZW50ID0gc3RhdGUuaWRlbnRpdHkubXlGaW5nZXJwcmludDsKCiAgICAvLyDQodC40L3RhdGA0L7QvdC40LfQuNGA0YPQtdC8IElEINGBINC90LDRgtC40LLQvdGL0LwgQW5kcm9pZCBCTEUg0YHQtdGA0LLQuNGB0L7QvAogICAgaWYgKHdpbmRvdy5BbmRyb2lkQnJpZGdlICYmIHdpbmRvdy5BbmRyb2lkQnJpZGdlLnVwZGF0ZU5vZGVJZGVudGl0eSkgewogICAgICB0cnkgewogICAgICAgIHdpbmRvdy5BbmRyb2lkQnJpZGdlLnVwZGF0ZU5vZGVJZGVudGl0eShzdGF0ZS5pZGVudGl0eS5teUlkKTsKICAgICAgfSBjYXRjaCAoZSkge30KICAgIH0KCiAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgcmVuZGVyTmVhcmJ5UGVlcnMoKTsKICAgIHNldHVwRXZlbnRMaXN0ZW5lcnMoKTsKICAgIGluaXROZXR3b3JrVHJhbnNwb3J0cygpOwoKICAgIC8vINCX0LDQv9GD0YHQutCw0LXQvCDQv9C10YDQuNC+0LTQuNGH0LXRgdC60LjQuSDQvNCw0Y/QuiDQvtCx0L3QsNGA0YPQttC10L3QuNGPCiAgICBzZXRJbnRlcnZhbChzZW5kRGlzY292ZXJ5QmVhY29uLCA1MDAwKTsKICAgIHNlbmREaXNjb3ZlcnlCZWFjb24oKTsKICB9CgogIGZ1bmN0aW9uIHJlbmRlckNoYXRMaXN0KCkgewogICAgdmFyIGNvbnRhaW5lciA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdjaGF0TGlzdCcpOwogICAgaWYgKCFjb250YWluZXIpIHJldHVybjsKICAgIGNvbnRhaW5lci5pbm5lckhUTUwgPSAnJzsKCiAgICB2YXIgZmlsdGVyZWQgPSBzdGF0ZS5jaGF0cy5maWx0ZXIoZnVuY3Rpb24gKGNoYXQpIHsKICAgICAgaWYgKCFzdGF0ZS5zZWFyY2hRdWVyeSkgcmV0dXJuIHRydWU7CiAgICAgIHZhciBxID0gc3RhdGUuc2VhcmNoUXVlcnkudG9Mb3dlckNhc2UoKTsKICAgICAgcmV0dXJuICgKICAgICAgICBjaGF0Lm5hbWUudG9Mb3dlckNhc2UoKS5pbmRleE9mKHEpICE9PSAtMSB8fAogICAgICAgIChjaGF0Lmxhc3RNZXNzYWdlICYmIGNoYXQubGFzdE1lc3NhZ2UudG9Mb3dlckNhc2UoKS5pbmRleE9mKHEpICE9PSAtMSkKICAgICAgKTsKICAgIH0pOwoKICAgIGZpbHRlcmVkLmZvckVhY2goZnVuY3Rpb24gKGNoYXQpIHsKICAgICAgdmFyIGl0ZW0gPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgaXRlbS5jbGFzc05hbWUgPSAnY2hhdC1pdGVtJyArIChjaGF0LmlkID09PSBzdGF0ZS5hY3RpdmVDaGF0SWQgPyAnIGFjdGl2ZScgOiAnJyk7CgogICAgICBpdGVtLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgc2VsZWN0Q2hhdChjaGF0LmlkKTsKICAgICAgfTsKCiAgICAgIHZhciBncmFkID0gZ2V0QXZhdGFyQ29sb3IoY2hhdC5pZCk7CiAgICAgIHZhciBhdmF0YXJTdHlsZSA9ICdiYWNrZ3JvdW5kOiBsaW5lYXItZ3JhZGllbnQoMTM1ZGVnLCAnICsgZ3JhZFswXSArICcsICcgKyBncmFkWzFdICsgJyknOwoKICAgICAgdmFyIHVucmVhZEh0bWwgPQogICAgICAgIGNoYXQudW5yZWFkID4gMAogICAgICAgICAgPyAnPHNwYW4gY2xhc3M9InVucmVhZC1iYWRnZSI+JyArIGNoYXQudW5yZWFkICsgJzwvc3Bhbj4nCiAgICAgICAgICA6ICcnOwoKICAgICAgdmFyIGNoYXRNZXRhID0gZ2V0UGVlck1ldGFkYXRhKHsgaWQ6IGNoYXQuaWQsIG5hbWU6IGNoYXQubmFtZSB9KTsKICAgICAgdmFyIHRhZ0h0bWwgPSAnJzsKICAgICAgaWYgKGNoYXQuaWQgPT09ICdtZXNoLWJyb2FkY2FzdCcpIHsKICAgICAgICB0YWdIdG1sID0gJzxzcGFuIGNsYXNzPSJwbGF0Zm9ybS1waWxsIiBzdHlsZT0ibWFyZ2luLWxlZnQ6NnB4OyBmb250LXNpemU6OHB4OyI+0K3QpNCY0KA8L3NwYW4+JzsKICAgICAgfSBlbHNlIHsKICAgICAgICB0YWdIdG1sID0gJzxzcGFuIGNsYXNzPSJwbGF0Zm9ybS1waWxsICcgKyBjaGF0TWV0YS5iYWRnZUNsYXNzICsgJyIgc3R5bGU9Im1hcmdpbi1sZWZ0OjZweDsgZm9udC1zaXplOjhweDsiPicgKyBjaGF0TWV0YS5iYWRnZVRleHQgKyAnPC9zcGFuPic7CiAgICAgIH0KCiAgICAgIGl0ZW0uaW5uZXJIVE1MID0KICAgICAgICAnPGRpdiBjbGFzcz0iYXZhdGFyIiBzdHlsZT0iJyArCiAgICAgICAgYXZhdGFyU3R5bGUgKwogICAgICAgICciPicgKwogICAgICAgIGVzY2FwZUh0bWwoY2hhdC5hdmF0YXIgfHwgJ1AnKSArCiAgICAgICAgJzwvZGl2PicgKwogICAgICAgICc8ZGl2IGNsYXNzPSJjaGF0LWl0ZW0tbWV0YSI+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9ImNoYXQtaXRlbS10b3AiPicgKwogICAgICAgICc8c3BhbiBjbGFzcz0iY2hhdC1pdGVtLW5hbWUiPicgKwogICAgICAgIGVzY2FwZUh0bWwoY2hhdC5uYW1lKSArCiAgICAgICAgdGFnSHRtbCArCiAgICAgICAgJzwvc3Bhbj4nICsKICAgICAgICAnPHNwYW4gY2xhc3M9ImNoYXQtaXRlbS10aW1lIj4nICsKICAgICAgICBlc2NhcGVIdG1sKGNoYXQudGltZSkgKwogICAgICAgICc8L3NwYW4+JyArCiAgICAgICAgJzwvZGl2PicgKwogICAgICAgICc8ZGl2IGNsYXNzPSJjaGF0LWl0ZW0tcHJldmlldyI+JyArCiAgICAgICAgJzxzcGFuIGNsYXNzPSJwcmV2aWV3LXRleHQiPicgKwogICAgICAgIGVzY2FwZUh0bWwoY2hhdC5sYXN0TWVzc2FnZSB8fCAnJykgKwogICAgICAgICc8L3NwYW4+JyArCiAgICAgICAgdW5yZWFkSHRtbCArCiAgICAgICAgJzwvZGl2PicgKwogICAgICAgICc8L2Rpdj4nOwoKICAgICAgY29udGFpbmVyLmFwcGVuZENoaWxkKGl0ZW0pOwogICAgfSk7CiAgfQoKICBmdW5jdGlvbiBzZWxlY3RDaGF0KGNoYXRJZCkgewogICAgc3RhdGUuYWN0aXZlQ2hhdElkID0gY2hhdElkOwogICAgdmFyIGNoYXQgPSBzdGF0ZS5jaGF0cy5maW5kKGZ1bmN0aW9uIChjKSB7CiAgICAgIHJldHVybiBjLmlkID09PSBjaGF0SWQ7CiAgICB9KTsKICAgIGlmIChjaGF0KSBjaGF0LnVucmVhZCA9IDA7CgogICAgc2F2ZUNoYXRzKCk7CiAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwoKICAgIHZhciBsYXlvdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYXBwTGF5b3V0Jyk7CiAgICBpZiAobGF5b3V0KSBsYXlvdXQuY2xhc3NMaXN0LmFkZCgnbW9iaWxlLWNoYXQtb3BlbicpOwogIH0KCiAgZnVuY3Rpb24gcmVuZGVyQWN0aXZlQ2hhdCgpIHsKICAgIHZhciBjaGF0ID0gc3RhdGUuY2hhdHMuZmluZChmdW5jdGlvbiAoYykgewogICAgICByZXR1cm4gYy5pZCA9PT0gc3RhdGUuYWN0aXZlQ2hhdElkOwogICAgfSk7CiAgICBpZiAoIWNoYXQpIHJldHVybjsKCiAgICB2YXIgbmFtZUVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYWN0aXZlQ2hhdE5hbWUnKTsKICAgIHZhciBzdGF0dXNFbGVtID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2FjdGl2ZUNoYXRTdGF0dXNUZXh0Jyk7CiAgICB2YXIgYXZhdGFyRWxlbSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdhY3RpdmVDaGF0QXZhdGFyJyk7CiAgICB2YXIgYmFubmVyVGV4dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdlbmNyeXB0aW9uQmFubmVyVGV4dCcpOwoKICAgIGlmIChuYW1lRWxlbSkgbmFtZUVsZW0udGV4dENvbnRlbnQgPSBjaGF0Lm5hbWU7CiAgICBpZiAoc3RhdHVzRWxlbSkgc3RhdHVzRWxlbS50ZXh0Q29udGVudCA9IGNoYXQuc3RhdHVzOwogICAgaWYgKGF2YXRhckVsZW0pIHsKICAgICAgYXZhdGFyRWxlbS50ZXh0Q29udGVudCA9IGNoYXQuYXZhdGFyIHx8ICdQJzsKICAgICAgdmFyIGdyYWQgPSBnZXRBdmF0YXJDb2xvcihjaGF0LmlkKTsKICAgICAgYXZhdGFyRWxlbS5zdHlsZS5iYWNrZ3JvdW5kID0gJ2xpbmVhci1ncmFkaWVudCgxMzVkZWcsICcgKyBncmFkWzBdICsgJywgJyArIGdyYWRbMV0gKyAnKSc7CiAgICB9CgogICAgaWYgKGJhbm5lclRleHQpIHsKICAgICAgaWYgKGNoYXQuaWQgPT09ICdtZXNoLWJyb2FkY2FzdCcpIHsKICAgICAgICBiYW5uZXJUZXh0LnRleHRDb250ZW50ID0KICAgICAgICAgICfQntCx0YnQuNC5INC+0YLQutGA0YvRgtGL0LkgTWVzaC3QutCw0L3QsNC7LiDQktGB0LUg0L3QsNGF0L7QtNGP0YnQuNC10YHRjyDRgNGP0LTQvtC8INGD0LfQu9GLINCy0LjQtNGP0YIg0Y3RgtC4INGB0L7QvtCx0YnQtdC90LjRjy4nOwogICAgICB9IGVsc2UgewogICAgICAgIGJhbm5lclRleHQudGV4dENvbnRlbnQgPQogICAgICAgICAgJ9Cf0LXRgNGB0L7QvdCw0LvRjNC90YvQuSBFMkVFINC60LDQvdCw0LsuINCo0LjRhNGA0L7QstCw0L3QuNC1IFgyNTUxOSAmIENoYUNoYTIwLVBvbHkxMzA1INGBINGD0LfQu9C+0LwgJyArCiAgICAgICAgICBjaGF0LmlkOwogICAgICB9CiAgICB9CgogICAgdmFyIHZpZXdwb3J0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ21lc3NhZ2VzVmlld3BvcnQnKTsKICAgIGlmICghdmlld3BvcnQpIHJldHVybjsKCiAgICAvLyDQodC+0YXRgNCw0L3Rj9C10LwgZGF0ZS1iYWRnZSDQuCBlbmNyeXB0aW9uLWJhbm5lcgogICAgdmFyIHN0YXRpY0VsZW1lbnRzID0gW107CiAgICB2YXIgZGF0ZUJhZGdlID0gdmlld3BvcnQucXVlcnlTZWxlY3RvcignLmRhdGUtYmFkZ2Utd3JhcCcpOwogICAgdmFyIGVuY0Jhbm5lciA9IHZpZXdwb3J0LnF1ZXJ5U2VsZWN0b3IoJy5lbmNyeXB0aW9uLWJhbm5lcicpOwogICAgaWYgKGRhdGVCYWRnZSkgc3RhdGljRWxlbWVudHMucHVzaChkYXRlQmFkZ2UpOwogICAgaWYgKGVuY0Jhbm5lcikgc3RhdGljRWxlbWVudHMucHVzaChlbmNCYW5uZXIpOwoKICAgIHZpZXdwb3J0LmlubmVySFRNTCA9ICcnOwogICAgc3RhdGljRWxlbWVudHMuZm9yRWFjaChmdW5jdGlvbiAoZWwpIHsKICAgICAgdmlld3BvcnQuYXBwZW5kQ2hpbGQoZWwpOwogICAgfSk7CgogICAgY2hhdC5tZXNzYWdlcy5mb3JFYWNoKGZ1bmN0aW9uIChtc2cpIHsKICAgICAgdmFyIHJvdyA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ2RpdicpOwogICAgICByb3cuY2xhc3NOYW1lID0gJ21lc3NhZ2Utcm93ICcgKyAobXNnLmlzT3V0Z29pbmcgPyAnb3V0Z29pbmcnIDogJ2luY29taW5nJyk7CgogICAgICB2YXIgYnViYmxlID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgIGJ1YmJsZS5jbGFzc05hbWUgPSAnYnViYmxlJzsKCiAgICAgIGlmICghbXNnLmlzT3V0Z29pbmcgJiYgY2hhdC5pZCA9PT0gJ21lc2gtYnJvYWRjYXN0JyAmJiBtc2cuYXV0aG9yTmFtZSkgewogICAgICAgIHZhciBhdXRob3IgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdkaXYnKTsKICAgICAgICBhdXRob3IuY2xhc3NOYW1lID0gJ2J1YmJsZS1hdXRob3InOwogICAgICAgIGF1dGhvci50ZXh0Q29udGVudCA9IG1zZy5hdXRob3JOYW1lOwogICAgICAgIGJ1YmJsZS5hcHBlbmRDaGlsZChhdXRob3IpOwogICAgICB9CgogICAgICAvLyDQotC10LrRgdGC0L7QstC+0LUg0YHQvtC+0LHRidC10L3QuNC1CiAgICAgIGlmIChtc2cudHlwZSA9PT0gJ3RleHQnKSB7CiAgICAgICAgdmFyIHRleHRFbCA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ2RpdicpOwogICAgICAgIHRleHRFbC5jbGFzc05hbWUgPSAnYnViYmxlLXRleHQnOwogICAgICAgIHRleHRFbC50ZXh0Q29udGVudCA9IG1zZy50ZXh0OwogICAgICAgIGJ1YmJsZS5hcHBlbmRDaGlsZCh0ZXh0RWwpOwogICAgICB9CiAgICAgIC8vINCk0LDQudC7INC40LvQuCDRhNC+0YLQvgogICAgICBlbHNlIGlmIChtc2cudHlwZSA9PT0gJ2ZpbGUnKSB7CiAgICAgICAgaWYgKG1zZy5maWxlVHlwZSAmJiBtc2cuZmlsZVR5cGUuaW5kZXhPZignaW1hZ2UvJykgPT09IDAgJiYgbXNnLmZpbGVEYXRhKSB7CiAgICAgICAgICB2YXIgaW1nID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnaW1nJyk7CiAgICAgICAgICBpbWcuY2xhc3NOYW1lID0gJ2J1YmJsZS1pbWFnZS1wcmV2aWV3JzsKICAgICAgICAgIGltZy5zcmMgPSBtc2cuZmlsZURhdGE7CiAgICAgICAgICBpbWcuYWx0ID0gbXNnLmZpbGVOYW1lIHx8ICfQmNC30L7QsdGA0LDQttC10L3QuNC1JzsKICAgICAgICAgIGJ1YmJsZS5hcHBlbmRDaGlsZChpbWcpOwogICAgICAgIH0KCiAgICAgICAgdmFyIGZpbGVDYXJkID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgICAgZmlsZUNhcmQuY2xhc3NOYW1lID0gJ2J1YmJsZS1maWxlLWNhcmQnOwogICAgICAgIGZpbGVDYXJkLmlubmVySFRNTCA9CiAgICAgICAgICAnPGRpdiBjbGFzcz0iZmlsZS1pY29uLWJveCI+JyArCiAgICAgICAgICBkb2NJY29uU3ZnICsKICAgICAgICAgICc8L2Rpdj4nICsKICAgICAgICAgICc8ZGl2IGNsYXNzPSJmaWxlLWNhcmQtZGV0YWlscyI+JyArCiAgICAgICAgICAnPGRpdiBjbGFzcz0iZmlsZS1jYXJkLW5hbWUiPicgKwogICAgICAgICAgZXNjYXBlSHRtbChtc2cuZmlsZU5hbWUgfHwgJ9Ck0LDQudC7JykgKwogICAgICAgICAgJzwvZGl2PicgKwogICAgICAgICAgJzxkaXYgY2xhc3M9ImZpbGUtY2FyZC1zaXplIj4nICsKICAgICAgICAgIGZvcm1hdEZpbGVTaXplKG1zZy5maWxlU2l6ZSkgKwogICAgICAgICAgJzwvZGl2PicgKwogICAgICAgICAgJzwvZGl2PicgKwogICAgICAgICAgJzxhIGNsYXNzPSJmaWxlLWRvd25sb2FkLWxpbmsiIHRpdGxlPSLQodC60LDRh9Cw0YLRjCI+JyArCiAgICAgICAgICBkb3dubG9hZEljb25TdmcgKwogICAgICAgICAgJzwvYT4nOwoKICAgICAgICB2YXIgZGxMaW5rID0gZmlsZUNhcmQucXVlcnlTZWxlY3RvcignLmZpbGUtZG93bmxvYWQtbGluaycpOwogICAgICAgIGlmIChkbExpbmsgJiYgbXNnLmZpbGVEYXRhKSB7CiAgICAgICAgICBkbExpbmsuaHJlZiA9IG1zZy5maWxlRGF0YTsKICAgICAgICAgIGRsTGluay5kb3dubG9hZCA9IG1zZy5maWxlTmFtZSB8fCAnc3lwaGVyX2ZpbGUnOwogICAgICAgIH0KICAgICAgICBidWJibGUuYXBwZW5kQ2hpbGQoZmlsZUNhcmQpOwogICAgICB9CgogICAgICAvLyDQpNGD0YLQtdGAINCx0LDQsdCx0LvQsDog0LLRgNC10LzRjyDQuCDRgdGC0LDRgtGD0YEg0LTQvtGB0YLQsNCy0LrQuAogICAgICB2YXIgZm9vdGVyID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgIGZvb3Rlci5jbGFzc05hbWUgPSAnYnViYmxlLWZvb3Rlcic7CgogICAgICB2YXIgdGltZVNwYW4gPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdzcGFuJyk7CiAgICAgIHRpbWVTcGFuLmNsYXNzTmFtZSA9ICdidWJibGUtdGltZSc7CiAgICAgIHRpbWVTcGFuLnRleHRDb250ZW50ID0gbXNnLnRpbWU7CiAgICAgIGZvb3Rlci5hcHBlbmRDaGlsZCh0aW1lU3Bhbik7CgogICAgICBpZiAobXNnLmlzT3V0Z29pbmcpIHsKICAgICAgICB2YXIgdGlja1dyYXAgPSBkb2N1bWVudC5jcmVhdGVFbGVtZW50KCdzcGFuJyk7CiAgICAgICAgdGlja1dyYXAuaW5uZXJIVE1MID0gbXNnLmRlbGl2ZXJlZCA/IGRvdWJsZUNoZWNrU3ZnIDogc2luZ2xlQ2hlY2tTdmc7CiAgICAgICAgZm9vdGVyLmFwcGVuZENoaWxkKHRpY2tXcmFwKTsKICAgICAgfQoKICAgICAgYnViYmxlLmFwcGVuZENoaWxkKGZvb3Rlcik7CiAgICAgIHJvdy5hcHBlbmRDaGlsZChidWJibGUpOwogICAgICB2aWV3cG9ydC5hcHBlbmRDaGlsZChyb3cpOwogICAgfSk7CgogICAgdmlld3BvcnQuc2Nyb2xsVG9wID0gdmlld3BvcnQuc2Nyb2xsSGVpZ2h0OwogIH0KCiAgLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQogIC8vINCg0JDQlNCQ0KAg0KPQodCi0KDQntCZ0KHQotCSINCf0J7QkdCb0JjQl9Ce0KHQotCYIChCTEUg0J/QoNCe0KXQntCW0JjQlSDQmCBXSS1GSSkKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgZnVuY3Rpb24gZ2V0UGVlck1ldGFkYXRhKHBlZXIpIHsKICAgIHZhciBpZCA9IChwZWVyLmlkIHx8ICcnKS50b0xvd2VyQ2FzZSgpOwogICAgdmFyIG5hbWUgPSAocGVlci5uYW1lICYmIHBlZXIubmFtZSAhPT0gcGVlci5pZCkgPyBwZWVyLm5hbWUgOiAnJzsKICAgIHZhciBwbGF0Zm9ybSA9ICfQo9C30LXQuyBNZXNoJzsKICAgIHZhciBiYWRnZUNsYXNzID0gJ3BpbGwtbWVzaCc7CiAgICB2YXIgYmFkZ2VUZXh0ID0gJ01FU0gnOwogICAgdmFyIGljb25MZXR0ZXIgPSAnUyc7CgogICAgaWYgKGlkLmluZGV4T2YoJ3N5cC13aW4tJykgIT09IC0xIHx8IG5hbWUgPT09ICfQmtC+0LzQv9GM0Y7RgtC10YAgV2luZG93cycpIHsKICAgICAgcGxhdGZvcm0gPSAn0JrQvtC80L/RjNGO0YLQtdGAIFdpbmRvd3MnOwogICAgICBiYWRnZUNsYXNzID0gJ3BpbGwtd2luJzsKICAgICAgYmFkZ2VUZXh0ID0gJ1dJTic7CiAgICAgIGljb25MZXR0ZXIgPSAnVyc7CiAgICB9IGVsc2UgaWYgKGlkLmluZGV4T2YoJ3N5cC1kcm9pZC0nKSAhPT0gLTEgfHwgbmFtZSA9PT0gJ9Ci0LXQu9C10YTQvtC9IEFuZHJvaWQnKSB7CiAgICAgIHBsYXRmb3JtID0gJ9Ci0LXQu9C10YTQvtC9IEFuZHJvaWQnOwogICAgICBiYWRnZUNsYXNzID0gJ3BpbGwtZHJvaWQnOwogICAgICBiYWRnZVRleHQgPSAnQU5EUk9JRCc7CiAgICAgIGljb25MZXR0ZXIgPSAnQSc7CiAgICB9IGVsc2UgaWYgKGlkLmluZGV4T2YoJ3N5cC1saW4tJykgIT09IC0xIHx8IG5hbWUgPT09ICfQmtC+0LzQv9GM0Y7RgtC10YAgTGludXgnKSB7CiAgICAgIHBsYXRmb3JtID0gJ9Ca0L7QvNC/0YzRjtGC0LXRgCBMaW51eCc7CiAgICAgIGJhZGdlQ2xhc3MgPSAncGlsbC1saW51eCc7CiAgICAgIGJhZGdlVGV4dCA9ICdMSU5VWCc7CiAgICAgIGljb25MZXR0ZXIgPSAnTCc7CiAgICB9IGVsc2UgaWYgKGlkLmluZGV4T2YoJ3N5cC1tYWMtJykgIT09IC0xIHx8IG5hbWUgPT09ICfQmtC+0LzQv9GM0Y7RgtC10YAgTWFjJykgewogICAgICBwbGF0Zm9ybSA9ICfQmtC+0LzQv9GM0Y7RgtC10YAgTWFjJzsKICAgICAgYmFkZ2VDbGFzcyA9ICdwaWxsLWFwcGxlJzsKICAgICAgYmFkZ2VUZXh0ID0gJ01BQyc7CiAgICAgIGljb25MZXR0ZXIgPSAnTSc7CiAgICB9IGVsc2UgaWYgKGlkLmluZGV4T2YoJ3N5cC1pb3MtJykgIT09IC0xIHx8IG5hbWUgPT09ICdBcHBsZSBpUGhvbmUnKSB7CiAgICAgIHBsYXRmb3JtID0gJ0FwcGxlIGlQaG9uZSc7CiAgICAgIGJhZGdlQ2xhc3MgPSAncGlsbC1hcHBsZSc7CiAgICAgIGJhZGdlVGV4dCA9ICdJT1MnOwogICAgICBpY29uTGV0dGVyID0gJ2knOwogICAgfQoKICAgIHZhciBzaG9ydENvZGUgPSBpZC5yZXBsYWNlKC9zeXAtKHdpbi18ZHJvaWQtfGxpbi18bWFjLXxpb3MtfG5vZGUtKT8vZywgJycpLnRvVXBwZXJDYXNlKCk7CiAgICBpZiAoc2hvcnRDb2RlLmxlbmd0aCA+IDQpIHNob3J0Q29kZSA9IHNob3J0Q29kZS5zbGljZSgtNCk7CiAgICB2YXIgdGl0bGUgPSAobmFtZSAmJiBuYW1lICE9PSBpZCkgPyBuYW1lIDogKHBsYXRmb3JtICsgJyAjJyArIChzaG9ydENvZGUgfHwgJ1BFRVInKSk7CgogICAgcmV0dXJuIHsKICAgICAgdGl0bGU6IHRpdGxlLAogICAgICBwbGF0Zm9ybTogcGxhdGZvcm0sCiAgICAgIGJhZGdlQ2xhc3M6IGJhZGdlQ2xhc3MsCiAgICAgIGJhZGdlVGV4dDogYmFkZ2VUZXh0LAogICAgICBpY29uTGV0dGVyOiBpY29uTGV0dGVyLAogICAgfTsKICB9CgogIHZhciByZW5kZXJOZWFyYnlUaW1lciA9IG51bGw7CiAgZnVuY3Rpb24gc2NoZWR1bGVSZW5kZXJOZWFyYnlQZWVycygpIHsKICAgIGlmIChyZW5kZXJOZWFyYnlUaW1lcikgcmV0dXJuOwogICAgcmVuZGVyTmVhcmJ5VGltZXIgPSBzZXRUaW1lb3V0KGZ1bmN0aW9uICgpIHsKICAgICAgcmVuZGVyTmVhcmJ5VGltZXIgPSBudWxsOwogICAgICByZW5kZXJOZWFyYnlQZWVycygpOwogICAgfSwgMzUwKTsKICB9CgogIGZ1bmN0aW9uIHJlbmRlck5lYXJieVBlZXJzKCkgewogICAgdmFyIGxpc3QgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbmVhcmJ5UGVlckxpc3QnKTsKICAgIHZhciBiYWRnZSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdwZWVyQ291bnRlckJhZGdlJyk7CiAgICB2YXIgbW9kYWxMaXN0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2Rpc2NvdmVyZWRQZWVyc01vZGFsTGlzdCcpOwogICAgaWYgKCFsaXN0KSByZXR1cm47CgogICAgdmFyIHBlZXJzID0gT2JqZWN0LnZhbHVlcyhzdGF0ZS5kaXNjb3ZlcmVkUGVlcnMpOwogICAgdmFyIG5vdyA9IERhdGUubm93KCk7CiAgICAvLyDQntGB0YLQsNCy0LvRj9C10Lwg0YPQt9C70YssINCy0LjQtNC40LzRi9C1INC/0L7RgdC70LXQtNC90LjQtSA0NSDRgdC10LrRg9C90LQKICAgIHBlZXJzID0gcGVlcnMuZmlsdGVyKGZ1bmN0aW9uIChwKSB7CiAgICAgIHJldHVybiAobm93IC0gcC5sYXN0U2VlbiA8IDQ1MDAwKSAmJiBwLmlkICYmIHAuaWQuaW5kZXhPZignc3lwLScpID09PSAwOwogICAgfSk7CgogICAgaWYgKGJhZGdlKSBiYWRnZS50ZXh0Q29udGVudCA9IFN0cmluZyhwZWVycy5sZW5ndGgpOwoKICAgIHZhciBkZXNjID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ21lc2hTdGF0dXNEZXNjJyk7CiAgICBpZiAoZGVzYykgewogICAgICBkZXNjLnRleHRDb250ZW50ID0KICAgICAgICBwZWVycy5sZW5ndGggPiAwCiAgICAgICAgICA/IHBlZXJzLmxlbmd0aCArICcg0YPQt9C10Lso0L7Qsikg0LIg0YDQsNC00LjRg9GB0LUg0YDQsNC00LjQvtC60LDQvdCw0LvQsCcKICAgICAgICAgIDogJ9Ch0LrQsNC90LjRgNC+0LLQsNC90LjQtSBCTEUg0LggVURQLi4uJzsKICAgIH0KCiAgICBpZiAocGVlcnMubGVuZ3RoID09PSAwKSB7CiAgICAgIGxpc3QuaW5uZXJIVE1MID0KICAgICAgICAnPGRpdiBjbGFzcz0iZW1wdHktbmVhcmJ5LXBsYWNlaG9sZGVyIj48c3Bhbj7Qn9C+0LjRgdC6INGD0LfQu9C+0LIg0L/QviBCTEUg0Lgg0LvQvtC60LDQu9GM0L3QvtC5INGB0LXRgtC4Li4uPC9zcGFuPjwvZGl2Pic7CiAgICAgIGlmIChtb2RhbExpc3QpIHsKICAgICAgICBtb2RhbExpc3QuaW5uZXJIVE1MID0KICAgICAgICAgICc8c3BhbiBzdHlsZT0iZm9udC1zaXplOjEycHg7IGNvbG9yOnZhcigtLXRleHQtbXV0ZWQpIj7QndC10YIg0LDQutGC0LjQstC90YvRhSDRg9C30LvQvtCyINC/0L7QsdC70LjQt9C+0YHRgtC4PC9zcGFuPic7CiAgICAgIH0KICAgICAgcmV0dXJuOwogICAgfQoKICAgIGxpc3QuaW5uZXJIVE1MID0gJyc7CiAgICBpZiAobW9kYWxMaXN0KSBtb2RhbExpc3QuaW5uZXJIVE1MID0gJyc7CgogICAgcGVlcnMuZm9yRWFjaChmdW5jdGlvbiAocGVlcikgewogICAgICB2YXIgcm93ID0gZG9jdW1lbnQuY3JlYXRlRWxlbWVudCgnZGl2Jyk7CiAgICAgIHJvdy5jbGFzc05hbWUgPSAnbmVhcmJ5LXBlZXItcm93JzsKCiAgICAgIHZhciBtZXRhID0gZ2V0UGVlck1ldGFkYXRhKHBlZXIpOwogICAgICB2YXIgZ3JhZCA9IGdldEF2YXRhckNvbG9yKHBlZXIuaWQpOwogICAgICB2YXIgYXZhdGFyU3R5bGUgPSAnYmFja2dyb3VuZDogbGluZWFyLWdyYWRpZW50KDEzNWRlZywgJyArIGdyYWRbMF0gKyAnLCAnICsgZ3JhZFsxXSArICcpJzsKCiAgICAgIHZhciBtZXRhVGV4dCA9ICcnOwogICAgICBpZiAocGVlci50cmFuc3BvcnQgPT09ICdCTEUnKSB7CiAgICAgICAgbWV0YVRleHQgPSAnQkxFIH4nICsgKHBlZXIuZGlzdGFuY2UgfHwgJzEuNScpICsgJ9C8ICgnICsgKHBlZXIucnNzaSB8fCAnLTYwJykgKyAnIGRCbSknOwogICAgICB9IGVsc2UgewogICAgICAgIG1ldGFUZXh0ID0gJ1dpLUZpOiAnICsgKHBlZXIuaXAgfHwgJ9C70L7QutCw0LvRjNC90L4nKTsKICAgICAgfQoKICAgICAgcm93LmlubmVySFRNTCA9CiAgICAgICAgJzxkaXYgY2xhc3M9InBlZXItcm93LWxlZnQiPicgKwogICAgICAgICc8ZGl2IGNsYXNzPSJuZWFyYnktYXZhdGFyIiBzdHlsZT0iJyArIGF2YXRhclN0eWxlICsgJyI+JyArCiAgICAgICAgZXNjYXBlSHRtbChtZXRhLmljb25MZXR0ZXIpICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9Im5lYXJieS1pbmZvIj4nICsKICAgICAgICAnPGRpdiBjbGFzcz0ibmVhcmJ5LXRpdGxlLXJvdyI+JyArCiAgICAgICAgJzxzcGFuIGNsYXNzPSJuZWFyYnktbmFtZSI+JyArIGVzY2FwZUh0bWwobWV0YS50aXRsZSkgKyAnPC9zcGFuPicgKwogICAgICAgICc8c3BhbiBjbGFzcz0icGxhdGZvcm0tcGlsbCAnICsgbWV0YS5iYWRnZUNsYXNzICsgJyI+JyArIGVzY2FwZUh0bWwobWV0YS5iYWRnZVRleHQpICsgJzwvc3Bhbj4nICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxkaXYgY2xhc3M9Im5lYXJieS1tZXRhLXJvdyI+JyArCiAgICAgICAgJzxzcGFuIGNsYXNzPSJuZWFyYnktaWQiPicgKyBlc2NhcGVIdG1sKHBlZXIuaWQpICsgJzwvc3Bhbj4g4oCiICcgKwogICAgICAgICc8c3BhbiBjbGFzcz0ibmVhcmJ5LW1ldGEiPicgKyBlc2NhcGVIdG1sKG1ldGFUZXh0KSArICc8L3NwYW4+JyArCiAgICAgICAgJzwvZGl2PicgKwogICAgICAgICc8L2Rpdj4nICsKICAgICAgICAnPC9kaXY+JyArCiAgICAgICAgJzxidXR0b24gY2xhc3M9InBlZXItYWN0aW9uLWJ0biIgdGl0bGU9ItCe0YLQutGA0YvRgtGMINC70LjRh9C90YvQuSDQtNC40LDQu9C+0LMg0YEg0Y3RgtC40Lwg0YPRgdGC0YDQvtC50YHRgtCy0L7QvCI+0J3QsNC/0LjRgdCw0YLRjDwvYnV0dG9uPic7CgogICAgICB2YXIgd3JpdGVCdG4gPSByb3cucXVlcnlTZWxlY3RvcignLnBlZXItYWN0aW9uLWJ0bicpOwogICAgICBpZiAod3JpdGVCdG4pIHsKICAgICAgICB3cml0ZUJ0bi5vbmNsaWNrID0gZnVuY3Rpb24gKGUpIHsKICAgICAgICAgIGUuc3RvcFByb3BhZ2F0aW9uKCk7CiAgICAgICAgICBzdGFydERpcmVjdENoYXRXaXRoUGVlcihwZWVyLmlkLCBtZXRhLnRpdGxlKTsKICAgICAgICB9OwogICAgICB9CgogICAgICByb3cub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBzdGFydERpcmVjdENoYXRXaXRoUGVlcihwZWVyLmlkLCBtZXRhLnRpdGxlKTsKICAgICAgfTsKCiAgICAgIGxpc3QuYXBwZW5kQ2hpbGQocm93KTsKCiAgICAgIC8vINCSINC80L7QtNCw0LvRjNC90L7QtSDQvtC60L3QviDQsdGL0YHRgtGA0L7Qs9C+INCy0YvQsdC+0YDQsAogICAgICBpZiAobW9kYWxMaXN0KSB7CiAgICAgICAgdmFyIHF1aWNrSXRlbSA9IGRvY3VtZW50LmNyZWF0ZUVsZW1lbnQoJ2RpdicpOwogICAgICAgIHF1aWNrSXRlbS5jbGFzc05hbWUgPSAnbmVhcmJ5LXBlZXItcm93JzsKICAgICAgICBxdWlja0l0ZW0uc3R5bGUuY3Vyc29yID0gJ3BvaW50ZXInOwogICAgICAgIHF1aWNrSXRlbS5pbm5lckhUTUwgPQogICAgICAgICAgJzxkaXYgc3R5bGU9ImRpc3BsYXk6ZmxleDsganVzdGlmeS1jb250ZW50OnNwYWNlLWJldHdlZW47IGFsaWduLWl0ZW1zOmNlbnRlcjsgd2lkdGg6MTAwJSI+JyArCiAgICAgICAgICAnPHNwYW4gc3R5bGU9ImZvbnQtZmFtaWx5OnZhcigtLWZvbnQtbW9ubyk7IGZvbnQtc2l6ZToxMnB4OyBjb2xvcjp2YXIoLS10ZXh0LXByaW1hcnkpIj4nICsKICAgICAgICAgIGVzY2FwZUh0bWwobWV0YS50aXRsZSkgKwogICAgICAgICAgJzwvc3Bhbj48c3BhbiBjbGFzcz0icGxhdGZvcm0tcGlsbCAnICsgbWV0YS5iYWRnZUNsYXNzICsgJyI+JyArCiAgICAgICAgICBlc2NhcGVIdG1sKG1ldGEuYmFkZ2VUZXh0KSArCiAgICAgICAgICAnPC9zcGFuPjwvZGl2Pic7CiAgICAgICAgcXVpY2tJdGVtLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgICB2YXIgaW5wdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbmV3Q2hhdFBlZXJJZElucHV0Jyk7CiAgICAgICAgICBpZiAoaW5wdXQpIGlucHV0LnZhbHVlID0gcGVlci5pZDsKICAgICAgICB9OwogICAgICAgIG1vZGFsTGlzdC5hcHBlbmRDaGlsZChxdWlja0l0ZW0pOwogICAgICB9CiAgICB9KTsKICB9CgogIGZ1bmN0aW9uIHN0YXJ0RGlyZWN0Q2hhdFdpdGhQZWVyKHBlZXJJZCwgcGVlck5hbWUpIHsKICAgIGlmIChwZWVySWQgPT09IHN0YXRlLmlkZW50aXR5Lm15SWQpIHJldHVybjsKCiAgICB2YXIgZXhpc3RpbmcgPSBzdGF0ZS5jaGF0cy5maW5kKGZ1bmN0aW9uIChjKSB7CiAgICAgIHJldHVybiBjLmlkID09PSBwZWVySWQ7CiAgICB9KTsKCiAgICB2YXIgbWV0YSA9IGdldFBlZXJNZXRhZGF0YSh7IGlkOiBwZWVySWQsIG5hbWU6IHBlZXJOYW1lIH0pOwoKICAgIGlmICghZXhpc3RpbmcpIHsKICAgICAgdmFyIG5ld0NoYXQgPSB7CiAgICAgICAgaWQ6IHBlZXJJZCwKICAgICAgICBuYW1lOiBtZXRhLnRpdGxlLAogICAgICAgIGF2YXRhcjogbWV0YS5pY29uTGV0dGVyLAogICAgICAgIHN0YXR1czogJ9Cb0LjRh9C90YvQuSDQtNC40LDQu9C+0LMgRTJFRSDigKIg0KLQvtC70YzQutC+INCy0Ysg0LTQstC+0LUnLAogICAgICAgIGxhc3RNZXNzYWdlOiAn0J/RgNGP0LzQvtC5INC30LDRiNC40YTRgNC+0LLQsNC90L3Ri9C5INC60LDQvdCw0Lsg0L7RgtC60YDRi9GCJywKICAgICAgICB0aW1lOiBmb3JtYXRUaW1lKG5ldyBEYXRlKCkpLAogICAgICAgIHVucmVhZDogMCwKICAgICAgICBtZXNzYWdlczogWwogICAgICAgICAgewogICAgICAgICAgICBpZDogJ2luZm9fJyArIERhdGUubm93KCksCiAgICAgICAgICAgIHNlbmRlcjogJ3N5c3RlbScsCiAgICAgICAgICAgIGF1dGhvck5hbWU6ICfQodCY0KHQotCV0JzQkCcsCiAgICAgICAgICAgIHR5cGU6ICd0ZXh0JywKICAgICAgICAgICAgdGV4dDogJ9Ce0YLQutGA0YvRgiDQu9C40YfQvdGL0Lkg0LTQuNCw0LvQvtCzINGBICcgKyBtZXRhLnRpdGxlICsgJyAoJyArIHBlZXJJZCArICcpLiDQodC+0L7QsdGJ0LXQvdC40Y8g0YjQuNGE0YDRg9GO0YLRgdGPINGB0LrQstC+0LfQvdGL0Lwg0LrQu9GO0YfQvtC8IFgyNTUxOSAmIENoYUNoYTIwLVBvbHkxMzA1INC4INCy0LjQtNC90Ysg0KLQntCb0KzQmtCeINCy0LDQvCDQtNCy0L7QuNC8LicsCiAgICAgICAgICAgIHRpbWU6IGZvcm1hdFRpbWUobmV3IERhdGUoKSksCiAgICAgICAgICAgIGlzT3V0Z29pbmc6IGZhbHNlLAogICAgICAgICAgfSwKICAgICAgICBdLAogICAgICB9OwogICAgICBzdGF0ZS5jaGF0cy5wdXNoKG5ld0NoYXQpOwogICAgICBzYXZlQ2hhdHMoKTsKICAgIH0gZWxzZSBpZiAocGVlck5hbWUgJiYgZXhpc3RpbmcubmFtZSA9PT0gZXhpc3RpbmcuaWQpIHsKICAgICAgZXhpc3RpbmcubmFtZSA9IG1ldGEudGl0bGU7CiAgICAgIHNhdmVDaGF0cygpOwogICAgfQoKICAgIHNlbGVjdENoYXQocGVlcklkKTsKICB9CgogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICAvLyDQodCV0KLQldCS0J7QmSDQotCg0JDQndCh0J/QntCg0KIg0Jgg0J/QldCg0JXQlNCQ0KfQkCDQn9CQ0JrQldCi0J7QkgogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiBpbml0TmV0d29ya1RyYW5zcG9ydHMoKSB7CiAgICAvLyAxLiDQntC/0YDQtdC00LXQu9C10L3QuNC1INC70L7QutCw0LvRjNC90L7Qs9C+IElQINGBINC00LXRgdC60YLQvtC/INGB0LXRgNCy0LXRgNCwCiAgICBpZiAod2luZG93LmZldGNoKSB7CiAgICAgIGZldGNoKCcvYXBpL21lc2gvaXAnKQogICAgICAgIC50aGVuKGZ1bmN0aW9uIChyZXMpIHsKICAgICAgICAgIHJldHVybiByZXMuanNvbigpOwogICAgICAgIH0pCiAgICAgICAgLnRoZW4oZnVuY3Rpb24gKGRhdGEpIHsKICAgICAgICAgIGlmIChkYXRhICYmIGRhdGEuaXApIHsKICAgICAgICAgICAgc3RhdGUubG9jYWxJcCA9IGRhdGEuaXA7CiAgICAgICAgICAgIHVwZGF0ZUxvY2FsSXBVSShkYXRhLmlwKTsKICAgICAgICAgIH0KICAgICAgICB9KQogICAgICAgIC5jYXRjaChmdW5jdGlvbiAoKSB7fSk7CiAgICB9CgogICAgLy8gMi4gU2VydmVyLVNlbnQgRXZlbnRzIChTU0UpINC00LvRjyDQtNC10YHQutGC0L7Qv9CwIChMaW51eCwgbWFjT1MsIFdpbmRvd3MpCiAgICBpZiAod2luZG93LkV2ZW50U291cmNlKSB7CiAgICAgIHRyeSB7CiAgICAgICAgdmFyIHNzZSA9IG5ldyBFdmVudFNvdXJjZSgnL2FwaS9tZXNoL2V2ZW50cycpOwogICAgICAgIHNzZS5vbm1lc3NhZ2UgPSBmdW5jdGlvbiAoZSkgewogICAgICAgICAgaWYgKGUuZGF0YSAmJiB3aW5kb3cub25NZXNoUGFja2V0UmVjZWl2ZWQpIHsKICAgICAgICAgICAgd2luZG93Lm9uTWVzaFBhY2tldFJlY2VpdmVkKGUuZGF0YSk7CiAgICAgICAgICB9CiAgICAgICAgfTsKICAgICAgfSBjYXRjaCAoZXJyKSB7fQogICAgfQogIH0KCiAgZnVuY3Rpb24gdXBkYXRlTG9jYWxJcFVJKGlwKSB7CiAgICB2YXIgbGJsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NldHRpbmdzTG9jYWxJcCcpOwogICAgaWYgKGxibCkgbGJsLnRleHRDb250ZW50ID0gaXAgKyAnOjc3ODgnOwogIH0KCiAgd2luZG93Lm9uTG9jYWxJcERldGVjdGVkID0gZnVuY3Rpb24gKGlwKSB7CiAgICBpZiAoaXApIHsKICAgICAgc3RhdGUubG9jYWxJcCA9IGlwOwogICAgICB1cGRhdGVMb2NhbElwVUkoaXApOwogICAgfQogIH07CgogIC8vINCi0L7Rh9C60LAg0LLRhdC+0LTQsCDQtNC70Y8g0L7QsdC90LDRgNGD0LbQtdC90LjRjyDQv9GA0L7RhdC+0LbQuNGFINC/0L4gQkxFINC40Lcg0L3QsNGC0LjQstC90L7Qs9C+INGB0LXRgNCy0LjRgdCwCiAgd2luZG93Lm9uQmxlUGVlckRpc2NvdmVyZWQgPSBmdW5jdGlvbiAocGVlcklkLCByc3NpLCBkaXN0YW5jZSwgbWFjLCBuYW1lKSB7CiAgICBpZiAoIXBlZXJJZCB8fCBwZWVySWQgPT09IHN0YXRlLmlkZW50aXR5Lm15SWQpIHJldHVybjsKICAgIHN0YXRlLmRpc2NvdmVyZWRQZWVyc1twZWVySWRdID0gewogICAgICBpZDogcGVlcklkLAogICAgICBuYW1lOiBuYW1lIHx8IHBlZXJJZCwKICAgICAgdHJhbnNwb3J0OiAnQkxFJywKICAgICAgcnNzaTogcnNzaSwKICAgICAgZGlzdGFuY2U6IGRpc3RhbmNlLAogICAgICBtYWM6IG1hYywKICAgICAgbGFzdFNlZW46IERhdGUubm93KCksCiAgICB9OwogICAgc2NoZWR1bGVSZW5kZXJOZWFyYnlQZWVycygpOwogIH07CgogIC8vINCi0L7Rh9C60LAg0LLRhdC+0LTQsCDQstGF0L7QtNGP0YnQuNGFINC/0LDQutC10YLQvtCyIChVRFAsIEhUVFAsIEJMRSBHQVRUKQogIHdpbmRvdy5vbk1lc2hQYWNrZXRSZWNlaXZlZCA9IGZ1bmN0aW9uIChwYWNrZXRPckpzb24pIHsKICAgIHZhciBwYWNrZXQgPSBwYWNrZXRPckpzb247CiAgICBpZiAodHlwZW9mIHBhY2tldE9ySnNvbiA9PT0gJ3N0cmluZycpIHsKICAgICAgdHJ5IHsKICAgICAgICBwYWNrZXQgPSBKU09OLnBhcnNlKHBhY2tldE9ySnNvbik7CiAgICAgIH0gY2F0Y2ggKGUpIHsKICAgICAgICByZXR1cm47CiAgICAgIH0KICAgIH0KCiAgICBpZiAocGFja2V0ICYmIHBhY2tldC50eXBlID09PSAnQkxFX1BFRVJfRElTQ09WRVJFRCcpIHsKICAgICAgd2luZG93Lm9uQmxlUGVlckRpc2NvdmVyZWQocGFja2V0LnBlZXJJZCwgcGFja2V0LnJzc2ksIHBhY2tldC5kaXN0YW5jZSwgcGFja2V0Lm1hYywgcGFja2V0Lm5hbWUpOwogICAgICByZXR1cm47CiAgICB9CgogICAgaGFuZGxlSW5jb21pbmdQYWNrZXQocGFja2V0KTsKICB9OwoKICBmdW5jdGlvbiBzZW5kRGlzY292ZXJ5QmVhY29uKCkgewogICAgdmFyIGJlYWNvbiA9IHsKICAgICAgdHlwZTogJ1NZUEhfQkVBQ09OJywKICAgICAgaWQ6ICdiY25fJyArIERhdGUubm93KCksCiAgICAgIGZyb206IHN0YXRlLmlkZW50aXR5Lm15SWQsCiAgICAgIGZyb21OYW1lOiBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSwKICAgICAgc2VuZGVySXA6IHN0YXRlLmxvY2FsSXAsCiAgICAgIHRpbWVzdGFtcDogRGF0ZS5ub3coKSwKICAgIH07CiAgICBkaXNwYXRjaFBhY2tldFRvTmV0d29yayhiZWFjb24pOwogIH0KCiAgZnVuY3Rpb24gZGlzcGF0Y2hQYWNrZXRUb05ldHdvcmsocGFja2V0KSB7CiAgICBwYWNrZXQuc2VuZGVySXAgPSBzdGF0ZS5sb2NhbElwOwogICAgdmFyIHJhdyA9IEpTT04uc3RyaW5naWZ5KHBhY2tldCk7CgogICAgLy8gMS4g0J3QsNGC0LjQstC90YvQuSBBbmRyb2lkINC80L7RgdGCCiAgICBpZiAod2luZG93LkFuZHJvaWRCcmlkZ2UgJiYgd2luZG93LkFuZHJvaWRCcmlkZ2Uuc2VuZE1lc2hQYWNrZXQpIHsKICAgICAgdHJ5IHsKICAgICAgICB3aW5kb3cuQW5kcm9pZEJyaWRnZS5zZW5kTWVzaFBhY2tldChyYXcpOwogICAgICB9IGNhdGNoIChlKSB7fQogICAgfQoKICAgIC8vIDIuINCU0LXRgdC60YLQvtC/0L3Ri9C5INC70L7QutCw0LvRjNC90YvQuSDRgdC10YDQstC10YAKICAgIGlmICh3aW5kb3cuZmV0Y2gpIHsKICAgICAgZmV0Y2goJy9hcGkvbWVzaC9zZW5kJywgewogICAgICAgIG1ldGhvZDogJ1BPU1QnLAogICAgICAgIGhlYWRlcnM6IHsgJ0NvbnRlbnQtVHlwZSc6ICdhcHBsaWNhdGlvbi9qc29uJyB9LAogICAgICAgIGJvZHk6IHJhdywKICAgICAgfSkuY2F0Y2goZnVuY3Rpb24gKCkge30pOwogICAgfQoKICAgIC8vIDMuINCf0YDRj9C80L7QuSBUQ1AgSFRUUCBGYWxsYmFjayDQvdCwINCy0YHQtSDQuNC30LLQtdGB0YLQvdGL0LUgSVAgKNC+0LHRhdC+0LQg0L7Qs9GA0LDQvdC40YfQtdC90LjQuSDRgNC+0YPRgtC10YDQvtCyKQogICAgc3RhdGUua25vd25QZWVySXBzLmZvckVhY2goZnVuY3Rpb24gKHBlZXJJcCkgewogICAgICBpZiAocGVlcklwICYmIHBlZXJJcCAhPT0gJzEyNy4wLjAuMScgJiYgcGVlcklwICE9PSBzdGF0ZS5sb2NhbElwICYmIHdpbmRvdy5mZXRjaCkgewogICAgICAgIC8vINCe0YLQv9GA0LDQstC60LAg0L3QsCDQv9C+0YDRgiA3Nzg5IChBbmRyb2lkKSDQuCA3Nzg4IChEZXNrdG9wKQogICAgICAgIFs3Nzg5LCA3Nzg4XS5mb3JFYWNoKGZ1bmN0aW9uIChwdCkgewogICAgICAgICAgZmV0Y2goJ2h0dHA6Ly8nICsgcGVlcklwICsgJzonICsgcHQgKyAnL2FwaS9tZXNoL3NlbmQnLCB7CiAgICAgICAgICAgIG1ldGhvZDogJ1BPU1QnLAogICAgICAgICAgICBoZWFkZXJzOiB7ICdDb250ZW50LVR5cGUnOiAnYXBwbGljYXRpb24vanNvbicgfSwKICAgICAgICAgICAgYm9keTogcmF3LAogICAgICAgICAgICBtb2RlOiAnbm8tY29ycycsCiAgICAgICAgICB9KS5jYXRjaChmdW5jdGlvbiAoKSB7fSk7CiAgICAgICAgfSk7CiAgICAgIH0KICAgIH0pOwogIH0KCiAgZnVuY3Rpb24gaGFuZGxlSW5jb21pbmdQYWNrZXQocGFja2V0KSB7CiAgICBpZiAoIXBhY2tldCB8fCAhcGFja2V0LmZyb20pIHJldHVybjsKICAgIGlmIChwYWNrZXQuZnJvbSA9PT0gc3RhdGUuaWRlbnRpdHkubXlJZCkgcmV0dXJuOyAvLyDQmNCz0L3QvtGA0LjRgNGD0LXQvCDRjdGF0L4g0YHQstC+0LjRhSDQttC1INC/0LDQutC10YLQvtCyCgogICAgaWYgKHBhY2tldC5zZW5kZXJJcCAmJiBwYWNrZXQuc2VuZGVySXAgIT09ICcxMjcuMC4wLjEnKSB7CiAgICAgIHN0YXRlLmtub3duUGVlcklwcy5hZGQocGFja2V0LnNlbmRlcklwKTsKICAgIH0KCiAgICAvLyDQoNC10LPQuNGB0YLRgNC40YDRg9C10Lwg0L7QsdC90LDRgNGD0LbQtdC90L3Ri9C5INGD0LfQtdC7INCyINGA0LDQtNCw0YDQtQogICAgc3RhdGUuZGlzY292ZXJlZFBlZXJzW3BhY2tldC5mcm9tXSA9IHsKICAgICAgaWQ6IHBhY2tldC5mcm9tLAogICAgICBuYW1lOiBwYWNrZXQuZnJvbU5hbWUgfHwgcGFja2V0LmZyb20sCiAgICAgIHRyYW5zcG9ydDogcGFja2V0LnNlbmRlcklwID8gJ1dpLUZpJyA6ICdCTEUnLAogICAgICBpcDogcGFja2V0LnNlbmRlcklwIHx8ICcnLAogICAgICBsYXN0U2VlbjogRGF0ZS5ub3coKSwKICAgIH07CiAgICBzY2hlZHVsZVJlbmRlck5lYXJieVBlZXJzKCk7CgogICAgLy8gMS4g0J7QsdGA0LDQsdC+0YLQutCwINC/0L7QtNGC0LLQtdGA0LbQtNC10L3QuNGPINC00L7RgdGC0LDQstC60LggKEFDSykKICAgIGlmIChwYWNrZXQudHlwZSA9PT0gJ1NZUEhfQUNLJyAmJiBwYWNrZXQudGFyZ2V0TXNnSWQpIHsKICAgICAgc3RhdGUuY2hhdHMuZm9yRWFjaChmdW5jdGlvbiAoY2hhdCkgewogICAgICAgIGNoYXQubWVzc2FnZXMuZm9yRWFjaChmdW5jdGlvbiAobXNnKSB7CiAgICAgICAgICBpZiAobXNnLmlkID09PSBwYWNrZXQudGFyZ2V0TXNnSWQgJiYgbXNnLmlzT3V0Z29pbmcpIHsKICAgICAgICAgICAgbXNnLmRlbGl2ZXJlZCA9IHRydWU7CiAgICAgICAgICB9CiAgICAgICAgfSk7CiAgICAgIH0pOwogICAgICBzYXZlQ2hhdHMoKTsKICAgICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgICByZXR1cm47CiAgICB9CgogICAgdmFyIGlzRm9yVXMgPSBwYWNrZXQudG8gPT09IHN0YXRlLmlkZW50aXR5Lm15SWQ7CiAgICB2YXIgaXNCcm9hZGNhc3QgPSBwYWNrZXQudG8gPT09ICdicm9hZGNhc3QnIHx8ICFwYWNrZXQudG87CgogICAgaWYgKCFpc0ZvclVzICYmICFpc0Jyb2FkY2FzdCkgcmV0dXJuOyAvLyDQn9Cw0LrQtdGCINC00LvRjyDQtNGA0YPQs9C+0LPQviDRg9C30LvQsAoKICAgIC8vIDIuINCe0LHRgNCw0LHQvtGC0LrQsCDRgtC10LrRgdGC0L7QstC+0LPQviDRgdC+0L7QsdGJ0LXQvdC40Y8g0LjQu9C4INGE0LDQudC70LAKICAgIGlmIChwYWNrZXQudHlwZSA9PT0gJ1NZUEhfTVNHJyB8fCBwYWNrZXQudHlwZSA9PT0gJ1NZUEhfRklMRScpIHsKICAgICAgdmFyIHRhcmdldENoYXRJZCA9IGlzQnJvYWRjYXN0ID8gJ21lc2gtYnJvYWRjYXN0JyA6IHBhY2tldC5mcm9tOwogICAgICB2YXIgY2hhdCA9IHN0YXRlLmNoYXRzLmZpbmQoZnVuY3Rpb24gKGMpIHsKICAgICAgICByZXR1cm4gYy5pZCA9PT0gdGFyZ2V0Q2hhdElkOwogICAgICB9KTsKCiAgICAgIGlmICghY2hhdCkgewogICAgICAgIGNoYXQgPSB7CiAgICAgICAgICBpZDogcGFja2V0LmZyb20sCiAgICAgICAgICBuYW1lOiBwYWNrZXQuZnJvbU5hbWUgfHwgcGFja2V0LmZyb20sCiAgICAgICAgICBhdmF0YXI6IChwYWNrZXQuZnJvbU5hbWUgfHwgcGFja2V0LmZyb20pLnJlcGxhY2UoJ3N5cC0nLCAnJykuY2hhckF0KDApLnRvVXBwZXJDYXNlKCksCiAgICAgICAgICBzdGF0dXM6ICfQn9GA0Y/QvNC+0Lkg0LfQsNGI0LjRhNGA0L7QstCw0L3QvdGL0Lkg0LrQsNC90LDQuycsCiAgICAgICAgICBsYXN0TWVzc2FnZTogcGFja2V0LnR5cGUgPT09ICdTWVBIX0ZJTEUnID8gJ9CS0LvQvtC20LXQvdC40LU6ICcgKyBwYWNrZXQuZmlsZU5hbWUgOiBwYWNrZXQudGV4dCwKICAgICAgICAgIHRpbWU6IGZvcm1hdFRpbWUobmV3IERhdGUoKSksCiAgICAgICAgICB1bnJlYWQ6IDAsCiAgICAgICAgICBtZXNzYWdlczogW10sCiAgICAgICAgfTsKICAgICAgICBzdGF0ZS5jaGF0cy5wdXNoKGNoYXQpOwogICAgICB9CgogICAgICB2YXIgbXNnVGltZSA9IHBhY2tldC50aW1lc3RhbXAgPyBmb3JtYXRUaW1lKG5ldyBEYXRlKHBhY2tldC50aW1lc3RhbXApKSA6IGZvcm1hdFRpbWUobmV3IERhdGUoKSk7CgogICAgICB2YXIgaW5jb21pbmdNc2cgPSB7CiAgICAgICAgaWQ6IHBhY2tldC5pZCB8fCAncGt0XycgKyBEYXRlLm5vdygpLAogICAgICAgIHNlbmRlcjogcGFja2V0LmZyb20sCiAgICAgICAgYXV0aG9yTmFtZTogcGFja2V0LmZyb21OYW1lIHx8IHBhY2tldC5mcm9tLnN1YnN0cmluZygwLCAxMCksCiAgICAgICAgdHlwZTogcGFja2V0LnR5cGUgPT09ICdTWVBIX0ZJTEUnID8gJ2ZpbGUnIDogJ3RleHQnLAogICAgICAgIHRleHQ6IHBhY2tldC50ZXh0IHx8ICcnLAogICAgICAgIGZpbGVOYW1lOiBwYWNrZXQuZmlsZU5hbWUsCiAgICAgICAgZmlsZVNpemU6IHBhY2tldC5maWxlU2l6ZSwKICAgICAgICBmaWxlVHlwZTogcGFja2V0LmZpbGVUeXBlLAogICAgICAgIGZpbGVEYXRhOiBwYWNrZXQuZmlsZURhdGEsCiAgICAgICAgdGltZTogbXNnVGltZSwKICAgICAgICBpc091dGdvaW5nOiBmYWxzZSwKICAgICAgfTsKCiAgICAgIGNoYXQubWVzc2FnZXMucHVzaChpbmNvbWluZ01zZyk7CiAgICAgIGNoYXQubGFzdE1lc3NhZ2UgPSBwYWNrZXQudHlwZSA9PT0gJ1NZUEhfRklMRScgPyAn0JLQu9C+0LbQtdC90LjQtTogJyArIHBhY2tldC5maWxlTmFtZSA6IHBhY2tldC50ZXh0OwogICAgICBjaGF0LnRpbWUgPSBtc2dUaW1lOwoKICAgICAgaWYgKHN0YXRlLmFjdGl2ZUNoYXRJZCAhPT0gY2hhdC5pZCkgewogICAgICAgIGNoYXQudW5yZWFkID0gKGNoYXQudW5yZWFkIHx8IDApICsgMTsKICAgICAgICBzaG93VG9hc3QoJ9Cd0L7QstGL0Lkg0L/QsNC60LXRgiDQvtGCICcgKyAocGFja2V0LmZyb21OYW1lIHx8IHBhY2tldC5mcm9tKSk7CiAgICAgIH0KCiAgICAgIHNhdmVDaGF0cygpOwogICAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgICBpZiAoc3RhdGUuYWN0aXZlQ2hhdElkID09PSBjaGF0LmlkKSB7CiAgICAgICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwogICAgICB9CgogICAgICAvLyDQldGB0LvQuCDRgdC+0L7QsdGJ0LXQvdC40LUg0LDQtNGA0LXRgdC+0LLQsNC90L4g0L/QtdGA0YHQvtC90LDQu9GM0L3QviDQvdCw0Lwg4oCUINC+0YLQv9GA0LDQstC70Y/QtdC8INC/0L7QtNGC0LLQtdGA0LbQtNC10L3QuNC1INC00L7RgdGC0LDQstC60LggKEFDSykKICAgICAgaWYgKGlzRm9yVXMpIHsKICAgICAgICB2YXIgYWNrUGFja2V0ID0gewogICAgICAgICAgdHlwZTogJ1NZUEhfQUNLJywKICAgICAgICAgIGlkOiAnYWNrXycgKyBEYXRlLm5vdygpLAogICAgICAgICAgZnJvbTogc3RhdGUuaWRlbnRpdHkubXlJZCwKICAgICAgICAgIHRvOiBwYWNrZXQuZnJvbSwKICAgICAgICAgIHRhcmdldE1zZ0lkOiBwYWNrZXQuaWQsCiAgICAgICAgICB0aW1lc3RhbXA6IERhdGUubm93KCksCiAgICAgICAgfTsKICAgICAgICBkaXNwYXRjaFBhY2tldFRvTmV0d29yayhhY2tQYWNrZXQpOwogICAgICB9CiAgICB9CiAgfQoKICAvLyA9PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CiAgLy8g0J7QotCf0KDQkNCS0JrQkCDQodCe0J7QkdCp0JXQndCY0Jkg0Jgg0JLQm9Ce0JbQldCd0JjQmQogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiBzZW5kTWVzc2FnZSgpIHsKICAgIHZhciBpbnB1dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdtZXNzYWdlSW5wdXQnKTsKICAgIGlmICghaW5wdXQpIHJldHVybjsKCiAgICB2YXIgdGV4dCA9IGlucHV0LnZhbHVlLnRyaW0oKTsKICAgIGlmICghdGV4dCkgcmV0dXJuOwoKICAgIHZhciBjaGF0ID0gc3RhdGUuY2hhdHMuZmluZChmdW5jdGlvbiAoYykgewogICAgICByZXR1cm4gYy5pZCA9PT0gc3RhdGUuYWN0aXZlQ2hhdElkOwogICAgfSk7CiAgICBpZiAoIWNoYXQpIHJldHVybjsKCiAgICB2YXIgbm93ID0gbmV3IERhdGUoKTsKICAgIHZhciB0aW1lU3RyID0gZm9ybWF0VGltZShub3cpOwoKICAgIHZhciBwYWNrZXQgPSB7CiAgICAgIHR5cGU6ICdTWVBIX01TRycsCiAgICAgIGlkOiAncGt0XycgKyBEYXRlLm5vdygpICsgJ18nICsgTWF0aC5yYW5kb20oKS50b1N0cmluZygzNikuc3Vic3RyKDIsIDYpLAogICAgICBmcm9tOiBzdGF0ZS5pZGVudGl0eS5teUlkLAogICAgICBmcm9tTmFtZTogc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUsCiAgICAgIHRvOiBjaGF0LmlkID09PSAnbWVzaC1icm9hZGNhc3QnID8gJ2Jyb2FkY2FzdCcgOiBjaGF0LmlkLAogICAgICB0ZXh0OiB0ZXh0LAogICAgICB0aW1lc3RhbXA6IG5vdy5nZXRUaW1lKCksCiAgICB9OwoKICAgIHZhciBuZXdNc2cgPSB7CiAgICAgIGlkOiBwYWNrZXQuaWQsCiAgICAgIHNlbmRlcjogJ21lJywKICAgICAgYXV0aG9yTmFtZTogc3RhdGUuaWRlbnRpdHkuZGlzcGxheU5hbWUsCiAgICAgIHR5cGU6ICd0ZXh0JywKICAgICAgdGV4dDogdGV4dCwKICAgICAgdGltZTogdGltZVN0ciwKICAgICAgaXNPdXRnb2luZzogdHJ1ZSwKICAgICAgZGVsaXZlcmVkOiBmYWxzZSwKICAgIH07CgogICAgY2hhdC5tZXNzYWdlcy5wdXNoKG5ld01zZyk7CiAgICBjaGF0Lmxhc3RNZXNzYWdlID0gdGV4dDsKICAgIGNoYXQudGltZSA9IHRpbWVTdHI7CgogICAgaW5wdXQudmFsdWUgPSAnJzsKICAgIGlucHV0LmZvY3VzKCk7CgogICAgc2F2ZUNoYXRzKCk7CiAgICByZW5kZXJDaGF0TGlzdCgpOwogICAgcmVuZGVyQWN0aXZlQ2hhdCgpOwoKICAgIGRpc3BhdGNoUGFja2V0VG9OZXR3b3JrKHBhY2tldCk7CiAgICBzaG93VG9hc3QoJ9Ch0L7QvtCx0YnQtdC90LjQtSDQvtGC0L/RgNCw0LLQu9C10L3QviDQsiDRgNCw0LTQuNC+0LrQsNC90LDQuycpOwogIH0KCiAgZnVuY3Rpb24gaGFuZGxlRmlsZVNlbGVjdGVkKGUpIHsKICAgIHZhciBmaWxlID0gZS50YXJnZXQuZmlsZXMgJiYgZS50YXJnZXQuZmlsZXNbMF07CiAgICBpZiAoIWZpbGUpIHJldHVybjsKCiAgICBpZiAoZmlsZS5zaXplID4gOCAqIDEwMjQgKiAxMDI0KSB7CiAgICAgIGFsZXJ0KCfQoNCw0LfQvNC10YAg0YTQsNC50LvQsCDQv9GA0LXQstGL0YjQsNC10YIgOCDQnNCRLiDQlNC70Y8g0YDQsNC00LjQvtC/0LXRgNC10LTQsNGH0Lgg0LLRi9Cx0LXRgNC40YLQtSDRhNCw0LnQuyDQvNC10L3RjNGI0LXQs9C+INGA0LDQt9C80LXRgNCwLicpOwogICAgICBlLnRhcmdldC52YWx1ZSA9ICcnOwogICAgICByZXR1cm47CiAgICB9CgogICAgc2hvd1RvYXN0KCfQp9GC0LXQvdC40LUg0YTQsNC50LvQsDogJyArIGZpbGUubmFtZSk7CgogICAgdmFyIHJlYWRlciA9IG5ldyBGaWxlUmVhZGVyKCk7CiAgICByZWFkZXIub25sb2FkID0gZnVuY3Rpb24gKGV2dCkgewogICAgICB2YXIgZGF0YVVybCA9IGV2dC50YXJnZXQucmVzdWx0OwogICAgICB2YXIgY2hhdCA9IHN0YXRlLmNoYXRzLmZpbmQoZnVuY3Rpb24gKGMpIHsKICAgICAgICByZXR1cm4gYy5pZCA9PT0gc3RhdGUuYWN0aXZlQ2hhdElkOwogICAgICB9KTsKICAgICAgaWYgKCFjaGF0KSByZXR1cm47CgogICAgICB2YXIgbm93ID0gbmV3IERhdGUoKTsKICAgICAgdmFyIHRpbWVTdHIgPSBmb3JtYXRUaW1lKG5vdyk7CgogICAgICB2YXIgcGFja2V0ID0gewogICAgICAgIHR5cGU6ICdTWVBIX0ZJTEUnLAogICAgICAgIGlkOiAnZmlsZV8nICsgRGF0ZS5ub3coKSArICdfJyArIE1hdGgucmFuZG9tKCkudG9TdHJpbmcoMzYpLnN1YnN0cigyLCA2KSwKICAgICAgICBmcm9tOiBzdGF0ZS5pZGVudGl0eS5teUlkLAogICAgICAgIGZyb21OYW1lOiBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSwKICAgICAgICB0bzogY2hhdC5pZCA9PT0gJ21lc2gtYnJvYWRjYXN0JyA/ICdicm9hZGNhc3QnIDogY2hhdC5pZCwKICAgICAgICBmaWxlTmFtZTogZmlsZS5uYW1lLAogICAgICAgIGZpbGVTaXplOiBmaWxlLnNpemUsCiAgICAgICAgZmlsZVR5cGU6IGZpbGUudHlwZSwKICAgICAgICBmaWxlRGF0YTogZGF0YVVybCwKICAgICAgICB0aW1lc3RhbXA6IG5vdy5nZXRUaW1lKCksCiAgICAgIH07CgogICAgICB2YXIgbmV3TXNnID0gewogICAgICAgIGlkOiBwYWNrZXQuaWQsCiAgICAgICAgc2VuZGVyOiAnbWUnLAogICAgICAgIGF1dGhvck5hbWU6IHN0YXRlLmlkZW50aXR5LmRpc3BsYXlOYW1lLAogICAgICAgIHR5cGU6ICdmaWxlJywKICAgICAgICBmaWxlTmFtZTogZmlsZS5uYW1lLAogICAgICAgIGZpbGVTaXplOiBmaWxlLnNpemUsCiAgICAgICAgZmlsZVR5cGU6IGZpbGUudHlwZSwKICAgICAgICBmaWxlRGF0YTogZGF0YVVybCwKICAgICAgICB0aW1lOiB0aW1lU3RyLAogICAgICAgIGlzT3V0Z29pbmc6IHRydWUsCiAgICAgICAgZGVsaXZlcmVkOiBmYWxzZSwKICAgICAgfTsKCiAgICAgIGNoYXQubWVzc2FnZXMucHVzaChuZXdNc2cpOwogICAgICBjaGF0Lmxhc3RNZXNzYWdlID0gJ9Ck0LDQudC7OiAnICsgZmlsZS5uYW1lOwogICAgICBjaGF0LnRpbWUgPSB0aW1lU3RyOwoKICAgICAgc2F2ZUNoYXRzKCk7CiAgICAgIHJlbmRlckNoYXRMaXN0KCk7CiAgICAgIHJlbmRlckFjdGl2ZUNoYXQoKTsKCiAgICAgIGRpc3BhdGNoUGFja2V0VG9OZXR3b3JrKHBhY2tldCk7CiAgICAgIHNob3dUb2FzdCgn0KTQsNC50Lsg0L7RgtC/0YDQsNCy0LvQtdC9INCyINGA0LDQtNC40L7QutCw0L3QsNC7Jyk7CiAgICAgIGUudGFyZ2V0LnZhbHVlID0gJyc7CiAgICB9OwoKICAgIHJlYWRlci5vbmVycm9yID0gZnVuY3Rpb24gKCkgewogICAgICBhbGVydCgn0J7RiNC40LHQutCwINC/0YDQuCDRh9GC0LXQvdC40Lgg0YTQsNC50LvQsCcpOwogICAgfTsKCiAgICByZWFkZXIucmVhZEFzRGF0YVVSTChmaWxlKTsKICB9CgogIGZ1bmN0aW9uIGNvcHlUb0NsaXBib2FyZCh0ZXh0LCBzdWNjZXNzTXNnKSB7CiAgICBpZiAobmF2aWdhdG9yLmNsaXBib2FyZCAmJiBuYXZpZ2F0b3IuY2xpcGJvYXJkLndyaXRlVGV4dCkgewogICAgICBuYXZpZ2F0b3IuY2xpcGJvYXJkLndyaXRlVGV4dCh0ZXh0KS50aGVuKGZ1bmN0aW9uICgpIHsKICAgICAgICBzaG93VG9hc3Qoc3VjY2Vzc01zZyk7CiAgICAgIH0pOwogICAgfSBlbHNlIHsKICAgICAgc2hvd1RvYXN0KHN1Y2Nlc3NNc2cpOwogICAgfQogIH0KCiAgLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PQogIC8vINCh0JvQo9Co0JDQotCV0JvQmCDQodCe0JHQq9Ci0JjQmQogIC8vID09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0KICBmdW5jdGlvbiBzZXR1cEV2ZW50TGlzdGVuZXJzKCkgewogICAgLy8gMS4g0J7RgtC/0YDQsNCy0LrQsCDRgdC+0L7QsdGJ0LXQvdC40LkKICAgIHZhciBzZW5kQnRuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blNlbmQnKTsKICAgIGlmIChzZW5kQnRuKSB7CiAgICAgIHNlbmRCdG4ub25jbGljayA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgZS5wcmV2ZW50RGVmYXVsdCgpOwogICAgICAgIHNlbmRNZXNzYWdlKCk7CiAgICAgIH07CiAgICB9CgogICAgdmFyIG1zZ0lucHV0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ21lc3NhZ2VJbnB1dCcpOwogICAgaWYgKG1zZ0lucHV0KSB7CiAgICAgIG1zZ0lucHV0Lm9ua2V5ZG93biA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgaWYgKGUua2V5ID09PSAnRW50ZXInICYmICFlLnNoaWZ0S2V5KSB7CiAgICAgICAgICBlLnByZXZlbnREZWZhdWx0KCk7CiAgICAgICAgICBzZW5kTWVzc2FnZSgpOwogICAgICAgIH0KICAgICAgfTsKICAgIH0KCiAgICAvLyAyLiDQn9GA0LjQutGA0LXQv9C70LXQvdC40LUg0YTQsNC50LvQvtCyCiAgICB2YXIgYXR0YWNoQnRuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bkF0dGFjaCcpOwogICAgdmFyIGZpbGVJbnB1dCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdmaWxlSW5wdXQnKTsKICAgIGlmIChhdHRhY2hCdG4gJiYgZmlsZUlucHV0KSB7CiAgICAgIGF0dGFjaEJ0bi5vbmNsaWNrID0gZnVuY3Rpb24gKGUpIHsKICAgICAgICBlLnByZXZlbnREZWZhdWx0KCk7CiAgICAgICAgZmlsZUlucHV0LmNsaWNrKCk7CiAgICAgIH07CiAgICAgIGZpbGVJbnB1dC5vbmNoYW5nZSA9IGhhbmRsZUZpbGVTZWxlY3RlZDsKICAgIH0KCiAgICAvLyAzLiDQndCw0LLQuNCz0LDRhtC40Y8g0L3QsCDRgdC80LDRgNGC0YTQvtC90LDRhQogICAgdmFyIGJhY2tCdG4gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQmFja1RvQ2hhdHMnKTsKICAgIGlmIChiYWNrQnRuKSB7CiAgICAgIGJhY2tCdG4ub25jbGljayA9IGZ1bmN0aW9uIChlKSB7CiAgICAgICAgZS5wcmV2ZW50RGVmYXVsdCgpOwogICAgICAgIHZhciBsYXlvdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYXBwTGF5b3V0Jyk7CiAgICAgICAgaWYgKGxheW91dCkgbGF5b3V0LmNsYXNzTGlzdC5yZW1vdmUoJ21vYmlsZS1jaGF0LW9wZW4nKTsKICAgICAgfTsKICAgIH0KCiAgICAvLyA0LiDQmtC+0L/QuNGA0L7QstCw0L3QuNC1IElECiAgICB2YXIgbXlOb2RlQ2FyZCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdteU5vZGVDYXJkJyk7CiAgICB2YXIgY29weUJ0biA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5Db3B5TXlJZCcpOwogICAgZnVuY3Rpb24gY29weUlkKGUpIHsKICAgICAgaWYgKGUpIGUuc3RvcFByb3BhZ2F0aW9uKCk7CiAgICAgIGNvcHlUb0NsaXBib2FyZChzdGF0ZS5pZGVudGl0eS5teUlkLCAn0JLQsNGIIFN5cGhlciBJRCDRgdC60L7Qv9C40YDQvtCy0LDQvScpOwogICAgfQogICAgaWYgKG15Tm9kZUNhcmQpIG15Tm9kZUNhcmQub25jbGljayA9IGNvcHlJZDsKICAgIGlmIChjb3B5QnRuKSBjb3B5QnRuLm9uY2xpY2sgPSBjb3B5SWQ7CgogICAgLy8gNS4g0J/QvtC40YHQugogICAgdmFyIHNlYXJjaElucHV0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NlYXJjaElucHV0Jyk7CiAgICBpZiAoc2VhcmNoSW5wdXQpIHsKICAgICAgc2VhcmNoSW5wdXQub25pbnB1dCA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBzdGF0ZS5zZWFyY2hRdWVyeSA9IHNlYXJjaElucHV0LnZhbHVlOwogICAgICAgIHJlbmRlckNoYXRMaXN0KCk7CiAgICAgIH07CiAgICB9CgogICAgLy8gNi4g0JzQvtC00LDQu9GM0L3QvtC1INC+0LrQvdC+INC90L7QstC+0LPQviDQtNC40LDQu9C+0LPQsAogICAgdmFyIG5ld0NoYXRNb2RhbCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCduZXdDaGF0TW9kYWwnKTsKICAgIHZhciBidG5OZXdDaGF0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bk5ld0NoYXQnKTsKICAgIHZhciBidG5DbG9zZU5ld0NoYXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VOZXdDaGF0Jyk7CiAgICB2YXIgYnRuQ2FuY2VsTmV3Q2hhdCA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5DYW5jZWxOZXdDaGF0Jyk7CiAgICB2YXIgYnRuQ29uZmlybU5ld0NoYXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ29uZmlybU5ld0NoYXQnKTsKICAgIHZhciBuZXdDaGF0SW5wdXQgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbmV3Q2hhdFBlZXJJZElucHV0Jyk7CgogICAgaWYgKGJ0bk5ld0NoYXQgJiYgbmV3Q2hhdE1vZGFsKSB7CiAgICAgIGJ0bk5ld0NoYXQub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBpZiAobmV3Q2hhdElucHV0KSBuZXdDaGF0SW5wdXQudmFsdWUgPSAnJzsKICAgICAgICByZW5kZXJOZWFyYnlQZWVycygpOwogICAgICAgIG5ld0NoYXRNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlTmV3Q2hhdCgpIHsKICAgICAgaWYgKG5ld0NoYXRNb2RhbCkgbmV3Q2hhdE1vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlTmV3Q2hhdCkgYnRuQ2xvc2VOZXdDaGF0Lm9uY2xpY2sgPSBjbG9zZU5ld0NoYXQ7CiAgICBpZiAoYnRuQ2FuY2VsTmV3Q2hhdCkgYnRuQ2FuY2VsTmV3Q2hhdC5vbmNsaWNrID0gY2xvc2VOZXdDaGF0OwoKICAgIGlmIChidG5Db25maXJtTmV3Q2hhdCAmJiBuZXdDaGF0SW5wdXQpIHsKICAgICAgYnRuQ29uZmlybU5ld0NoYXQub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICB2YXIgaWQgPSBuZXdDaGF0SW5wdXQudmFsdWUudHJpbSgpOwogICAgICAgIGlmICghaWQpIHJldHVybjsKICAgICAgICBjbG9zZU5ld0NoYXQoKTsKICAgICAgICBzdGFydERpcmVjdENoYXRXaXRoUGVlcihpZCk7CiAgICAgIH07CiAgICB9CgogICAgLy8gNy4g0JzQvtC00LDQu9GM0L3QvtC1INC+0LrQvdC+INC90LDRgdGC0YDQvtC10LoKICAgIHZhciBzZXR0aW5nc01vZGFsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NldHRpbmdzTW9kYWwnKTsKICAgIHZhciBidG5TZXR0aW5ncyA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5TZXR0aW5ncycpOwogICAgdmFyIGJ0bkNsb3NlU2V0dGluZ3MgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VTZXR0aW5ncycpOwogICAgdmFyIGJ0blNhdmVTZXR0aW5ncyA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5TYXZlU2V0dGluZ3MnKTsKICAgIHZhciBpbnB1dE5hbWUgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnaW5wdXREaXNwbGF5TmFtZScpOwogICAgdmFyIGJ0blJlZ2VuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blJlZ2VuZXJhdGVJZCcpOwoKICAgIGlmIChidG5TZXR0aW5ncyAmJiBzZXR0aW5nc01vZGFsKSB7CiAgICAgIGJ0blNldHRpbmdzLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgc2V0dGluZ3NNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlU2V0dGluZ3MoKSB7CiAgICAgIGlmIChzZXR0aW5nc01vZGFsKSBzZXR0aW5nc01vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlU2V0dGluZ3MpIGJ0bkNsb3NlU2V0dGluZ3Mub25jbGljayA9IGNsb3NlU2V0dGluZ3M7CgogICAgaWYgKGJ0blNhdmVTZXR0aW5ncyAmJiBpbnB1dE5hbWUpIHsKICAgICAgYnRuU2F2ZVNldHRpbmdzLm9uY2xpY2sgPSBmdW5jdGlvbiAoKSB7CiAgICAgICAgdmFyIG5ld05hbWUgPSBpbnB1dE5hbWUudmFsdWUudHJpbSgpOwogICAgICAgIGlmIChuZXdOYW1lKSB7CiAgICAgICAgICBzdGF0ZS5pZGVudGl0eS5kaXNwbGF5TmFtZSA9IG5ld05hbWU7CiAgICAgICAgICBsb2NhbFN0b3JhZ2Uuc2V0SXRlbSgnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnLCBKU09OLnN0cmluZ2lmeShzdGF0ZS5pZGVudGl0eSkpOwogICAgICAgICAgdmFyIG15TmFtZUVsZW0gPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnbXlOb2RlRGlzcGxheU5hbWUnKTsKICAgICAgICAgIGlmIChteU5hbWVFbGVtKSBteU5hbWVFbGVtLnRleHRDb250ZW50ID0gbmV3TmFtZTsKICAgICAgICB9CiAgICAgICAgY2xvc2VTZXR0aW5ncygpOwogICAgICAgIHNob3dUb2FzdCgn0J3QsNGB0YLRgNC+0LnQutC4INGB0L7RhdGA0LDQvdC10L3RiycpOwogICAgICB9OwogICAgfQoKICAgIGlmIChidG5SZWdlbikgewogICAgICBidG5SZWdlbi5vbmNsaWNrID0gZnVuY3Rpb24gKCkgewogICAgICAgIGlmIChjb25maXJtKCfQodCz0LXQvdC10YDQuNGA0L7QstCw0YLRjCDQsNCx0YHQvtC70Y7RgtC90L4g0L3QvtCy0YvQuSDRg9C90LjQutCw0LvRjNC90YvQuSBTeXBoZXIgSUQ/INCS0YHQtSDRgtC10LrRg9GJ0LjQtSDQutC+0L3RgtCw0LrRgtGLINC00L7Qu9C20L3RiyDQsdGD0LTRg9GCINC40YHQv9C+0LvRjNC30L7QstCw0YLRjCDQvdC+0LLRi9C5IElELicpKSB7CiAgICAgICAgICBsb2NhbFN0b3JhZ2UucmVtb3ZlSXRlbSgnc3lwaGVyX25vZGVfaWRlbnRpdHlfdjMnKTsKICAgICAgICAgIHN0YXRlLmlkZW50aXR5ID0gZ2V0T3JDcmVhdGVJZGVudGl0eSgpOwogICAgICAgICAgaW5pdFVJKCk7CiAgICAgICAgICBjbG9zZVNldHRpbmdzKCk7CiAgICAgICAgICBzaG93VG9hc3QoJ9Cd0L7QstGL0Lkg0YPQvdC40LrQsNC70YzQvdGL0LkgSUQg0YPQt9C70LAg0YHQs9C10L3QtdGA0LjRgNC+0LLQsNC9Jyk7CiAgICAgICAgfQogICAgICB9OwogICAgfQoKICAgIC8vIDguINCc0L7QtNCw0LvRjNC90L7QtSDQvtC60L3QviDRgdCy0LXRgNC60Lgg0L7RgtC/0LXRh9Cw0YLQutCwINC60LvRjtGH0LXQuQogICAgdmFyIHZlcmlmeU1vZGFsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3ZlcmlmeUtleXNNb2RhbCcpOwogICAgdmFyIGJ0blZlcmlmeSA9IGRvY3VtZW50LmdldEVsZW1lbnRCeUlkKCdidG5WZXJpZnlLZXlzJyk7CiAgICB2YXIgYnRuQ2xvc2VWZXJpZnkgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuQ2xvc2VWZXJpZnlLZXlzJyk7CiAgICB2YXIgYnRuT2tWZXJpZnkgPSBkb2N1bWVudC5nZXRFbGVtZW50QnlJZCgnYnRuT2tWZXJpZnlLZXlzJyk7CgogICAgaWYgKGJ0blZlcmlmeSAmJiB2ZXJpZnlNb2RhbCkgewogICAgICBidG5WZXJpZnkub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICB2ZXJpZnlNb2RhbC5jbGFzc0xpc3QucmVtb3ZlKCdoaWRkZW4nKTsKICAgICAgfTsKICAgIH0KICAgIGZ1bmN0aW9uIGNsb3NlVmVyaWZ5KCkgewogICAgICBpZiAodmVyaWZ5TW9kYWwpIHZlcmlmeU1vZGFsLmNsYXNzTGlzdC5hZGQoJ2hpZGRlbicpOwogICAgfQogICAgaWYgKGJ0bkNsb3NlVmVyaWZ5KSBidG5DbG9zZVZlcmlmeS5vbmNsaWNrID0gY2xvc2VWZXJpZnk7CiAgICBpZiAoYnRuT2tWZXJpZnkpIGJ0bk9rVmVyaWZ5Lm9uY2xpY2sgPSBjbG9zZVZlcmlmeTsKCiAgICAvLyA5LiDQotCw0LnQvNC10YAg0YHQsNC80L7Rg9C90LjRh9GC0L7QttC10L3QuNGPCiAgICB2YXIgYnRuU2VsZkRlc3RydWN0ID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0blNlbGZEZXN0cnVjdCcpOwogICAgdmFyIHNlbGZEZXN0cnVjdExhYmVsID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ3NlbGZEZXN0cnVjdExhYmVsJyk7CiAgICBpZiAoYnRuU2VsZkRlc3RydWN0ICYmIHNlbGZEZXN0cnVjdExhYmVsKSB7CiAgICAgIGJ0blNlbGZEZXN0cnVjdC5vbmNsaWNrID0gZnVuY3Rpb24gKCkgewogICAgICAgIHN0YXRlLnNlbGZEZXN0cnVjdEluZGV4ID0gKHN0YXRlLnNlbGZEZXN0cnVjdEluZGV4ICsgMSkgJSBzdGF0ZS5zZWxmRGVzdHJ1Y3RPcHRpb25zLmxlbmd0aDsKICAgICAgICB2YXIgb3B0ID0gc3RhdGUuc2VsZkRlc3RydWN0T3B0aW9uc1tzdGF0ZS5zZWxmRGVzdHJ1Y3RJbmRleF07CiAgICAgICAgc2VsZkRlc3RydWN0TGFiZWwudGV4dENvbnRlbnQgPSBvcHQ7CiAgICAgICAgc2hvd1RvYXN0KCfQotCw0LnQvNC10YAg0YHQsNC80L7Rg9C90LjRh9GC0L7QttC10L3QuNGPOiAnICsgb3B0KTsKICAgICAgfTsKICAgIH0KCiAgICAvLyAxMC4g0K3QutGB0YLRgNC10L3QvdC+0LUg0YHRgtC40YDQsNC90LjQtSDQstGB0LXRhSDQtNCw0L3QvdGL0YUKICAgIHZhciBidG5CdXJuID0gZG9jdW1lbnQuZ2V0RWxlbWVudEJ5SWQoJ2J0bkVtZXJnZW5jeUJ1cm4nKTsKICAgIGlmIChidG5CdXJuKSB7CiAgICAgIGJ0bkJ1cm4ub25jbGljayA9IGZ1bmN0aW9uICgpIHsKICAgICAgICBpZiAoCiAgICAgICAgICBjb25maXJtKAogICAgICAgICAgICAn0JLQndCY0JzQkNCd0JjQlTog0JLRi9C/0L7Qu9C90LjRgtGMINGN0LrRgdGC0YDQtdC90L3QvtC1INGB0YLQuNGA0LDQvdC40LUg0LLRgdC10YUg0LrQu9GO0YfQtdC5LCDQv9C10YDQtdC/0LjRgdC60Lgg0Lgg0LjRgdGC0L7RgNC40Lgg0L3QsCDQtNCw0L3QvdC+0Lwg0YPRgdGC0YDQvtC50YHRgtCy0LU/JwogICAgICAgICAgKQogICAgICAgICkgewogICAgICAgICAgbG9jYWxTdG9yYWdlLmNsZWFyKCk7CiAgICAgICAgICBzaG93VG9hc3QoJ9CS0YHQtSDQtNCw0L3QvdGL0LUg0YHRgtGR0YDRgtGLJyk7CiAgICAgICAgICBzZXRUaW1lb3V0KGZ1bmN0aW9uICgpIHsKICAgICAgICAgICAgbG9jYXRpb24ucmVsb2FkKCk7CiAgICAgICAgICB9LCA2MDApOwogICAgICAgIH0KICAgICAgfTsKICAgIH0KICB9CgogIC8vINCX0LDQv9GD0YHQuiDQv9GA0LjQu9C+0LbQtdC90LjRjwogIGlmIChkb2N1bWVudC5yZWFkeVN0YXRlID09PSAnbG9hZGluZycpIHsKICAgIGRvY3VtZW50LmFkZEV2ZW50TGlzdGVuZXIoJ0RPTUNvbnRlbnRMb2FkZWQnLCBpbml0VUkpOwogIH0gZWxzZSB7CiAgICBpbml0VUkoKTsKICB9Cn0pKCk7Cg=="));
                }
                return _js;
            }
        }
    }
}
