// Sypher Desktop Controller
const state = {
  myId: 'syph19k20fae37b981290aa3bc7',
  myFingerprint: '7F2A 9C81 D0E4 11B3 44FE 8812 90AA 3BC7',
  activeChatId: 'mesh-broadcast',
  chats: [
    {
      id: 'mesh-broadcast',
      name: '🌐 Local Mesh Broadcast',
      avatar: 'M',
      status: 'Ad-Hoc UDP/BLE Network',
      lastMessage: 'Beacon active: 3 nearby nodes detected',
      time: 'Just now',
      unread: 0,
      messages: [
        {
          id: '1',
          sender: 'system',
          text: 'Joined local mesh frequency (UDP: 239.255.77.88:7788). All packets routed via Epidemic Gossip.',
          time: '18:30',
          isOutgoing: false,
        },
        {
          id: '2',
          sender: 'syph1bob...449',
          text: 'Node Bob online. Relay service initialized.',
          time: '18:32',
          isOutgoing: false,
        },
      ],
    },
    {
      id: 'bob-direct',
      name: 'Bob (syph1bob...449)',
      avatar: 'B',
      status: 'Double Ratchet Active • 1-hop',
      lastMessage: 'Verified fingerprint matches!',
      time: '18:33',
      unread: 1,
      messages: [
        {
          id: 'm1',
          sender: 'syph1bob...449',
          text: 'Hey! Are you running the new Sypher build?',
          time: '18:31',
          isOutgoing: false,
        },
        {
          id: 'm2',
          sender: 'me',
          text: 'Yes! Completely decentralized, zero servers, full E2EE.',
          time: '18:32',
          isOutgoing: true,
        },
        {
          id: 'm3',
          sender: 'syph1bob...449',
          text: 'Verified fingerprint matches!',
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
      <div class="avatar">${chat.avatar}</div>
      <div class="chat-item-meta">
        <div class="chat-item-top">
          <span class="chat-item-name">${chat.name}</span>
          <span class="chat-item-time">${chat.time}</span>
        </div>
        <div class="chat-item-preview">${chat.lastMessage}</div>
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
    <span class="dot-online"></span> ${chat.status}
  `;

  const viewport = document.getElementById('messagesViewport');
  // Retain the top encryption notice
  const notice = viewport.querySelector('.encryption-notice');
  viewport.innerHTML = '';
  if (notice) viewport.appendChild(notice);

  chat.messages.forEach((msg) => {
    const row = document.createElement('div');
    row.className = `message-row ${msg.isOutgoing ? 'outgoing' : 'incoming'}`;

    row.innerHTML = `
      <div class="message-bubble">${escapeHtml(msg.text)}</div>
      <div class="message-info">
        <span>${msg.time}</span>
        ${msg.isOutgoing ? '<span>✓✓ E2EE</span>' : ''}
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
    if (confirm('EMERGENCY BURN: This will immediately overwrite and wipe all encryption keys, session ratchets, and local database with random noise. Are you sure?')) {
      alert('Vault zeroized and destroyed. Exiting Sypher.');
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
