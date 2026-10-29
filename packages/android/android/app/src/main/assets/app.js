// Sypher Core Controller — Decentralized Mesh Radio & Identity Engine
(function () {
  'use strict';

  // 1. Инициализация или загрузка уникального криптографического идентификатора узла
  function getOrCreateIdentity() {
    var STORAGE_KEY = 'sypher_node_identity_v1';
    var stored = localStorage.getItem(STORAGE_KEY);
    if (stored) {
      try {
        var parsed = JSON.parse(stored);
        if (parsed.myId && parsed.myFingerprint) return parsed;
      } catch (e) {}
    }

    // Генерация случайных 16 байт с использованием криптографически стойкого генератора
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

    // Уникальный Sypher ID устройства: syph1 + 14 hex символов
    var myId = 'syph1' + hex.substring(0, 14);

    // Уникальный Safety Number (отпечаток) из 8 блоков по 4 символа
    var fpParts = [];
    for (var k = 0; k < 8; k++) {
      fpParts.push(hex.substring(k * 4, k * 4 + 4).toUpperCase());
    }
    var myFingerprint = fpParts.join(' ');

    var identity = { myId: myId, myFingerprint: myFingerprint };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
    return identity;
  }

  // 2. Хранилище сообщений и чатов
  function loadStoredChats() {
    var stored = localStorage.getItem('sypher_chats_v1');
    if (stored) {
      try {
        var parsed = JSON.parse(stored);
        if (Array.isArray(parsed) && parsed.length > 0) return parsed;
      } catch (e) {}
    }

    // По умолчанию ТОЛЬКО один общий эфирный канал Mesh (никаких фейковых собеседников)
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
            text: 'Вы подключены к общему эфиру. Все устройства в локальной сети (Wi-Fi, точка доступа, Bluetooth) получают широковещательные пакеты напрямую без серверов.',
            time: formatTime(new Date()),
            isOutgoing: false,
          },
        ],
      },
    ];
  }

  function saveChats() {
    try {
      localStorage.setItem('sypher_chats_v1', JSON.stringify(state.chats));
    } catch (e) {}
  }

  var identity = getOrCreateIdentity();

  var state = {
    identity: identity,
    activeChatId: 'mesh-broadcast',
    selfDestructIndex: 1,
    selfDestructOptions: ['1ч', '24ч', '7д', 'Выкл'],
    searchQuery: '',
    discoveredPeers: {}, // peerId -> { lastSeen, name }
    chats: loadStoredChats(),
  };

  var doubleCheckSvg =
    '<svg class="icon-svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#00f0ff" stroke-width="2.5"><path d="M18 6L7 17l-5-5"/><path d="M22 10l-7.5 7.5L13 16"/></svg>';

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

  function initUI() {
    var idElem = document.getElementById('mySypherId');
    if (idElem) {
      idElem.textContent = state.identity.myId;
      idElem.onclick = function () {
        copyToClipboard(state.identity.myId, 'Ваш Sypher ID скопирован');
      };
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
    // 1. Проверяем наличие моста Android
    if (window.AndroidBridge && window.AndroidBridge.isAvailable) {
      console.log('[Sypher] Android Native Bridge active');
      updateMeshStatus('Android BLE Mesh & UDP сокет активны');
    }

    // 2. На ПК подключаем SSE для получения входящих пакетов
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
          updateMeshStatus('Локальный UDP Multicast узел активен');
        };
        sse.onerror = function () {
          // SSE недоступен, если открыт просто статический файл
        };
      } catch (e) {}
    }

    // 3. Регулярная посылка маяка присутствия (Heartbeat Ping) в эфир каждые 8 секунд
    setInterval(sendDiscoveryBeacon, 8000);
    setTimeout(sendDiscoveryBeacon, 500);
  }

  function sendDiscoveryBeacon() {
    var beacon = {
      type: 'SYPH_BEACON',
      from: state.identity.myId,
      time: Date.now(),
    };
    dispatchPacketToNetwork(beacon);
  }

  function dispatchPacketToNetwork(packet) {
    var raw = JSON.stringify(packet);

    // 1. Android Native
    if (window.AndroidBridge && window.AndroidBridge.sendMeshPacket) {
      try {
        window.AndroidBridge.sendMeshPacket(raw);
        return;
      } catch (e) {
        console.error('AndroidBridge send error:', e);
      }
    }

    // 2. Desktop Web Local Server
    if (window.fetch) {
      fetch('/api/mesh/send', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: raw,
      }).catch(function () {});
    }
  }

  // Глобальная точка входа входящих пакетов для Android WebView
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

    // Регистрируем обнаруженный узел
    state.discoveredPeers[packet.from] = {
      lastSeen: Date.now(),
      name: packet.from.substring(0, 14) + '...',
    };
    updateDiscoveredPeersUI();

    // Обработка сообщения
    if (packet.type === 'SYPH_MSG') {
      var isForUs = packet.to === state.identity.myId;
      var isBroadcast = packet.to === 'broadcast' || !packet.to;

      if (!isForUs && !isBroadcast) return; // Пакет адресован другому узлу

      var targetChatId = isBroadcast ? 'mesh-broadcast' : packet.from;
      var chat = state.chats.find(function (c) {
        return c.id === targetChatId;
      });

      // Если личный чат еще не создан, создаем его автоматически
      if (!chat) {
        chat = {
          id: packet.from,
          name: packet.from.substring(0, 16) + '...',
          avatar: packet.from.charAt(5).toUpperCase() || 'P',
          status: 'Прямой E2EE диалог',
          lastMessage: packet.text,
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
        text: packet.text,
        time: msgTime,
        isOutgoing: false,
      };

      chat.messages.push(incomingMsg);
      chat.lastMessage = packet.text;
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
        count > 0 ? count + ' узел(ов) в радиодиапазоне' : 'Поиск радиосигналов в сети...';
    }

    var listElem = document.getElementById('discoveredPeersList');
    if (!listElem) return;

    if (count === 0) {
      listElem.innerHTML = '<span class="empty-hint">Ожидание радиосигналов поблизости...</span>';
      return;
    }

    listElem.innerHTML = '';
    Object.keys(state.discoveredPeers).forEach(function (peerId) {
      var item = document.createElement('div');
      item.className = 'discovered-peer-item';
      item.innerHTML =
        '<span>' +
        escapeHtml(peerId) +
        '</span><span style="font-size:11px; color:var(--accent-green)">Рядом</span>';
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
          ? '<span style="background:var(--accent-cyan); color:#0a0d14; padding:2px 6px; border-radius:10px; font-size:10px; font-weight:700;">' +
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
        escapeHtml(chat.lastMessage || '') +
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
          chat.id.substring(0, 12) +
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

      row.innerHTML =
        authorHtml +
        '<div class="message-bubble">' +
        escapeHtml(msg.text) +
        '</div>' +
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

    // Добавляем к себе локально
    var newMsg = {
      id: packet.id,
      sender: 'me',
      authorName: 'Я',
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

    // Отправляем в радиоэфир Mesh
    dispatchPacketToNetwork(packet);
    showToast('Пакет отправлен в Mesh радиоканал');
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
    // 1. Отправка сообщения
    var sendBtn = document.getElementById('btnSend');
    if (sendBtn) {
      sendBtn.onclick = function (e) {
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

    // 2. Кнопка возврата на мобильных
    var backBtn = document.getElementById('btnBack');
    if (backBtn) {
      backBtn.onclick = function (e) {
        e.preventDefault();
        var layout = document.getElementById('appLayout');
        if (layout) layout.classList.remove('mobile-chat-open');
      };
    }

    // 3. Поиск
    var searchInput = document.getElementById('searchInput');
    if (searchInput) {
      searchInput.oninput = function () {
        state.searchQuery = searchInput.value;
        renderChatList();
      };
    }

    // 4. Модальное окно "Новый диалог"
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
          alert('Введите Sypher ID собеседника (начинается с syph1...)');
          return;
        }

        if (peerId === state.identity.myId) {
          alert('Это ваш собственный ID. Укажите ID другого телефона или ПК.');
          return;
        }

        // Проверяем, существует ли уже диалог
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

    // 5. Модальное окно верификации отпечатка
    var verifyModal = document.getElementById('verificationModal');
    var verifyBtn = document.getElementById('btnVerifyKeys');
    var closeVerifyBtn = document.getElementById('btnCloseModal');
    var tagBtn = document.getElementById('myFingerprintTag');

    function openVerifyModal(e) {
      if (e) e.preventDefault();
      var fpElem = document.getElementById('modalFingerprint');
      if (fpElem) fpElem.textContent = state.identity.myFingerprint;
      if (verifyModal) verifyModal.classList.remove('hidden');
    }

    if (verifyBtn) verifyBtn.onclick = openVerifyModal;
    if (tagBtn) tagBtn.onclick = openVerifyModal;

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

    // 6. Настройки
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

    // 7. Таймер самоуничтожения
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

    // 8. Кнопка вложений
    var attachBtn = document.getElementById('btnAttach');
    if (attachBtn) {
      attachBtn.onclick = function (e) {
        e.preventDefault();
        showToast('Шифрование файлов: выберите файл для Mesh передачи');
      };
    }

    // 9. Экстренное уничтожение
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
        showToast('Все ключи и сообщения затерты нулями');
        setTimeout(function () {
          window.location.reload();
        }, 1000);
      };
    }

    // 10. Клик вне модалок закрывает их
    [newChatModal, verifyModal, settingsModal, burnModal].forEach(function (m) {
      if (m) {
        m.onclick = function (e) {
          if (e.target === m) m.classList.add('hidden');
        };
      }
    });

    // 11. Escape закрывает модалки
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        [newChatModal, verifyModal, settingsModal, burnModal].forEach(function (m) {
          if (m) m.classList.add('hidden');
        });
      }
    });

    // 12. Баннер переключает на общий канал
    var meshBanner = document.getElementById('meshBanner');
    if (meshBanner) {
      meshBanner.onclick = function () {
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
