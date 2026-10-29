// Sypher Client Core — Offline BLE Mesh, Dual UDP Radio & E2EE Messenger
(function () {
  'use strict';

  // 1. Уникальная идентификация узла
  function getDevicePlatformPrefix() {
    var ua = navigator.userAgent || '';
    if (window.AndroidBridge) return 'syp-droid-';
    if (/Android/i.test(ua)) return 'syp-droid-';
    if (/iPhone|iPad|iPod/i.test(ua)) return 'syp-ios-';
    if (/Mac/i.test(ua)) return 'syp-mac-';
    if (/Linux/i.test(ua)) return 'syp-lin-';
    return 'syp-win-';
  }

  function generateSecureHex(bytesLength) {
    var rand = new Uint8Array(bytesLength);
    if (window.crypto && window.crypto.getRandomValues) {
      window.crypto.getRandomValues(rand);
    } else {
      for (var i = 0; i < bytesLength; i++) {
        rand[i] = Math.floor(Math.random() * 256);
      }
    }
    var hex = '';
    for (var j = 0; j < bytesLength; j++) {
      var b = rand[j].toString(16);
      hex += b.length === 1 ? '0' + b : b;
    }
    return hex;
  }

  function getAvatarColor(str) {
    var colors = [
      ['#007aff', '#00f0ff'],
      ['#7928ca', '#ff0080'],
      ['#00e676', '#00b0ff'],
      ['#ff9100', '#ff5252'],
      ['#1de9b6', '#1dc4e9'],
      ['#651fff', '#d500f9'],
    ];
    var hash = 0;
    for (var i = 0; i < str.length; i++) {
      hash = str.charCodeAt(i) + ((hash << 5) - hash);
    }
    var idx = Math.abs(hash) % colors.length;
    return colors[idx];
  }

  function getOrCreateIdentity() {
    var STORAGE_KEY = 'sypher_node_identity_v3';
    var stored = localStorage.getItem(STORAGE_KEY);
    if (stored) {
      try {
        var parsed = JSON.parse(stored);
        if (parsed.myId && parsed.myFingerprint) return parsed;
      } catch (e) {}
    }

    var prefix = getDevicePlatformPrefix();
    var hex = generateSecureHex(16);
    var myId = prefix + hex.substring(0, 8);

    var fpParts = [];
    for (var k = 0; k < 8; k++) {
      fpParts.push(hex.substring(k * 4, k * 4 + 4).toUpperCase());
    }
    var myFingerprint = fpParts.join(' ');

    var displayName = 'Узел ' + myId.replace('syp-', '').toUpperCase();

    var identity = {
      myId: myId,
      displayName: displayName,
      myFingerprint: myFingerprint,
    };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
    return identity;
  }

  // 2. Хранилище диалогов (ТОЛЬКО ОДИН канал по умолчанию — общий эфир)
  function loadStoredChats() {
    var STORAGE_KEY = 'sypher_chats_v3';
    var stored = localStorage.getItem(STORAGE_KEY);
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
        status: 'Широковещательный канал (UDP & BLE)',
        lastMessage: 'Радиоканал готов к обмену пакетами',
        time: 'Эфир',
        unread: 0,
        messages: [
          {
            id: 'sys_start',
            sender: 'system',
            authorName: 'СИСТЕМА',
            type: 'text',
            text: 'Автономный радиоканал активен. Сообщения передаются напрямую через Wi-Fi UDP и Bluetooth Low Energy без серверов и интернета.',
            time: formatTime(new Date()),
            isOutgoing: false,
          },
        ],
      },
    ];
  }

  function saveChats() {
    try {
      localStorage.setItem('sypher_chats_v3', JSON.stringify(state.chats));
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
    discoveredPeers: {}, // peerId -> { id, name, transport, rssi, distance, ip, mac, lastSeen }
    chats: loadStoredChats(),
  };

  var singleCheckSvg =
    '<svg class="tick-icon" width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="20 6 9 17 4 12"/></svg>';

  var doubleCheckSvg =
    '<svg class="tick-icon" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="var(--accent-cyan)" stroke-width="2.5"><path d="M18 6L7 17l-5-5"/><path d="M22 10l-7.5 7.5L13 16"/></svg>';

  var docIconSvg =
    '<svg class="icon-svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/></svg>';

  var downloadIconSvg =
    '<svg class="icon-svg" width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';

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
    }, 2800);
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

  function escapeHtml(str) {
    if (!str) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  // ====================================================
  // РЕНДЕР ИНТЕРФЕЙСА
  // ====================================================
  function initUI() {
    var idElem = document.getElementById('mySypherId');
    if (idElem) idElem.textContent = state.identity.myId;

    var nameElem = document.getElementById('myNodeDisplayName');
    if (nameElem) nameElem.textContent = state.identity.displayName || 'Мой узел';

    var avatarElem = document.getElementById('myNodeAvatar');
    if (avatarElem) {
      avatarElem.textContent = (state.identity.displayName || 'U').charAt(0).toUpperCase();
      var grad = getAvatarColor(state.identity.myId);
      avatarElem.style.background = 'linear-gradient(135deg, ' + grad[0] + ', ' + grad[1] + ')';
    }

    var settingsId = document.getElementById('settingsNodeId');
    if (settingsId) settingsId.textContent = state.identity.myId;

    var settingsName = document.getElementById('inputDisplayName');
    if (settingsName) settingsName.value = state.identity.displayName || '';

    var fpElem = document.getElementById('modalFingerprint');
    if (fpElem) fpElem.textContent = state.identity.myFingerprint;

    // Синхронизируем ID с нативным Android BLE сервисом
    if (window.AndroidBridge && window.AndroidBridge.updateNodeIdentity) {
      try {
        window.AndroidBridge.updateNodeIdentity(state.identity.myId);
      } catch (e) {}
    }

    renderChatList();
    renderActiveChat();
    renderNearbyPeers();
    setupEventListeners();
    initNetworkTransports();

    // Запускаем периодический маяк обнаружения
    setInterval(sendDiscoveryBeacon, 5000);
    sendDiscoveryBeacon();
  }

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

      var grad = getAvatarColor(chat.id);
      var avatarStyle = 'background: linear-gradient(135deg, ' + grad[0] + ', ' + grad[1] + ')';

      var unreadHtml =
        chat.unread > 0
          ? '<span class="unread-badge">' + chat.unread + '</span>'
          : '';

      var chatMeta = getPeerMetadata({ id: chat.id, name: chat.name });
      var tagHtml = '';
      if (chat.id === 'mesh-broadcast') {
        tagHtml = '<span class="platform-pill" style="margin-left:6px; font-size:8px;">ЭФИР</span>';
      } else {
        tagHtml = '<span class="platform-pill ' + chatMeta.badgeClass + '" style="margin-left:6px; font-size:8px;">' + chatMeta.badgeText + '</span>';
      }

      item.innerHTML =
        '<div class="avatar" style="' +
        avatarStyle +
        '">' +
        escapeHtml(chat.avatar || 'P') +
        '</div>' +
        '<div class="chat-item-meta">' +
        '<div class="chat-item-top">' +
        '<span class="chat-item-name">' +
        escapeHtml(chat.name) +
        tagHtml +
        '</span>' +
        '<span class="chat-item-time">' +
        escapeHtml(chat.time) +
        '</span>' +
        '</div>' +
        '<div class="chat-item-preview">' +
        '<span class="preview-text">' +
        escapeHtml(chat.lastMessage || '') +
        '</span>' +
        unreadHtml +
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
    var bannerText = document.getElementById('encryptionBannerText');

    if (nameElem) nameElem.textContent = chat.name;
    if (statusElem) statusElem.textContent = chat.status;
    if (avatarElem) {
      avatarElem.textContent = chat.avatar || 'P';
      var grad = getAvatarColor(chat.id);
      avatarElem.style.background = 'linear-gradient(135deg, ' + grad[0] + ', ' + grad[1] + ')';
    }

    if (bannerText) {
      if (chat.id === 'mesh-broadcast') {
        bannerText.textContent =
          'Общий открытый Mesh-канал. Все находящиеся рядом узлы видят эти сообщения.';
      } else {
        bannerText.textContent =
          'Персональный E2EE канал. Шифрование X25519 & ChaCha20-Poly1305 с узлом ' +
          chat.id;
      }
    }

    var viewport = document.getElementById('messagesViewport');
    if (!viewport) return;

    // Сохраняем date-badge и encryption-banner
    var staticElements = [];
    var dateBadge = viewport.querySelector('.date-badge-wrap');
    var encBanner = viewport.querySelector('.encryption-banner');
    if (dateBadge) staticElements.push(dateBadge);
    if (encBanner) staticElements.push(encBanner);

    viewport.innerHTML = '';
    staticElements.forEach(function (el) {
      viewport.appendChild(el);
    });

    chat.messages.forEach(function (msg) {
      var row = document.createElement('div');
      row.className = 'message-row ' + (msg.isOutgoing ? 'outgoing' : 'incoming');

      var bubble = document.createElement('div');
      bubble.className = 'bubble';

      if (!msg.isOutgoing && chat.id === 'mesh-broadcast' && msg.authorName) {
        var author = document.createElement('div');
        author.className = 'bubble-author';
        author.textContent = msg.authorName;
        bubble.appendChild(author);
      }

      // Текстовое сообщение
      if (msg.type === 'text') {
        var textEl = document.createElement('div');
        textEl.className = 'bubble-text';
        textEl.textContent = msg.text;
        bubble.appendChild(textEl);
      }
      // Файл или фото
      else if (msg.type === 'file') {
        if (msg.fileType && msg.fileType.indexOf('image/') === 0 && msg.fileData) {
          var img = document.createElement('img');
          img.className = 'bubble-image-preview';
          img.src = msg.fileData;
          img.alt = msg.fileName || 'Изображение';
          bubble.appendChild(img);
        }

        var fileCard = document.createElement('div');
        fileCard.className = 'bubble-file-card';
        fileCard.innerHTML =
          '<div class="file-icon-box">' +
          docIconSvg +
          '</div>' +
          '<div class="file-card-details">' +
          '<div class="file-card-name">' +
          escapeHtml(msg.fileName || 'Файл') +
          '</div>' +
          '<div class="file-card-size">' +
          formatFileSize(msg.fileSize) +
          '</div>' +
          '</div>' +
          '<a class="file-download-link" title="Скачать">' +
          downloadIconSvg +
          '</a>';

        var dlLink = fileCard.querySelector('.file-download-link');
        if (dlLink && msg.fileData) {
          dlLink.href = msg.fileData;
          dlLink.download = msg.fileName || 'sypher_file';
        }
        bubble.appendChild(fileCard);
      }

      // Футер баббла: время и статус доставки
      var footer = document.createElement('div');
      footer.className = 'bubble-footer';

      var timeSpan = document.createElement('span');
      timeSpan.className = 'bubble-time';
      timeSpan.textContent = msg.time;
      footer.appendChild(timeSpan);

      if (msg.isOutgoing) {
        var tickWrap = document.createElement('span');
        tickWrap.innerHTML = msg.delivered ? doubleCheckSvg : singleCheckSvg;
        footer.appendChild(tickWrap);
      }

      bubble.appendChild(footer);
      row.appendChild(bubble);
      viewport.appendChild(row);
    });

    viewport.scrollTop = viewport.scrollHeight;
  }

  // ====================================================
  // РАДАР УСТРОЙСТВ ПОБЛИЗОСТИ (BLE ПРОХОЖИЕ И WI-FI)
  // ====================================================
  function getPeerMetadata(peer) {
    var id = (peer.id || '').toLowerCase();
    var name = (peer.name && peer.name !== peer.id) ? peer.name : '';
    var platform = 'Узел Mesh';
    var badgeClass = 'pill-mesh';
    var badgeText = 'MESH';
    var iconLetter = 'S';

    if (id.indexOf('syp-win-') !== -1 || name === 'Компьютер Windows') {
      platform = 'Компьютер Windows';
      badgeClass = 'pill-win';
      badgeText = 'WIN';
      iconLetter = 'W';
    } else if (id.indexOf('syp-droid-') !== -1 || name === 'Телефон Android') {
      platform = 'Телефон Android';
      badgeClass = 'pill-droid';
      badgeText = 'ANDROID';
      iconLetter = 'A';
    } else if (id.indexOf('syp-lin-') !== -1 || name === 'Компьютер Linux') {
      platform = 'Компьютер Linux';
      badgeClass = 'pill-linux';
      badgeText = 'LINUX';
      iconLetter = 'L';
    } else if (id.indexOf('syp-mac-') !== -1 || name === 'Компьютер Mac') {
      platform = 'Компьютер Mac';
      badgeClass = 'pill-apple';
      badgeText = 'MAC';
      iconLetter = 'M';
    } else if (id.indexOf('syp-ios-') !== -1 || name === 'Apple iPhone') {
      platform = 'Apple iPhone';
      badgeClass = 'pill-apple';
      badgeText = 'IOS';
      iconLetter = 'i';
    }

    var shortCode = id.replace(/syp-(win-|droid-|lin-|mac-|ios-|node-)?/g, '').toUpperCase();
    if (shortCode.length > 4) shortCode = shortCode.slice(-4);
    var title = (name && name !== id) ? name : (platform + ' #' + (shortCode || 'PEER'));

    return {
      title: title,
      platform: platform,
      badgeClass: badgeClass,
      badgeText: badgeText,
      iconLetter: iconLetter,
    };
  }

  var renderNearbyTimer = null;
  function scheduleRenderNearbyPeers() {
    if (renderNearbyTimer) return;
    renderNearbyTimer = setTimeout(function () {
      renderNearbyTimer = null;
      renderNearbyPeers();
    }, 350);
  }

  function renderNearbyPeers() {
    var list = document.getElementById('nearbyPeerList');
    var badge = document.getElementById('peerCounterBadge');
    var modalList = document.getElementById('discoveredPeersModalList');
    if (!list) return;

    var peers = Object.values(state.discoveredPeers);
    var now = Date.now();
    // Оставляем узлы, видимые последние 45 секунд
    peers = peers.filter(function (p) {
      return (now - p.lastSeen < 45000) && p.id && p.id.indexOf('syp-') === 0;
    });

    if (badge) badge.textContent = String(peers.length);

    var desc = document.getElementById('meshStatusDesc');
    if (desc) {
      desc.textContent =
        peers.length > 0
          ? peers.length + ' узел(ов) в радиусе радиоканала'
          : 'Сканирование BLE и UDP...';
    }

    if (peers.length === 0) {
      list.innerHTML =
        '<div class="empty-nearby-placeholder"><span>Поиск узлов по BLE и локальной сети...</span></div>';
      if (modalList) {
        modalList.innerHTML =
          '<span style="font-size:12px; color:var(--text-muted)">Нет активных узлов поблизости</span>';
      }
      return;
    }

    list.innerHTML = '';
    if (modalList) modalList.innerHTML = '';

    peers.forEach(function (peer) {
      var row = document.createElement('div');
      row.className = 'nearby-peer-row';

      var meta = getPeerMetadata(peer);
      var grad = getAvatarColor(peer.id);
      var avatarStyle = 'background: linear-gradient(135deg, ' + grad[0] + ', ' + grad[1] + ')';

      var metaText = '';
      if (peer.transport === 'BLE') {
        metaText = 'BLE ~' + (peer.distance || '1.5') + 'м (' + (peer.rssi || '-60') + ' dBm)';
      } else {
        metaText = 'Wi-Fi: ' + (peer.ip || 'локально');
      }

      row.innerHTML =
        '<div class="peer-row-left">' +
        '<div class="nearby-avatar" style="' + avatarStyle + '">' +
        escapeHtml(meta.iconLetter) +
        '</div>' +
        '<div class="nearby-info">' +
        '<div class="nearby-title-row">' +
        '<span class="nearby-name">' + escapeHtml(meta.title) + '</span>' +
        '<span class="platform-pill ' + meta.badgeClass + '">' + escapeHtml(meta.badgeText) + '</span>' +
        '</div>' +
        '<div class="nearby-meta-row">' +
        '<span class="nearby-id">' + escapeHtml(peer.id) + '</span> • ' +
        '<span class="nearby-meta">' + escapeHtml(metaText) + '</span>' +
        '</div>' +
        '</div>' +
        '</div>' +
        '<button class="peer-action-btn" title="Открыть личный диалог с этим устройством">Написать</button>';

      var writeBtn = row.querySelector('.peer-action-btn');
      if (writeBtn) {
        writeBtn.onclick = function (e) {
          e.stopPropagation();
          startDirectChatWithPeer(peer.id, meta.title);
        };
      }

      row.onclick = function () {
        startDirectChatWithPeer(peer.id, meta.title);
      };

      list.appendChild(row);

      // В модальное окно быстрого выбора
      if (modalList) {
        var quickItem = document.createElement('div');
        quickItem.className = 'nearby-peer-row';
        quickItem.style.cursor = 'pointer';
        quickItem.innerHTML =
          '<div style="display:flex; justify-content:space-between; align-items:center; width:100%">' +
          '<span style="font-family:var(--font-mono); font-size:12px; color:var(--text-primary)">' +
          escapeHtml(meta.title) +
          '</span><span class="platform-pill ' + meta.badgeClass + '">' +
          escapeHtml(meta.badgeText) +
          '</span></div>';
        quickItem.onclick = function () {
          var input = document.getElementById('newChatPeerIdInput');
          if (input) input.value = peer.id;
        };
        modalList.appendChild(quickItem);
      }
    });
  }

  function startDirectChatWithPeer(peerId, peerName) {
    if (peerId === state.identity.myId) return;

    var existing = state.chats.find(function (c) {
      return c.id === peerId;
    });

    var meta = getPeerMetadata({ id: peerId, name: peerName });

    if (!existing) {
      var newChat = {
        id: peerId,
        name: meta.title,
        avatar: meta.iconLetter,
        status: 'Личный диалог E2EE • Только вы двое',
        lastMessage: 'Прямой зашифрованный канал открыт',
        time: formatTime(new Date()),
        unread: 0,
        messages: [
          {
            id: 'info_' + Date.now(),
            sender: 'system',
            authorName: 'СИСТЕМА',
            type: 'text',
            text: 'Открыт личный диалог с ' + meta.title + ' (' + peerId + '). Сообщения шифруются сквозным ключом X25519 & ChaCha20-Poly1305 и видны ТОЛЬКО вам двоим.',
            time: formatTime(new Date()),
            isOutgoing: false,
          },
        ],
      };
      state.chats.push(newChat);
      saveChats();
    } else if (peerName && existing.name === existing.id) {
      existing.name = meta.title;
      saveChats();
    }

    selectChat(peerId);
  }

  // ====================================================
  // СЕТЕВОЙ ТРАНСПОРТ И ПЕРЕДАЧА ПАКЕТОВ
  // ====================================================
  function initNetworkTransports() {
    // 1. Определение локального IP с десктоп сервера
    if (window.fetch) {
      fetch('/api/mesh/ip')
        .then(function (res) {
          return res.json();
        })
        .then(function (data) {
          if (data && data.ip) {
            state.localIp = data.ip;
            updateLocalIpUI(data.ip);
          }
        })
        .catch(function () {});
    }

    // 2. Server-Sent Events (SSE) для десктопа (Linux, macOS, Windows)
    if (window.EventSource) {
      try {
        var sse = new EventSource('/api/mesh/events');
        sse.onmessage = function (e) {
          if (e.data && window.onMeshPacketReceived) {
            window.onMeshPacketReceived(e.data);
          }
        };
      } catch (err) {}
    }
  }

  function updateLocalIpUI(ip) {
    var lbl = document.getElementById('settingsLocalIp');
    if (lbl) lbl.textContent = ip + ':7788';
  }

  window.onLocalIpDetected = function (ip) {
    if (ip) {
      state.localIp = ip;
      updateLocalIpUI(ip);
    }
  };

  // Точка входа для обнаружения прохожих по BLE из нативного сервиса
  window.onBlePeerDiscovered = function (peerId, rssi, distance, mac, name) {
    if (!peerId || peerId === state.identity.myId) return;
    state.discoveredPeers[peerId] = {
      id: peerId,
      name: name || peerId,
      transport: 'BLE',
      rssi: rssi,
      distance: distance,
      mac: mac,
      lastSeen: Date.now(),
    };
    scheduleRenderNearbyPeers();
  };

  // Точка входа входящих пакетов (UDP, HTTP, BLE GATT)
  window.onMeshPacketReceived = function (packetOrJson) {
    var packet = packetOrJson;
    if (typeof packetOrJson === 'string') {
      try {
        packet = JSON.parse(packetOrJson);
      } catch (e) {
        return;
      }
    }

    if (packet && packet.type === 'BLE_PEER_DISCOVERED') {
      window.onBlePeerDiscovered(packet.peerId, packet.rssi, packet.distance, packet.mac, packet.name);
      return;
    }

    handleIncomingPacket(packet);
  };

  function sendDiscoveryBeacon() {
    var beacon = {
      type: 'SYPH_BEACON',
      id: 'bcn_' + Date.now(),
      from: state.identity.myId,
      fromName: state.identity.displayName,
      senderIp: state.localIp,
      timestamp: Date.now(),
    };
    dispatchPacketToNetwork(beacon);
  }

  function dispatchPacketToNetwork(packet) {
    packet.senderIp = state.localIp;
    var raw = JSON.stringify(packet);

    // 1. Нативный Android мост
    if (window.AndroidBridge && window.AndroidBridge.sendMeshPacket) {
      try {
        window.AndroidBridge.sendMeshPacket(raw);
      } catch (e) {}
    }

    // 2. Десктопный локальный сервер
    if (window.fetch) {
      fetch('/api/mesh/send', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: raw,
      }).catch(function () {});
    }

    // 3. Прямой TCP HTTP Fallback на все известные IP (обход ограничений роутеров)
    state.knownPeerIps.forEach(function (peerIp) {
      if (peerIp && peerIp !== '127.0.0.1' && peerIp !== state.localIp && window.fetch) {
        // Отправка на порт 7789 (Android) и 7788 (Desktop)
        [7789, 7788].forEach(function (pt) {
          fetch('http://' + peerIp + ':' + pt + '/api/mesh/send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: raw,
            mode: 'no-cors',
          }).catch(function () {});
        });
      }
    });
  }

  function handleIncomingPacket(packet) {
    if (!packet || !packet.from) return;
    if (packet.from === state.identity.myId) return; // Игнорируем эхо своих же пакетов

    if (packet.senderIp && packet.senderIp !== '127.0.0.1') {
      state.knownPeerIps.add(packet.senderIp);
    }

    // Регистрируем обнаруженный узел в радаре
    state.discoveredPeers[packet.from] = {
      id: packet.from,
      name: packet.fromName || packet.from,
      transport: packet.senderIp ? 'Wi-Fi' : 'BLE',
      ip: packet.senderIp || '',
      lastSeen: Date.now(),
    };
    scheduleRenderNearbyPeers();

    // 1. Обработка подтверждения доставки (ACK)
    if (packet.type === 'SYPH_ACK' && packet.targetMsgId) {
      state.chats.forEach(function (chat) {
        chat.messages.forEach(function (msg) {
          if (msg.id === packet.targetMsgId && msg.isOutgoing) {
            msg.delivered = true;
          }
        });
      });
      saveChats();
      renderActiveChat();
      return;
    }

    var isForUs = packet.to === state.identity.myId;
    var isBroadcast = packet.to === 'broadcast' || !packet.to;

    if (!isForUs && !isBroadcast) return; // Пакет для другого узла

    // 2. Обработка текстового сообщения или файла
    if (packet.type === 'SYPH_MSG' || packet.type === 'SYPH_FILE') {
      var targetChatId = isBroadcast ? 'mesh-broadcast' : packet.from;
      var chat = state.chats.find(function (c) {
        return c.id === targetChatId;
      });

      if (!chat) {
        chat = {
          id: packet.from,
          name: packet.fromName || packet.from,
          avatar: (packet.fromName || packet.from).replace('syp-', '').charAt(0).toUpperCase(),
          status: 'Прямой зашифрованный канал',
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
        authorName: packet.fromName || packet.from.substring(0, 10),
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
        showToast('Новый пакет от ' + (packet.fromName || packet.from));
      }

      saveChats();
      renderChatList();
      if (state.activeChatId === chat.id) {
        renderActiveChat();
      }

      // Если сообщение адресовано персонально нам — отправляем подтверждение доставки (ACK)
      if (isForUs) {
        var ackPacket = {
          type: 'SYPH_ACK',
          id: 'ack_' + Date.now(),
          from: state.identity.myId,
          to: packet.from,
          targetMsgId: packet.id,
          timestamp: Date.now(),
        };
        dispatchPacketToNetwork(ackPacket);
      }
    }
  }

  // ====================================================
  // ОТПРАВКА СООБЩЕНИЙ И ВЛОЖЕНИЙ
  // ====================================================
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
      fromName: state.identity.displayName,
      to: chat.id === 'mesh-broadcast' ? 'broadcast' : chat.id,
      text: text,
      timestamp: now.getTime(),
    };

    var newMsg = {
      id: packet.id,
      sender: 'me',
      authorName: state.identity.displayName,
      type: 'text',
      text: text,
      time: timeStr,
      isOutgoing: true,
      delivered: false,
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
    showToast('Сообщение отправлено в радиоканал');
  }

  function handleFileSelected(e) {
    var file = e.target.files && e.target.files[0];
    if (!file) return;

    if (file.size > 8 * 1024 * 1024) {
      alert('Размер файла превышает 8 МБ. Для радиопередачи выберите файл меньшего размера.');
      e.target.value = '';
      return;
    }

    showToast('Чтение файла: ' + file.name);

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
        id: 'file_' + Date.now() + '_' + Math.random().toString(36).substr(2, 6),
        from: state.identity.myId,
        fromName: state.identity.displayName,
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
        authorName: state.identity.displayName,
        type: 'file',
        fileName: file.name,
        fileSize: file.size,
        fileType: file.type,
        fileData: dataUrl,
        time: timeStr,
        isOutgoing: true,
        delivered: false,
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

  // ====================================================
  // СЛУШАТЕЛИ СОБЫТИЙ
  // ====================================================
  function setupEventListeners() {
    // 1. Отправка сообщений
    var sendBtn = document.getElementById('btnSend');
    if (sendBtn) {
      sendBtn.onclick = function (e) {
        e.preventDefault();
        sendMessage();
      };
    }

    var msgInput = document.getElementById('messageInput');
    if (msgInput) {
      msgInput.onkeydown = function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
          e.preventDefault();
          sendMessage();
        }
      };
    }

    // 2. Прикрепление файлов
    var attachBtn = document.getElementById('btnAttach');
    var fileInput = document.getElementById('fileInput');
    if (attachBtn && fileInput) {
      attachBtn.onclick = function (e) {
        e.preventDefault();
        fileInput.click();
      };
      fileInput.onchange = handleFileSelected;
    }

    // 3. Навигация на смартфонах
    var backBtn = document.getElementById('btnBackToChats');
    if (backBtn) {
      backBtn.onclick = function (e) {
        e.preventDefault();
        var layout = document.getElementById('appLayout');
        if (layout) layout.classList.remove('mobile-chat-open');
      };
    }

    // 4. Копирование ID
    var myNodeCard = document.getElementById('myNodeCard');
    var copyBtn = document.getElementById('btnCopyMyId');
    function copyId(e) {
      if (e) e.stopPropagation();
      copyToClipboard(state.identity.myId, 'Ваш Sypher ID скопирован');
    }
    if (myNodeCard) myNodeCard.onclick = copyId;
    if (copyBtn) copyBtn.onclick = copyId;

    // 5. Поиск
    var searchInput = document.getElementById('searchInput');
    if (searchInput) {
      searchInput.oninput = function () {
        state.searchQuery = searchInput.value;
        renderChatList();
      };
    }

    // 6. Модальное окно нового диалога
    var newChatModal = document.getElementById('newChatModal');
    var btnNewChat = document.getElementById('btnNewChat');
    var btnCloseNewChat = document.getElementById('btnCloseNewChat');
    var btnCancelNewChat = document.getElementById('btnCancelNewChat');
    var btnConfirmNewChat = document.getElementById('btnConfirmNewChat');
    var newChatInput = document.getElementById('newChatPeerIdInput');

    if (btnNewChat && newChatModal) {
      btnNewChat.onclick = function () {
        if (newChatInput) newChatInput.value = '';
        renderNearbyPeers();
        newChatModal.classList.remove('hidden');
      };
    }
    function closeNewChat() {
      if (newChatModal) newChatModal.classList.add('hidden');
    }
    if (btnCloseNewChat) btnCloseNewChat.onclick = closeNewChat;
    if (btnCancelNewChat) btnCancelNewChat.onclick = closeNewChat;

    if (btnConfirmNewChat && newChatInput) {
      btnConfirmNewChat.onclick = function () {
        var id = newChatInput.value.trim();
        if (!id) return;
        closeNewChat();
        startDirectChatWithPeer(id);
      };
    }

    // 7. Модальное окно настроек
    var settingsModal = document.getElementById('settingsModal');
    var btnSettings = document.getElementById('btnSettings');
    var btnCloseSettings = document.getElementById('btnCloseSettings');
    var btnSaveSettings = document.getElementById('btnSaveSettings');
    var inputName = document.getElementById('inputDisplayName');
    var btnRegen = document.getElementById('btnRegenerateId');

    if (btnSettings && settingsModal) {
      btnSettings.onclick = function () {
        settingsModal.classList.remove('hidden');
      };
    }
    function closeSettings() {
      if (settingsModal) settingsModal.classList.add('hidden');
    }
    if (btnCloseSettings) btnCloseSettings.onclick = closeSettings;

    if (btnSaveSettings && inputName) {
      btnSaveSettings.onclick = function () {
        var newName = inputName.value.trim();
        if (newName) {
          state.identity.displayName = newName;
          localStorage.setItem('sypher_node_identity_v3', JSON.stringify(state.identity));
          var myNameElem = document.getElementById('myNodeDisplayName');
          if (myNameElem) myNameElem.textContent = newName;
        }
        closeSettings();
        showToast('Настройки сохранены');
      };
    }

    if (btnRegen) {
      btnRegen.onclick = function () {
        if (confirm('Сгенерировать абсолютно новый уникальный Sypher ID? Все текущие контакты должны будут использовать новый ID.')) {
          localStorage.removeItem('sypher_node_identity_v3');
          state.identity = getOrCreateIdentity();
          initUI();
          closeSettings();
          showToast('Новый уникальный ID узла сгенерирован');
        }
      };
    }

    // 8. Модальное окно сверки отпечатка ключей
    var verifyModal = document.getElementById('verifyKeysModal');
    var btnVerify = document.getElementById('btnVerifyKeys');
    var btnCloseVerify = document.getElementById('btnCloseVerifyKeys');
    var btnOkVerify = document.getElementById('btnOkVerifyKeys');

    if (btnVerify && verifyModal) {
      btnVerify.onclick = function () {
        verifyModal.classList.remove('hidden');
      };
    }
    function closeVerify() {
      if (verifyModal) verifyModal.classList.add('hidden');
    }
    if (btnCloseVerify) btnCloseVerify.onclick = closeVerify;
    if (btnOkVerify) btnOkVerify.onclick = closeVerify;

    // 9. Таймер самоуничтожения
    var btnSelfDestruct = document.getElementById('btnSelfDestruct');
    var selfDestructLabel = document.getElementById('selfDestructLabel');
    if (btnSelfDestruct && selfDestructLabel) {
      btnSelfDestruct.onclick = function () {
        state.selfDestructIndex = (state.selfDestructIndex + 1) % state.selfDestructOptions.length;
        var opt = state.selfDestructOptions[state.selfDestructIndex];
        selfDestructLabel.textContent = opt;
        showToast('Таймер самоуничтожения: ' + opt);
      };
    }

    // 10. Экстренное стирание всех данных
    var btnBurn = document.getElementById('btnEmergencyBurn');
    if (btnBurn) {
      btnBurn.onclick = function () {
        if (
          confirm(
            'ВНИМАНИЕ: Выполнить экстренное стирание всех ключей, переписки и истории на данном устройстве?'
          )
        ) {
          localStorage.clear();
          showToast('Все данные стёрты');
          setTimeout(function () {
            location.reload();
          }, 600);
        }
      };
    }
  }

  // Запуск приложения
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initUI);
  } else {
    initUI();
  }
})();
