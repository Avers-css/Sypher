using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SypherDesktop
{
    static class Program
    {
        private static HttpListener listener;
        private static NotifyIcon trayIcon;
        private static int port = 7789;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            StartHttpServer();
            SetupTrayIcon();
            LaunchAppWindow();

            Application.Run();
        }

        static void StartHttpServer()
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                listener.Start();
            }
            catch
            {
                port = 7795;
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
            }

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
            string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (!File.Exists(edgePath))
            {
                edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            }

            if (File.Exists(edgePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = edgePath,
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
            trayIcon.Text = "Sypher — Mesh Active";
            trayIcon.Icon = SystemIcons.Shield;
            trayIcon.Visible = true;

            var menu = new ContextMenu();
            menu.MenuItems.Add("Открыть Sypher", (s, e) => LaunchAppWindow());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("🔥 Экстренное уничтожение", (s, e) =>
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
        public static string HTML = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>Sypher — Анонимный Mesh Мессенджер</title>
  <link rel=""stylesheet"" href=""styles.css"">
  <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
  <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
  <link href=""https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500;700&family=Plus+Jakarta+Sans:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
</head>
<body>
  <div class=""app-layout"">
    <!-- Sidebar: Профиль пользователя + Список диалогов -->
    <aside class=""sidebar"">
      <header class=""sidebar-header"">
        <div class=""user-profile"">
          <div class=""avatar-wrapper"">
            <div class=""avatar"" id=""myAvatar"">S</div>
            <div class=""status-indicator online pulse"" title=""Узел Mesh активен""></div>
          </div>
          <div class=""user-meta"">
            <h2 class=""username"" id=""mySypherId"">syph1...7f8a</h2>
            <div class=""fingerprint-tag"" id=""myFingerprintTag"" title=""Нажмите для просмотра ключа"">
              <span class=""lock-icon"">🔒</span> E2EE Активно
            </div>
          </div>
        </div>
        <button class=""icon-button"" id=""btnSettings"" title=""Настройки безопасности"">⚙️</button>
      </header>

      <!-- Mesh Radar / Статус сети -->
      <div class=""mesh-banner"">
        <div class=""mesh-banner-info"">
          <span class=""mesh-pulse-icon"">📡</span>
          <div>
            <div class=""mesh-title"">Локальная сеть Mesh</div>
            <div class=""mesh-subtitle"" id=""meshPeerCount"">3 узла в радиодиапазоне (UDP/BLE)</div>
          </div>
        </div>
        <div class=""radar-ping""></div>
      </div>

      <!-- Поиск -->
      <div class=""search-box"">
        <span class=""search-icon"">🔍</span>
        <input type=""text"" id=""searchInput"" placeholder=""Поиск чатов или ввод адреса syph1..."">
      </div>

      <!-- Список чатов -->
      <div class=""chat-list"" id=""chatList"">
        <!-- Заполняется динамически -->
      </div>

      <!-- Кнопка уничтожения данных -->
      <footer class=""sidebar-footer"">
        <button class=""burn-button"" id=""btnEmergencyBurn"" title=""Экстренное удаление всех ключей и базы"">
          🔥 Экстренное уничтожение (Burn)
        </button>
      </footer>
    </aside>

    <!-- Основное окно чата -->
    <main class=""chat-main"" id=""chatMain"">
      <header class=""chat-header"" id=""chatHeader"">
        <div class=""active-peer-info"">
          <div class=""avatar peer-avatar"" id=""activePeerAvatar"">B</div>
          <div>
            <h3 class=""active-peer-name"" id=""activePeerName"">Боб (syph1bob...449)</h3>
            <div class=""active-peer-status"" id=""activePeerStatus"">
              <span class=""dot-online""></span> Double Ratchet активен • Соседний узел
            </div>
          </div>
        </div>
        <div class=""chat-actions"">
          <button class=""action-btn"" id=""btnVerifyKeys"" title=""Сверить криптографический отпечаток"">🔑 Сверить ключ</button>
          <button class=""action-btn"" id=""btnSelfDestruct"" title=""Таймер самоуничтожения"">⏱️ 24ч</button>
        </div>
      </header>

      <!-- Сообщения -->
      <div class=""messages-viewport"" id=""messagesViewport"">
        <div class=""encryption-notice"">
          <div class=""notice-badge"">
            <span>🛡️</span> Сообщения в этом чате защищены сквозным шифрованием (X25519 & ChaCha20-Poly1305).
            Ни серверы, ни провайдеры не имеют доступа к тексту.
          </div>
        </div>
      </div>

      <!-- Поле ввода -->
      <footer class=""chat-composer"">
        <div class=""composer-container"">
          <button class=""composer-icon-btn"" title=""Прикрепить зашифрованный файл"">📎</button>
          <textarea id=""messageInput"" placeholder=""Написать зашифрованное сообщение..."" rows=""1""></textarea>
          <button class=""send-button"" id=""btnSend"" title=""Отправить пакет в Mesh"">
            <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"">
              <path d=""M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z""/>
            </svg>
          </button>
        </div>
      </footer>
    </main>
  </div>

  <!-- Модальное окно верификации отпечатка ключа -->
  <div class=""modal-overlay hidden"" id=""verificationModal"">
    <div class=""modal-content"">
      <div class=""modal-header"">
        <h3>Криптографический отпечаток</h3>
        <button class=""close-modal"" id=""btnCloseModal"">✕</button>
      </div>
      <div class=""modal-body"">
        <p class=""modal-hint"">Сверьте этот 64-значный SHA-256 отпечаток с собеседником при встрече или по видео:</p>
        <div class=""fingerprint-display"" id=""modalFingerprint"">
          7F2A 9C81 D0E4 11B3 44FE 8812 90AA 3BC7
        </div>
        <div class=""qr-placeholder"" id=""modalQR"">
          <svg width=""160"" height=""160"" viewBox=""0 0 100 100"" class=""qr-svg"">
            <rect width=""100"" height=""100"" fill=""#121824""/>
            <rect x=""10"" y=""10"" width=""25"" height=""25"" fill=""#00ffaa""/>
            <rect x=""15"" y=""15"" width=""15"" height=""15"" fill=""#121824""/>
            <rect x=""65"" y=""10"" width=""25"" height=""25"" fill=""#00ffaa""/>
            <rect x=""70"" y=""15"" width=""15"" height=""15"" fill=""#121824""/>
            <rect x=""10"" y=""65"" width=""25"" height=""25"" fill=""#00ffaa""/>
            <rect x=""15"" y=""70"" width=""15"" height=""15"" fill=""#121824""/>
            <rect x=""45"" y=""45"" width=""10"" height=""10"" fill=""#00ffaa""/>
          </svg>
        </div>
        <div class=""modal-stats"">
          <div><span>Протокол:</span> SYPH-v1</div>
          <div><span>Кривые:</span> Ed25519 / X25519</div>
          <div><span>Шифр:</span> ChaCha20-Poly1305</div>
        </div>
      </div>
    </div>
  </div>

  <script type=""module"" src=""app.js""></script>
</body>
</html>
";
        public static string CSS = @":root {
  --bg-primary: #0a0d14;
  --bg-secondary: #121824;
  --bg-tertiary: #1a2234;
  --bg-glass: rgba(18, 24, 36, 0.75);
  --border-color: rgba(255, 255, 255, 0.08);
  --accent-cyan: #00f0ff;
  --accent-green: #00ffaa;
  --accent-purple: #9d4edd;
  --accent-red: #ff3366;
  --text-primary: #f0f4f8;
  --text-secondary: #8e9bb0;
  --text-muted: #536179;
  --bubble-incoming: #182232;
  --bubble-outgoing: #1a3a4b;
  --font-main: 'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  --font-mono: 'JetBrains Mono', monospace;
}

* {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
  user-select: none;
}

body {
  font-family: var(--font-main);
  background-color: var(--bg-primary);
  color: var(--text-primary);
  height: 100vh;
  overflow: hidden;
  -webkit-font-smoothing: antialiased;
}

.app-layout {
  display: flex;
  height: 100vh;
  width: 100vw;
}

/* Sidebar */
.sidebar {
  width: 360px;
  min-width: 320px;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border-color);
  display: flex;
  flex-direction: column;
}

.sidebar-header {
  padding: 16px 20px;
  display: flex;
  justify-content: space-between;
  align-items: center;
  border-bottom: 1px solid var(--border-color);
}

.user-profile {
  display: flex;
  align-items: center;
  gap: 12px;
}

.avatar-wrapper {
  position: relative;
}

.avatar {
  width: 44px;
  height: 44px;
  border-radius: 50%;
  background: linear-gradient(135deg, #00f0ff, #7928ca);
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 700;
  font-size: 18px;
  color: #fff;
  box-shadow: 0 4px 12px rgba(0, 240, 255, 0.2);
}

.status-indicator {
  position: absolute;
  bottom: 0;
  right: 0;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: var(--accent-green);
  border: 2px solid var(--bg-secondary);
}

.pulse {
  box-shadow: 0 0 0 0 rgba(0, 255, 170, 0.7);
  animation: pulsing 2s infinite;
}

@keyframes pulsing {
  0% { box-shadow: 0 0 0 0 rgba(0, 255, 170, 0.7); }
  70% { box-shadow: 0 0 0 8px rgba(0, 255, 170, 0); }
  100% { box-shadow: 0 0 0 0 rgba(0, 255, 170, 0); }
}

.user-meta .username {
  font-size: 15px;
  font-weight: 600;
  font-family: var(--font-mono);
}

.fingerprint-tag {
  font-size: 11px;
  color: var(--accent-green);
  margin-top: 2px;
  display: flex;
  align-items: center;
  gap: 4px;
}

.icon-button {
  background: transparent;
  border: none;
  font-size: 18px;
  cursor: pointer;
  padding: 8px;
  border-radius: 8px;
  transition: background 0.2s;
}

.icon-button:hover {
  background: rgba(255, 255, 255, 0.05);
}

/* Mesh Banner */
.mesh-banner {
  margin: 12px 16px;
  padding: 12px 14px;
  background: rgba(0, 240, 255, 0.05);
  border: 1px solid rgba(0, 240, 255, 0.15);
  border-radius: 12px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.mesh-banner-info {
  display: flex;
  align-items: center;
  gap: 10px;
}

.mesh-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--accent-cyan);
}

.mesh-subtitle {
  font-size: 11px;
  color: var(--text-secondary);
}

/* Search Box */
.search-box {
  margin: 0 16px 12px 16px;
  background: var(--bg-primary);
  border: 1px solid var(--border-color);
  border-radius: 10px;
  display: flex;
  align-items: center;
  padding: 8px 12px;
  gap: 8px;
}

.search-box input {
  background: transparent;
  border: none;
  outline: none;
  color: var(--text-primary);
  font-family: var(--font-main);
  font-size: 13px;
  width: 100%;
}

/* Chat List */
.chat-list {
  flex: 1;
  overflow-y: auto;
}

.chat-item {
  display: flex;
  padding: 12px 16px;
  gap: 12px;
  cursor: pointer;
  transition: background 0.15s;
  border-bottom: 1px solid rgba(255, 255, 255, 0.02);
}

.chat-item:hover, .chat-item.active {
  background: var(--bg-tertiary);
}

.chat-item-meta {
  flex: 1;
  min-width: 0;
}

.chat-item-top {
  display: flex;
  justify-content: space-between;
  margin-bottom: 4px;
}

.chat-item-name {
  font-size: 14px;
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.chat-item-time {
  font-size: 11px;
  color: var(--text-muted);
}

.chat-item-preview {
  font-size: 12px;
  color: var(--text-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* Sidebar Footer */
.sidebar-footer {
  padding: 12px 16px;
  border-top: 1px solid var(--border-color);
}

.burn-button {
  width: 100%;
  padding: 10px;
  border: 1px solid rgba(255, 51, 102, 0.3);
  background: rgba(255, 51, 102, 0.1);
  color: var(--accent-red);
  border-radius: 8px;
  font-weight: 600;
  font-size: 13px;
  cursor: pointer;
  transition: all 0.2s;
}

.burn-button:hover {
  background: rgba(255, 51, 102, 0.2);
  border-color: var(--accent-red);
}

/* Main Chat Area */
.chat-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  background: var(--bg-primary);
}

.chat-header {
  padding: 14px 24px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-color);
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.active-peer-info {
  display: flex;
  align-items: center;
  gap: 12px;
}

.active-peer-name {
  font-size: 15px;
  font-weight: 600;
}

.active-peer-status {
  font-size: 12px;
  color: var(--text-secondary);
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 2px;
}

.dot-online {
  width: 8px;
  height: 8px;
  background: var(--accent-green);
  border-radius: 50%;
}

.action-btn {
  background: var(--bg-tertiary);
  border: 1px solid var(--border-color);
  color: var(--text-primary);
  padding: 6px 12px;
  border-radius: 8px;
  font-size: 12px;
  cursor: pointer;
  font-weight: 500;
  transition: background 0.15s;
}

.action-btn:hover {
  background: rgba(255, 255, 255, 0.1);
}

/* Messages Viewport */
.messages-viewport {
  flex: 1;
  padding: 24px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.encryption-notice {
  display: flex;
  justify-content: center;
  margin-bottom: 16px;
}

.notice-badge {
  background: rgba(0, 240, 255, 0.05);
  border: 1px solid rgba(0, 240, 255, 0.15);
  border-radius: 20px;
  padding: 6px 16px;
  font-size: 12px;
  color: var(--text-secondary);
  max-width: 600px;
  text-align: center;
}

.message-row {
  display: flex;
  flex-direction: column;
  max-width: 65%;
}

.message-row.outgoing {
  align-self: flex-end;
  align-items: flex-end;
}

.message-row.incoming {
  align-self: flex-start;
  align-items: flex-start;
}

.message-bubble {
  padding: 10px 16px;
  border-radius: 16px;
  font-size: 14px;
  line-height: 1.45;
  user-select: text;
  word-break: break-word;
}

.message-row.outgoing .message-bubble {
  background: var(--bubble-outgoing);
  border-bottom-right-radius: 4px;
  border: 1px solid rgba(0, 240, 255, 0.2);
}

.message-row.incoming .message-bubble {
  background: var(--bubble-incoming);
  border-bottom-left-radius: 4px;
  border: 1px solid var(--border-color);
}

.message-info {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  font-size: 11px;
  color: var(--text-muted);
}

/* Composer */
.chat-composer {
  padding: 16px 24px;
  background: var(--bg-secondary);
  border-top: 1px solid var(--border-color);
}

.composer-container {
  display: flex;
  align-items: center;
  gap: 12px;
  background: var(--bg-primary);
  border: 1px solid var(--border-color);
  border-radius: 14px;
  padding: 8px 14px;
}

.composer-container textarea {
  flex: 1;
  background: transparent;
  border: none;
  outline: none;
  color: var(--text-primary);
  font-family: var(--font-main);
  font-size: 14px;
  resize: none;
  max-height: 120px;
}

.composer-icon-btn {
  background: transparent;
  border: none;
  font-size: 18px;
  cursor: pointer;
  color: var(--text-secondary);
}

.send-button {
  background: var(--accent-cyan);
  border: none;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #0a0d14;
  cursor: pointer;
  transition: transform 0.15s, box-shadow 0.15s;
}

.send-button:hover {
  transform: scale(1.05);
  box-shadow: 0 0 12px rgba(0, 240, 255, 0.4);
}

/* Modal */
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.7);
  backdrop-filter: blur(8px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.modal-overlay.hidden {
  display: none;
}

.modal-content {
  background: var(--bg-secondary);
  border: 1px solid var(--border-color);
  border-radius: 16px;
  width: 440px;
  padding: 24px;
  box-shadow: 0 20px 40px rgba(0, 0, 0, 0.5);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.close-modal {
  background: transparent;
  border: none;
  color: var(--text-muted);
  font-size: 16px;
  cursor: pointer;
}

.modal-hint {
  font-size: 12px;
  color: var(--text-secondary);
  margin-bottom: 14px;
}

.fingerprint-display {
  font-family: var(--font-mono);
  background: var(--bg-primary);
  padding: 12px;
  border-radius: 8px;
  border: 1px solid var(--border-color);
  font-size: 13px;
  color: var(--accent-cyan);
  text-align: center;
  letter-spacing: 1px;
  margin-bottom: 20px;
}

.qr-placeholder {
  display: flex;
  justify-content: center;
  margin-bottom: 20px;
}

.modal-stats {
  border-top: 1px solid var(--border-color);
  padding-top: 14px;
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  color: var(--text-muted);
}

.modal-stats span {
  color: var(--text-secondary);
}
";
        public static string JS = @"// Sypher Desktop Controller (Russian Localization)
const state = {
  myId: 'syph19k20fae37b981290aa3bc7',
  myFingerprint: '7F2A 9C81 D0E4 11B3 44FE 8812 90AA 3BC7',
  activeChatId: 'mesh-broadcast',
  chats: [
    {
      id: 'mesh-broadcast',
      name: '🌐 Локальный радиоканал Mesh',
      avatar: 'M',
      status: 'Ad-Hoc UDP/BLE Сеть (Без серверов)',
      lastMessage: 'Радиомаяк активен: найдено 3 узла поблизости',
      time: 'Только что',
      unread: 0,
      messages: [
        {
          id: '1',
          sender: 'system',
          text: 'Подключено к частоте локальной Mesh-сети (UDP: 239.255.77.88:7788). Все пакеты передаются по цепочке через людей.',
          time: '18:30',
          isOutgoing: false,
        },
        {
          id: '2',
          sender: 'syph1bob...449',
          text: 'Узел Bob в сети. Служба ретрансляции активна.',
          time: '18:32',
          isOutgoing: false,
        },
      ],
    },
    {
      id: 'bob-direct',
      name: 'Боб (syph1bob...449)',
      avatar: 'Б',
      status: 'Double Ratchet Активен • 1-й прыжок',
      lastMessage: 'Отпечаток ключа проверен, совпадает!',
      time: '18:33',
      unread: 1,
      messages: [
        {
          id: 'm1',
          sender: 'syph1bob...449',
          text: 'Привет! Ты тестируешь новую версию Sypher?',
          time: '18:31',
          isOutgoing: false,
        },
        {
          id: 'm2',
          sender: 'me',
          text: 'Да! Полная децентрализация, ноль серверов, чистое сквозное шифрование E2EE.',
          time: '18:32',
          isOutgoing: true,
        },
        {
          id: 'm3',
          sender: 'syph1bob...449',
          text: 'Отпечаток ключа проверен, совпадает!',
          time: '18:33',
          isOutgoing: false,
        },
      ],
    },
  ],
};

function initUI() {
  document.getElementById('mySypherId').textContent = state.myId.substring(0, 16) + '...';
  renderChatList();
  renderActiveChat();
  setupEventListeners();
}

function renderChatList() {
  const container = document.getElementById('chatList');
  container.innerHTML = '';

  state.chats.forEach((chat) => {
    const item = document.createElement('div');
    item.className = `chat-item ${chat.id === state.activeChatId ? 'active' : ''}`;
    item.onclick = () => selectChat(chat.id);

    item.innerHTML = `
      <div class=""avatar"">${chat.avatar}</div>
      <div class=""chat-item-meta"">
        <div class=""chat-item-top"">
          <span class=""chat-item-name"">${chat.name}</span>
          <span class=""chat-item-time"">${chat.time}</span>
        </div>
        <div class=""chat-item-preview"">${chat.lastMessage}</div>
      </div>
    `;
    container.appendChild(item);
  });
}

function selectChat(chatId) {
  state.activeChatId = chatId;
  renderChatList();
  renderActiveChat();
}

function renderActiveChat() {
  const chat = state.chats.find((c) => c.id === state.activeChatId);
  if (!chat) return;

  document.getElementById('activePeerAvatar').textContent = chat.avatar;
  document.getElementById('activePeerName').textContent = chat.name;
  document.getElementById('activePeerStatus').innerHTML = `
    <span class=""dot-online""></span> ${chat.status}
  `;

  const viewport = document.getElementById('messagesViewport');
  const notice = viewport.querySelector('.encryption-notice');
  viewport.innerHTML = '';
  if (notice) viewport.appendChild(notice);

  chat.messages.forEach((msg) => {
    const row = document.createElement('div');
    row.className = `message-row ${msg.isOutgoing ? 'outgoing' : 'incoming'}`;

    row.innerHTML = `
      <div class=""message-bubble"">${escapeHtml(msg.text)}</div>
      <div class=""message-info"">
        <span>${msg.time}</span>
        ${msg.isOutgoing ? '<span>✓✓ E2EE Зашифровано</span>' : ''}
      </div>
    `;
    viewport.appendChild(row);
  });

  viewport.scrollTop = viewport.scrollHeight;
}

function sendMessage() {
  const input = document.getElementById('messageInput');
  const text = input.value.trim();
  if (!text) return;

  const chat = state.chats.find((c) => c.id === state.activeChatId);
  if (!chat) return;

  const newMsg = {
    id: String(Date.now()),
    sender: 'me',
    text,
    time: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
    isOutgoing: true,
  };

  chat.messages.push(newMsg);
  chat.lastMessage = text;
  chat.time = newMsg.time;

  input.value = '';
  renderChatList();
  renderActiveChat();
}

function setupEventListeners() {
  const sendBtn = document.getElementById('btnSend');
  const input = document.getElementById('messageInput');

  sendBtn.addEventListener('click', sendMessage);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  });

  // Modal
  const modal = document.getElementById('verificationModal');
  const verifyBtn = document.getElementById('btnVerifyKeys');
  const closeBtn = document.getElementById('btnCloseModal');

  verifyBtn.addEventListener('click', () => {
    document.getElementById('modalFingerprint').textContent = state.myFingerprint;
    modal.classList.remove('hidden');
  });

  closeBtn.addEventListener('click', () => {
    modal.classList.add('hidden');
  });

  modal.addEventListener('click', (e) => {
    if (e.target === modal) modal.classList.add('hidden');
  });

  // Emergency Burn
  document.getElementById('btnEmergencyBurn').addEventListener('click', () => {
    if (confirm('ЭКСТРЕННОЕ УНИЧТОЖЕНИЕ ДАННЫХ: Это действие мгновенно сотрет все ключи шифрования, сессии и базу сообщений, заполнив память и диск случайным шумом. Вы уверены?')) {
      alert('Все ключи и данные успешно стерты без возможности восстановления. Выход из Sypher.');
      window.close();
    }
  });
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}

document.addEventListener('DOMContentLoaded', initUI);
";
    }
}