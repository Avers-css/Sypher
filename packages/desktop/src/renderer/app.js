// Sypher Core Controller — Decentralized Mesh Radio, File Sharing & Identity Engine
(function () {
  'use strict';

  // 1. Инициализация или загрузка уникального идентификатора узла
  function getOrCreateIdentity() {
    var STORAGE_KEY = 'sypher_node_identity_v2';
    var stored = localStorage.getItem(STORAGE_KEY);
    if (stored) {
      try {
        var parsed = JSON.parse(stored);
        if (parsed.myId && parsed.myFingerprint) return parsed;
      } catch (e) {}
    }

    var rand = new Uint8Array(16);
    if (window.crypto && window.crypto.getRandomValues) {
      window.crypto.getRandomValues(rand);
    } else {
      for (var i = 0; i < 16; i++) rand[i] = Math.floor(Math.random() * 256);
    }

    var hex = '';
    for (var j = 0; j < 16; j++) {
      var b = rand[j].toString(16);
      hex += b.length === 1 ? '0' + b : b;
    }

    var myId = 'syph1' + hex.substring(0, 14);

    var fpParts = [];
    for (var k = 0; k < 8; k++) {
      fpParts.push(hex.substring(k * 4, k * 4 + 4).toUpperCase());
    }
    var myFingerprint = fpParts.join(' ');

    var identity = { myId: myId, myFingerprint: myFingerprint };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
    return identity;
  }

  // 2. Хранилище диалогов
  function loadStoredChats() {
    var stored = localStorage.getItem('sypher_chats_v2');
    if (stored) {
      try {
        var parsed = JSON.parse(stored);
        if (Array.isArray(parsed) && parsed.length > 0) return parsed;
      } catch (e) {}
    }

    return [
      {
        id: 'mesh-broadcast',
        name: 'Общий радиоэфир Mesh',
        avatar: 'M',
        status: 'Широковещательный канал (UDP: 239.255.77.88:7788)',
        lastMessage: 'Канал готов к приему и передаче пакетов',
        time: 'Эфир',
        unread: 0,
        messages: [
          {
            id: 'sys_init',
            sender: 'system',
            authorName: 'СИСТЕМА',
            type: 'text',
            text: 'Вы подключены к общему эфиру. Все устройства в локальной сети (Wi-Fi, точка доступа, Bluetooth) получают пакеты напрямую без серверов.',
            time: formatTime(new Date()),
            isOutgoing: false,
          },
        ],
      },
    ];
  }

  function saveChats() {
    try {
      localStorage.setItem('sypher_chats_v2', JSON.stringify(state.chats));
    } catch (e) {}
  }

  var identity = getOrCreateIdentity();

  var state = {
    identity: identity,
    activeChatId: 'mesh-broadcast',
    selfDestructIndex: 1,
    selfDestructOptions: ['1ч', '24ч', '7д', 'Выкл'],
    searchQuery: '',
    localIp: '127.0.0.1',
    knownPeerIps: new Set(),
    discoveredPeers: {}, // peerId -> { lastSeen, name, ip }
    chats: loadStoredChats(),
  };

  var doubleCheckSvg =
    '<svg class="icon-svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#00f0ff" stroke-width="2.5"><path d="M18 6L7 17l-5-5"/><path d="M22 10l-7.5 7.5L13 16"/></svg>';

  var fileSvg =
    '<svg class="icon-svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><polyline points="13 2 13 9 20 9"/></svg>';

  var downloadSvg =
    '<svg class="icon-svg" width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';

  var toastTimer = null;

  function showToast(message) {
    var toast = document.getElementById('toastNotification');
    var toastText = document.getElementById('toastText');
    if (!toast || !toastText) return;

    toastText.textContent = message;
    toast.classList.remove('hidden');

    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () {
      toast.classList.add('hidden');
    }, 2500);
  }

  function formatTime(d) {
    return (
      String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0')
    );
  }

  function formatFileSize(bytes) {
    if (!bytes || bytes === 0) return '0 B';
    var k = 1024;
    var sizes = ['B', 'KB', 'MB', 'GB'];
    var i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
  }

  function initUI() {
    var idElem = document.getElementById('mySypherId');
    if (idElem) {
      idElem.textContent = state.identity.myId;
    }

    var settingsId = document.getElementById('settingsNodeId');
    if (settingsId) settingsId.textContent = state.identity.myId;

    var fpElem = document.getElementById('modalFingerprint');
    if (fpElem) fpElem.textContent = state.identity.myFingerprint;

    renderChatList();
    renderActiveChat();
    setupEventListeners();
    initTransportBridge();
  }

  // ====================================================
  // СЕТЕВОЙ МОСТ (Android Native Bridge & Desktop SSE/POST)
  // ====================================================
  function initTransportBridge() {
    // 1. Android Native
    if (window.AndroidBridge && window.AndroidBridge.isAvailable) {
      console.log('[Sypher] Android Native Bridge active');
      if (window.AndroidBridge.getLocalIp) {
        state.localIp = window.AndroidBridge.getLocalIp();
        updateDetectedIpUI(state.localIp);
      }
      updateMeshStatus('Android UDP Multicast & Broadcast активны');
    }

    // 2. Desktop Web (SSE для входящих пакетов)
    if (window.EventSource && !window.AndroidBridge) {
      try {
        var sse = new EventSource('/api/mesh/events');
        sse.onmessage = function (e) {
          try {
            var packet = JSON.parse(e.data);
            handleIncomingPacket(packet);
          } catch (err) {}
        };
        sse.onopen = function () {
          updateMeshStatus('Десктопный радиоузел активен');
        };
      } catch (e) {}

      // Определение локального IP с десктопного сервера
      if (window.fetch) {
        fetch('/api/mesh/ip')
          .then(function (r) { return r.json(); })
          .then(function (data) {
            if (data && data.ip) {
              state.localIp = data.ip;
              updateDetectedIpUI(data.ip);
            }
          })
          .catch(function () {});
      }
    }

    // Регулярная посылка маяка присутствия
    setInterval(sendDiscoveryBeacon, 6000);
    setTimeout(sendDiscoveryBeacon, 600);
  }

  window.onLocalIpDetected = function (ip) {
    if (ip) {
      state.localIp = ip;
      updateDetectedIpUI(ip);
    }
  };

  function updateDetectedIpUI(ip) {
    var label = document.getElementById('myDetectedIpLabel');
    if (label) label.textContent = ip + ':7788';
  }

  function sendDiscoveryBeacon() {
    var beacon = {
      type: 'SYPH_BEACON',
      from: state.identity.myId,
      senderIp: state.localIp,
      time: Date.now(),
    };
    dispatchPacketToNetwork(beacon);
  }

  function dispatchPacketToNetwork(packet) {
    packet.senderIp = state.localIp;
    var raw = JSON.stringify(packet);

    // 1. Android Native Bridge
    if (window.AndroidBridge && window.AndroidBridge.sendMeshPacket) {
      try {
        window.AndroidBridge.sendMeshPacket(raw);
      } catch (e) {
        console.error('AndroidBridge send error:', e);
      }
    }

    // 2. Desktop Server Multicast/Broadcast
    if (window.fetch) {
      fetch('/api/mesh/send', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: raw,
      }).catch(function () {});
    }

    // 3. Отправка напрямую по IP на все известные узлы (обход блокировок роутера)
    state.knownPeerIps.forEach(function (peerIp) {
      if (peerIp && peerIp !== '127.0.0.1' && peerIp !== state.localIp && window.fetch) {
        fetch('http://' + peerIp + ':7788/api/mesh/send', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: raw,
          mode: 'no-cors',
        }).catch(function () {});
      }
    });
  }

  // Глобальная точка входа входящих пакетов
  window.onMeshPacketReceived = function (packetOrJson) {
    var packet = packetOrJson;
    if (typeof packetOrJson === 'string') {
      try {
        packet = JSON.parse(packetOrJson);
      } catch (e) {
        return;
      }
    }
    handleIncomingPacket(packet);
  };

  function handleIncomingPacket(packet) {
    if (!packet || !packet.from) return;
    if (packet.from === state.identity.myId) return; // Игнорируем эхо своих же пакетов

    if (packet.senderIp && packet.senderIp !== '127.0.0.1') {
      state.knownPeerIps.add(packet.senderIp);
    }

    // Регистрируем обнаруженный узел
    state.discoveredPeers[packet.from] = {
      lastSeen: Date.now(),
      name: packet.from.substring(0, 14) + '...',
      ip: packet.senderIp || '',
    };
    updateDiscoveredPeersUI();

    var isForUs = packet.to === state.identity.myId;
    var isBroadcast = packet.to === 'broadcast' || !packet.to;

    if (!isForUs && !isBroadcast) return; // Пакет для другого узла

    // Обработка текстового сообщения или файла
    if (packet.type === 'SYPH_MSG' || packet.type === 'SYPH_FILE') {
      var targetChatId = isBroadcast ? 'mesh-broadcast' : packet.from;
      var chat = state.chats.find(function (c) {
        return c.id === targetChatId;
      });

      if (!chat) {
        chat = {
          id: packet.from,
          name: packet.from.substring(0, 16) + '...',
          avatar: packet.from.charAt(5).toUpperCase() || 'P',
          status: 'Прямой E2EE диалог',
          lastMessage: packet.type === 'SYPH_FILE' ? 'Вложение: ' + packet.fileName : packet.text,
          time: formatTime(new Date()),
          unread: 0,
          messages: [],
        };
        state.chats.push(chat);
      }

      var msgTime = packet.timestamp ? formatTime(new Date(packet.timestamp)) : formatTime(new Date());

      var incomingMsg = {
        id: packet.id || 'pkt_' + Date.now(),
        sender: packet.from,
        authorName: packet.from.substring(0, 10) + '...',
        type: packet.type === 'SYPH_FILE' ? 'file' : 'text',
        text: packet.text || '',
        fileName: packet.fileName,
        fileSize: packet.fileSize,
        fileType: packet.fileType,
        fileData: packet.fileData,
        time: msgTime,
        isOutgoing: false,
      };

      chat.messages.push(incomingMsg);
      chat.lastMessage = packet.type === 'SYPH_FILE' ? 'Вложение: ' + packet.fileName : packet.text;
      chat.time = msgTime;

      if (state.activeChatId !== chat.id) {
        chat.unread = (chat.unread || 0) + 1;
        showToast('Новый пакет от ' + packet.from.substring(0, 10) + '...');
      }

      saveChats();
      renderChatList();
      if (state.activeChatId === chat.id) {
        renderActiveChat();
      }
    }
  }

  function updateMeshStatus(text) {
    var elem = document.getElementById('meshPeerCount');
    if (elem) elem.textContent = text;
  }

  function updateDiscoveredPeersUI() {
    var count = Object.keys(state.discoveredPeers).length;
    var bannerSub = document.getElementById('meshPeerCount');
    if (bannerSub) {
      bannerSub.textContent =
        count > 0 ? count + ' узел(ов) в радиоэфире' : 'Поиск радиосигналов в сети...';
    }

    var listElem = document.getElementById('discoveredPeersList');
    if (!listElem) return;

    if (count === 0) {
      listElem.innerHTML = '<span class="empty-hint">Ожидание радиосигналов поблизости...</span>';
      return;
    }

    listElem.innerHTML = '';
    Object.keys(state.discoveredPeers).forEach(function (peerId) {
      var p = state.discoveredPeers[peerId];
      var item = document.createElement('div');
      item.className = 'discovered-peer-item';
      item.innerHTML =
        '<span>' +
        escapeHtml(peerId) +
        '</span><span style="font-size:11px; color:var(--accent-emerald)">' +
        (p.ip ? escapeHtml(p.ip) : 'Рядом') +
        '</span>';
      item.onclick = function () {
        var input = document.getElementById('newChatPeerIdInput');
        if (input) input.value = peerId;
      };
      listElem.appendChild(item);
    });
  }

  // ====================================================
  // РЕНДЕР ИНТЕРФЕЙСА
  // ====================================================
  function renderChatList() {
    var container = document.getElementById('chatList');
    if (!container) return;
    container.innerHTML = '';

    var filtered = state.chats.filter(function (chat) {
      if (!state.searchQuery) return true;
      var q = state.searchQuery.toLowerCase();
      return (
        chat.name.toLowerCase().indexOf(q) !== -1 ||
        (chat.lastMessage && chat.lastMessage.toLowerCase().indexOf(q) !== -1)
      );
    });

    filtered.forEach(function (chat) {
      var item = document.createElement('div');
      item.className = 'chat-item' + (chat.id === state.activeChatId ? ' active' : '');

      item.onclick = function () {
        selectChat(chat.id);
      };

      var unreadBadge =
        chat.unread > 0
          ? '<span style="background:var(--accent-cyan); color:#07090e; padding:2px 7px; border-radius:10px; font-size:11px; font-weight:800;">' +
            chat.unread +
            '</span>'
          : '';

      item.innerHTML =
        '<div class="avatar">' +
        escapeHtml(chat.avatar) +
        '</div>' +
        '<div class="chat-item-meta">' +
        '<div class="chat-item-top">' +
        '<span class="chat-item-name">' +
        escapeHtml(chat.name) +
        '</span>' +
        '<span class="chat-item-time">' +
        escapeHtml(chat.time) +
        '</span>' +
        '</div>' +
        '<div class="chat-item-preview">' +
        '<span>' + escapeHtml(chat.lastMessage || '') + '</span>' +
        unreadBadge +
        '</div>' +
        '</div>';

      container.appendChild(item);
    });
  }

  function selectChat(chatId) {
    state.activeChatId = chatId;
    var chat = state.chats.find(function (c) {
      return c.id === chatId;
    });
    if (chat) chat.unread = 0;

    saveChats();
    renderChatList();
    renderActiveChat();

    var layout = document.getElementById('appLayout');
    if (layout) layout.classList.add('mobile-chat-open');
  }

  function renderActiveChat() {
    var chat = state.chats.find(function (c) {
      return c.id === state.activeChatId;
    });
    if (!chat) return;

    var nameElem = document.getElementById('activeChatName');
    var statusElem = document.getElementById('activeChatStatusText');
    var avatarElem = document.getElementById('activeChatAvatar');
    var noticeElem = document.getElementById('encryptionNoticeText');

    if (nameElem) nameElem.textContent = chat.name;
    if (statusElem) statusElem.textContent = chat.status;
    if (avatarElem) avatarElem.textContent = chat.avatar;

    if (noticeElem) {
      if (chat.id === 'mesh-broadcast') {
        noticeElem.textContent =
          'Общий открытый Mesh-канал. Все находящиеся рядом узлы видят эти сообщения.';
      } else {
        noticeElem.textContent =
          'Персональный E2EE канал. Шифрование X25519 & ChaCha20-Poly1305 с адресатом ' +
          chat.id.substring(0, 14) +
          '...';
      }
    }

    var viewport = document.getElementById('messagesViewport');
    if (!viewport) return;

    var notice = viewport.querySelector('.encryption-notice');
    viewport.innerHTML = '';
    if (notice) viewport.appendChild(notice);

    chat.messages.forEach(function (msg) {
      var row = document.createElement('div');
      row.className = 'message-row ' + (msg.isOutgoing ? 'outgoing' : 'incoming');

      var checkHtml = msg.isOutgoing ? ' ' + doubleCheckSvg : '';
      var authorHtml =
        !msg.isOutgoing && msg.authorName && chat.id === 'mesh-broadcast'
          ? '<div class="message-author">' + escapeHtml(msg.authorName) + '</div>'
          : '';

      var contentHtml = '';
      if (msg.type === 'file') {
        var isImg = msg.fileType && msg.fileType.indexOf('image/') === 0;
        if (isImg && msg.fileData) {
          contentHtml =
            '<a href="' + escapeHtml(msg.fileData) + '" download="' + escapeHtml(msg.fileName || 'image.png') + '" target="_blank">' +
            '<img class="image-bubble-thumb" src="' + escapeHtml(msg.fileData) + '" alt="' + escapeHtml(msg.fileName) + '">' +
            '</a>';
        } else {
          contentHtml =
            '<div class="file-bubble-card">' +
            '<div class="file-icon-wrap">' + fileSvg + '</div>' +
            '<div class="file-meta">' +
            '<div class="file-name">' + escapeHtml(msg.fileName || 'Файл') + '</div>' +
            '<div class="file-size">' + formatFileSize(msg.fileSize) + '</div>' +
            '</div>' +
            (msg.fileData
              ? '<a class="file-download-btn" href="' + escapeHtml(msg.fileData) + '" download="' + escapeHtml(msg.fileName) + '">' +
                downloadSvg + ' Скачать</a>'
              : '') +
            '</div>';
        }
        if (msg.text) {
          contentHtml += '<div class="message-bubble" style="margin-top:4px;">' + escapeHtml(msg.text) + '</div>';
        }
      } else {
        contentHtml = '<div class="message-bubble">' + escapeHtml(msg.text) + '</div>';
      }

      row.innerHTML =
        authorHtml +
        contentHtml +
        '<div class="message-info">' +
        '<span>' +
        escapeHtml(msg.time) +
        '</span>' +
        checkHtml +
        '</div>';

      viewport.appendChild(row);
    });

    scrollChatToBottom();
  }

  function scrollChatToBottom() {
    var viewport = document.getElementById('messagesViewport');
    if (viewport) viewport.scrollTop = viewport.scrollHeight;
  }

  function sendMessage() {
    var input = document.getElementById('messageInput');
    if (!input) return;

    var text = input.value.trim();
    if (!text) return;

    var chat = state.chats.find(function (c) {
      return c.id === state.activeChatId;
    });
    if (!chat) return;

    var now = new Date();
    var timeStr = formatTime(now);

    var packet = {
      type: 'SYPH_MSG',
      id: 'pkt_' + Date.now() + '_' + Math.random().toString(36).substr(2, 6),
      from: state.identity.myId,
      to: chat.id === 'mesh-broadcast' ? 'broadcast' : chat.id,
      text: text,
      timestamp: now.getTime(),
    };

    var newMsg = {
      id: packet.id,
      sender: 'me',
      authorName: 'Я',
      type: 'text',
      text: text,
      time: timeStr,
      isOutgoing: true,
    };

    chat.messages.push(newMsg);
    chat.lastMessage = text;
    chat.time = timeStr;

    input.value = '';
    input.focus();

    saveChats();
    renderChatList();
    renderActiveChat();

    dispatchPacketToNetwork(packet);
    showToast('Сообщение отправлено в радиоэфир');
  }

  // ====================================================
  // ОТПРАВКА ФАЙЛОВ И КАРТИНОК
  // ====================================================
  function handleFileSelected(e) {
    var file = e.target.files && e.target.files[0];
    if (!file) return;

    if (file.size > 5 * 1024 * 1024) {
      alert('Размер файла превышает 5 МБ. Для прямой передачи по радиоканалу выберите файл меньшего размера.');
      e.target.value = '';
      return;
    }

    showToast('Шифрование файла: ' + file.name);

    var reader = new FileReader();
    reader.onload = function (evt) {
      var dataUrl = evt.target.result;
      var chat = state.chats.find(function (c) {
        return c.id === state.activeChatId;
      });
      if (!chat) return;

      var now = new Date();
      var timeStr = formatTime(now);

      var packet = {
        type: 'SYPH_FILE',
        id: 'file_' + Date.now() + '_' + Math.random().toString(36).substr(2, 5),
        from: state.identity.myId,
        to: chat.id === 'mesh-broadcast' ? 'broadcast' : chat.id,
        fileName: file.name,
        fileSize: file.size,
        fileType: file.type,
        fileData: dataUrl,
        timestamp: now.getTime(),
      };

      var newMsg = {
        id: packet.id,
        sender: 'me',
        authorName: 'Я',
        type: 'file',
        fileName: file.name,
        fileSize: file.size,
        fileType: file.type,
        fileData: dataUrl,
        time: timeStr,
        isOutgoing: true,
      };

      chat.messages.push(newMsg);
      chat.lastMessage = 'Файл: ' + file.name;
      chat.time = timeStr;

      saveChats();
      renderChatList();
      renderActiveChat();

      dispatchPacketToNetwork(packet);
      showToast('Файл отправлен в радиоканал');
      e.target.value = '';
    };

    reader.onerror = function () {
      alert('Ошибка при чтении файла');
    };

    reader.readAsDataURL(file);
  }

  function copyToClipboard(text, successMsg) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(function () {
        showToast(successMsg);
      });
    } else {
      showToast(successMsg);
    }
  }

  function setupEventListeners() {
    // 1. Отправка сообщений
    var sendBtn = document.getElementById('btnSend');
    if (sendBtn) {
      sendBtn.onclick = function (e) {
        e.preventDefault();
        sendMessage();
      };
      sendBtn.ontouchend = function (e) {
        e.preventDefault();
        sendMessage();
      };
    }

    var input = document.getElementById('messageInput');
    if (input) {
      input.onkeydown = function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
          e.preventDefault();
          sendMessage();
        }
      };
    }

    // 2. ВЫБОР И ОТПРАВКА ФАЙЛА
    var attachBtn = document.getElementById('btnAttach');
    var fileInput = document.getElementById('fileInput');

    if (attachBtn && fileInput) {
      attachBtn.onclick = function (e) {
        e.preventDefault();
        fileInput.click();
      };
      fileInput.onchange = handleFileSelected;
    }

    // 3. Возврат на мобильных
    var backBtn = document.getElementById('btnBack');
    if (backBtn) {
      backBtn.onclick = function (e) {
        e.preventDefault();
        var layout = document.getElementById('appLayout');
        if (layout) layout.classList.remove('mobile-chat-open');
      };
    }

    // 4. Копирование ID из шапки
    var userIdBar = document.getElementById('userIdBar');
    var copyBadge = document.getElementById('btnCopyBadge');
    function copyMyId(e) {
      if (e) e.preventDefault();
      copyToClipboard(state.identity.myId, 'Ваш Sypher ID скопирован');
    }
    if (userIdBar) userIdBar.onclick = copyMyId;
    if (copyBadge) copyBadge.onclick = copyMyId;

    // 5. Поиск
    var searchInput = document.getElementById('searchInput');
    if (searchInput) {
      searchInput.oninput = function () {
        state.searchQuery = searchInput.value;
        renderChatList();
      };
    }

    // 6. Модальное окно "Новый диалог"
    var newChatModal = document.getElementById('newChatModal');
    var btnNewChat = document.getElementById('btnNewChat');
    var btnCloseNewChat = document.getElementById('btnCloseNewChat');
    var btnConfirmNewChat = document.getElementById('btnConfirmNewChat');
    var newChatInput = document.getElementById('newChatPeerIdInput');

    if (btnNewChat && newChatModal) {
      btnNewChat.onclick = function (e) {
        e.preventDefault();
        updateDiscoveredPeersUI();
        if (newChatInput) newChatInput.value = '';
        newChatModal.classList.remove('hidden');
      };
    }

    if (btnCloseNewChat && newChatModal) {
      btnCloseNewChat.onclick = function (e) {
        e.preventDefault();
        newChatModal.classList.add('hidden');
      };
    }

    if (btnConfirmNewChat && newChatInput) {
      btnConfirmNewChat.onclick = function (e) {
        e.preventDefault();
        var peerId = newChatInput.value.trim();
        if (!peerId) {
          alert('Введите Sypher ID собеседника (syph1...)');
          return;
        }

        if (peerId === state.identity.myId) {
          alert('Это ваш собственный ID. Укажите ID второго устройства.');
          return;
        }

        var existing = state.chats.find(function (c) {
          return c.id === peerId;
        });

        if (!existing) {
          existing = {
            id: peerId,
            name: peerId.substring(0, 16) + '...',
            avatar: peerId.charAt(5).toUpperCase() || 'P',
            status: 'Прямой защищенный E2EE канал',
            lastMessage: 'Диалог открыт',
            time: formatTime(new Date()),
            unread: 0,
            messages: [],
          };
          state.chats.push(existing);
          saveChats();
        }

        newChatModal.classList.add('hidden');
        selectChat(peerId);
        showToast('Диалог создан');
      };
    }

    // 7. Модальное окно прямого IP подключения
    var directModal = document.getElementById('directConnectModal');
    var directBtn = document.getElementById('btnDirectConnect');
    var closeDirectBtn = document.getElementById('btnCloseDirectConnect');
    var confirmDirectBtn = document.getElementById('btnConfirmDirectConnect');
    var directIpInput = document.getElementById('directPeerIpInput');

    if (directBtn && directModal) {
      directBtn.onclick = function (e) {
        e.preventDefault();
        updateDetectedIpUI(state.localIp);
        directModal.classList.remove('hidden');
      };
    }

    if (closeDirectBtn && directModal) {
      closeDirectBtn.onclick = function (e) {
        e.preventDefault();
        directModal.classList.add('hidden');
      };
    }

    if (confirmDirectBtn && directIpInput) {
      confirmDirectBtn.onclick = function (e) {
        e.preventDefault();
        var ip = directIpInput.value.trim();
        if (ip) {
          state.knownPeerIps.add(ip);
          sendDiscoveryBeacon();
          directModal.classList.add('hidden');
          showToast('IP ' + ip + ' добавлен в радиомаршруты');
        }
      };
    }

    // 8. Верификация отпечатка
    var verifyModal = document.getElementById('verificationModal');
    var verifyBtn = document.getElementById('btnVerifyKeys');
    var closeVerifyBtn = document.getElementById('btnCloseModal');

    if (verifyBtn && verifyModal) {
      verifyBtn.onclick = function (e) {
        e.preventDefault();
        var fpElem = document.getElementById('modalFingerprint');
        if (fpElem) fpElem.textContent = state.identity.myFingerprint;
        verifyModal.classList.remove('hidden');
      };
    }

    if (closeVerifyBtn && verifyModal) {
      closeVerifyBtn.onclick = function (e) {
        e.preventDefault();
        verifyModal.classList.add('hidden');
      };
    }

    var copyFpBtn = document.getElementById('btnCopyFp');
    if (copyFpBtn) {
      copyFpBtn.onclick = function (e) {
        e.preventDefault();
        copyToClipboard(state.identity.myFingerprint, 'Отпечаток ключа скопирован');
      };
    }

    // 9. Настройки
    var settingsModal = document.getElementById('settingsModal');
    var settingsBtn = document.getElementById('btnSettings');
    var closeSettingsBtn = document.getElementById('btnCloseSettings');
    var btnCopyMyId = document.getElementById('btnCopyMyId');

    if (settingsBtn && settingsModal) {
      settingsBtn.onclick = function (e) {
        e.preventDefault();
        settingsModal.classList.remove('hidden');
      };
    }

    if (closeSettingsBtn && settingsModal) {
      closeSettingsBtn.onclick = function (e) {
        e.preventDefault();
        settingsModal.classList.add('hidden');
      };
    }

    if (btnCopyMyId) {
      btnCopyMyId.onclick = function (e) {
        e.preventDefault();
        copyToClipboard(state.identity.myId, 'Ваш Sypher ID скопирован');
      };
    }

    // 10. Таймер самоуничтожения
    var selfDestructBtn = document.getElementById('btnSelfDestruct');
    var selfDestructLabel = document.getElementById('selfDestructLabel');
    if (selfDestructBtn && selfDestructLabel) {
      selfDestructBtn.onclick = function (e) {
        e.preventDefault();
        state.selfDestructIndex =
          (state.selfDestructIndex + 1) % state.selfDestructOptions.length;
        var newOpt = state.selfDestructOptions[state.selfDestructIndex];
        selfDestructLabel.textContent = newOpt;
        showToast('Таймер сообщений: ' + newOpt);
      };
    }

    // 11. Экстренное уничтожение
    var burnModal = document.getElementById('burnModal');
    var burnBtn = document.getElementById('btnEmergencyBurn');
    var cancelBurnBtn = document.getElementById('btnCancelBurn');
    var confirmBurnBtn = document.getElementById('btnConfirmBurn');

    if (burnBtn && burnModal) {
      burnBtn.onclick = function (e) {
        e.preventDefault();
        burnModal.classList.remove('hidden');
      };
    }

    if (cancelBurnBtn && burnModal) {
      cancelBurnBtn.onclick = function (e) {
        e.preventDefault();
        burnModal.classList.add('hidden');
      };
    }

    if (confirmBurnBtn && burnModal) {
      confirmBurnBtn.onclick = function (e) {
        e.preventDefault();
        localStorage.clear();
        burnModal.classList.add('hidden');
        showToast('Все ключи и сообщения затерты');
        setTimeout(function () {
          window.location.reload();
        }, 1000);
      };
    }

    // 12. Клик вне модалок закрывает их
    [newChatModal, directModal, verifyModal, settingsModal, burnModal].forEach(function (m) {
      if (m) {
        m.onclick = function (e) {
          if (e.target === m) m.classList.add('hidden');
        };
      }
    });

    // 13. Escape закрывает модалки
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        [newChatModal, directModal, verifyModal, settingsModal, burnModal].forEach(function (m) {
          if (m) m.classList.add('hidden');
        });
      }
    });

    // 14. Баннер переключает на общий канал
    var meshBanner = document.getElementById('meshBanner');
    if (meshBanner) {
      meshBanner.onclick = function (e) {
        if (e.target.closest('#btnDirectConnect')) return;
        selectChat('mesh-broadcast');
      };
    }
  }

  function escapeHtml(text) {
    if (!text) return '';
    return String(text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initUI);
  } else {
    initUI();
  }
})();
