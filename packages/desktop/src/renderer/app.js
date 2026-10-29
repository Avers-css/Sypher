// Sypher Core Controller — Responsive Touch & Desktop Engine
(function () {
  'use strict';

  var state = {
    myId: 'syph19k20fae37b981290aa3bc7',
    myFingerprint: '7F2A 9C81 D0E4 11B3 44FE 8812 90AA 3BC7',
    activeChatId: 'mesh-broadcast',
    selfDestructIndex: 1,
    selfDestructOptions: ['1ч', '24ч', '7д', 'Выкл'],
    searchQuery: '',
    chats: [
      {
        id: 'mesh-broadcast',
        name: 'Локальный радиоканал Mesh',
        avatar: 'M',
        status: 'Ad-Hoc UDP/BLE Сеть (Без серверов)',
        lastMessage: 'Радиомаяк активен: 3 узла поблизости',
        time: 'Сейчас',
        unread: 0,
        messages: [
          {
            id: '1',
            sender: 'system',
            text: 'Подключено к локальной Mesh-сети (UDP: 239.255.77.88:7788). Все пакеты передаются напрямую без серверов.',
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
            text: 'Привет! Запустил Sypher на смартфоне?',
            time: '18:31',
            isOutgoing: false,
          },
          {
            id: 'm2',
            sender: 'me',
            text: 'Да! Децентрализация, ноль серверов, чистое E2EE шифрование.',
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

  function initUI() {
    var idElem = document.getElementById('mySypherId');
    if (idElem) {
      idElem.textContent = state.myId.substring(0, 16) + '...';
    }

    var settingsId = document.getElementById('settingsNodeId');
    if (settingsId) {
      settingsId.textContent = state.myId;
    }

    renderChatList();
    renderActiveChat();
    setupEventListeners();
  }

  function renderChatList() {
    var container = document.getElementById('chatList');
    if (!container) return;
    container.innerHTML = '';

    var filtered = state.chats.filter(function (chat) {
      if (!state.searchQuery) return true;
      var q = state.searchQuery.toLowerCase();
      return chat.name.toLowerCase().indexOf(q) !== -1 || chat.lastMessage.toLowerCase().indexOf(q) !== -1;
    });

    if (filtered.length === 0) {
      var empty = document.createElement('div');
      empty.style.padding = '20px';
      empty.style.textAlign = 'center';
      empty.style.color = 'var(--text-muted)';
      empty.style.fontSize = '13px';
      empty.textContent = 'Ничего не найдено';
      container.appendChild(empty);
      return;
    }

    filtered.forEach(function (chat) {
      var item = document.createElement('div');
      item.className = 'chat-item' + (chat.id === state.activeChatId ? ' active' : '');

      item.onclick = function () {
        selectChat(chat.id);
      };

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
        escapeHtml(chat.lastMessage) +
        '</div>' +
        '</div>';

      container.appendChild(item);
    });
  }

  function selectChat(chatId) {
    state.activeChatId = chatId;
    renderChatList();
    renderActiveChat();

    // На смартфонах переключаем экран со списка чатов на открытый диалог
    var layout = document.getElementById('appLayout');
    if (layout) {
      layout.classList.add('mobile-chat-open');
    }
  }

  function renderActiveChat() {
    var chat = state.chats.find(function (c) {
      return c.id === state.activeChatId;
    });
    if (!chat) return;

    var nameElem = document.getElementById('activeChatName');
    var statusElem = document.getElementById('activeChatStatusText');
    var avatarElem = document.getElementById('activeChatAvatar');

    if (nameElem) nameElem.textContent = chat.name;
    if (statusElem) statusElem.textContent = chat.status;
    if (avatarElem) avatarElem.textContent = chat.avatar;

    var viewport = document.getElementById('messagesViewport');
    if (!viewport) return;

    // Сохраняем уведомление о шифровании
    var notice = viewport.querySelector('.encryption-notice');
    viewport.innerHTML = '';
    if (notice) viewport.appendChild(notice);

    chat.messages.forEach(function (msg) {
      var row = document.createElement('div');
      row.className = 'message-row ' + (msg.isOutgoing ? 'outgoing' : 'incoming');

      var checkHtml = msg.isOutgoing ? ' ' + doubleCheckSvg : '';

      row.innerHTML =
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
    if (viewport) {
      viewport.scrollTop = viewport.scrollHeight;
    }
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
    var timeStr =
      String(now.getHours()).padStart(2, '0') + ':' + String(now.getMinutes()).padStart(2, '0');

    var newMsg = {
      id: 'msg_' + Date.now(),
      sender: 'me',
      text: text,
      time: timeStr,
      isOutgoing: true,
    };

    chat.messages.push(newMsg);
    chat.lastMessage = text;
    chat.time = timeStr;

    input.value = '';
    input.focus();

    renderChatList();
    renderActiveChat();
    showToast('Пакет отправлен в Mesh');

    // Имитация ответа узла в сети через 1.2 секунды
    setTimeout(function () {
      if (chat.id === 'bob-direct') {
        var replyMsg = {
          id: 'msg_reply_' + Date.now(),
          sender: 'syph1bob...449',
          text: 'Принято по каналу Double Ratchet! Пакет подтвержден.',
          time: timeStr,
          isOutgoing: false,
        };
        chat.messages.push(replyMsg);
        chat.lastMessage = replyMsg.text;
        renderChatList();
        if (state.activeChatId === 'bob-direct') {
          renderActiveChat();
        }
      }
    }, 1200);
  }

  function setupEventListeners() {
    // 1. Отправка по кнопке
    var sendBtn = document.getElementById('btnSend');
    if (sendBtn) {
      sendBtn.onclick = function (e) {
        e.preventDefault();
        sendMessage();
      };
    }

    // 2. Ввод с клавиатуры (Enter отправляет, Shift+Enter переносит строку)
    var input = document.getElementById('messageInput');
    if (input) {
      input.onkeydown = function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
          e.preventDefault();
          sendMessage();
        }
      };
    }

    // 3. Кнопка возврата на смартфонах (Back Button)
    var backBtn = document.getElementById('btnBack');
    if (backBtn) {
      backBtn.onclick = function (e) {
        e.preventDefault();
        var layout = document.getElementById('appLayout');
        if (layout) {
          layout.classList.remove('mobile-chat-open');
        }
      };
    }

    // 4. Поиск чатов
    var searchInput = document.getElementById('searchInput');
    if (searchInput) {
      searchInput.oninput = function () {
        state.searchQuery = searchInput.value;
        renderChatList();
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
      if (fpElem) fpElem.textContent = state.myFingerprint;
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

    // Копирование отпечатка
    var copyFpBtn = document.getElementById('btnCopyFp');
    if (copyFpBtn) {
      copyFpBtn.onclick = function (e) {
        e.preventDefault();
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(state.myFingerprint).then(function () {
            showToast('Отпечаток скопирован в буфер');
          });
        } else {
          showToast('Отпечаток скопирован');
        }
      };
    }

    // 6. Модальное окно настроек узла
    var settingsModal = document.getElementById('settingsModal');
    var settingsBtn = document.getElementById('btnSettings');
    var closeSettingsBtn = document.getElementById('btnCloseSettings');

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

    // 7. Таймер самоуничтожения (переключение 1ч -> 24ч -> 7д -> Выкл)
    var selfDestructBtn = document.getElementById('btnSelfDestruct');
    var selfDestructLabel = document.getElementById('selfDestructLabel');
    if (selfDestructBtn && selfDestructLabel) {
      selfDestructBtn.onclick = function (e) {
        e.preventDefault();
        state.selfDestructIndex = (state.selfDestructIndex + 1) % state.selfDestructOptions.length;
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
        showToast('Шифрование файла: выберите файл для Mesh передачи');
      };
    }

    // 9. Экстренное уничтожение данных (Emergency Burn Modal)
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
        // Затирание памяти
        state.chats.forEach(function (c) {
          c.messages = [];
          c.lastMessage = '[Стерто]';
        });
        burnModal.classList.add('hidden');
        renderChatList();
        renderActiveChat();
        showToast('Все ключи и сообщения затерты нулями');
        setTimeout(function () {
          window.location.reload();
        }, 1200);
      };
    }

    // 10. Клик вне модального окна закрывает его
    [verifyModal, settingsModal, burnModal].forEach(function (m) {
      if (m) {
        m.onclick = function (e) {
          if (e.target === m) {
            m.classList.add('hidden');
          }
        };
      }
    });

    // 11. Закрытие окон по нажатию Escape
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        if (verifyModal) verifyModal.classList.add('hidden');
        if (settingsModal) settingsModal.classList.add('hidden');
        if (burnModal) burnModal.classList.add('hidden');
      }
    });

    // 12. Баннер Mesh переключает на общий канал
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
