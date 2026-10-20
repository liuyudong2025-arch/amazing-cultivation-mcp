import crypto from 'node:crypto';

function frameText(text) {
  const payload = Buffer.from(text, 'utf8');
  let header;
  if (payload.length < 126) {
    header = Buffer.from([0x81, payload.length]);
  } else if (payload.length <= 0xffff) {
    header = Buffer.alloc(4);
    header[0] = 0x81; header[1] = 126; header.writeUInt16BE(payload.length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x81; header[1] = 127; header.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  return Buffer.concat([header, payload]);
}

export class WebSocketPeer {
  constructor(socket) {
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    this.fragments = [];
    this.fragmentOpcode = null;
    this.closed = false;
    this.onMessage = () => {};
    this.onClose = () => {};
    socket.on('data', (chunk) => this.#receive(chunk));
    socket.on('close', () => this.#closed());
    socket.on('error', () => this.#closed());
  }

  send(value) {
    if (this.closed) throw new Error('Game bridge is disconnected');
    this.socket.write(frameText(typeof value === 'string' ? value : JSON.stringify(value)));
  }

  close() {
    if (!this.closed) this.socket.end(Buffer.from([0x88, 0x00]));
    this.#closed();
  }

  #closed() {
    if (this.closed) return;
    this.closed = true;
    this.onClose();
  }

  #receive(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 2) {
      const first = this.buffer[0];
      const second = this.buffer[1];
      const final = (first & 0x80) !== 0;
      const opcode = first & 0x0f;
      const masked = (second & 0x80) !== 0;
      let length = second & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (this.buffer.length < 4) return;
        length = this.buffer.readUInt16BE(2); offset = 4;
      } else if (length === 127) {
        if (this.buffer.length < 10) return;
        const wide = this.buffer.readBigUInt64BE(2);
        if (wide > BigInt(Number.MAX_SAFE_INTEGER)) return this.close();
        length = Number(wide); offset = 10;
      }
      const maskLength = masked ? 4 : 0;
      if (this.buffer.length < offset + maskLength + length) return;
      const mask = masked ? this.buffer.subarray(offset, offset + 4) : null;
      offset += maskLength;
      const payload = Buffer.from(this.buffer.subarray(offset, offset + length));
      this.buffer = this.buffer.subarray(offset + length);
      if (mask) for (let i = 0; i < payload.length; i += 1) payload[i] ^= mask[i % 4];
      if (opcode === 0x8) return this.close();
      if (opcode === 0x9) {
        const pong = Buffer.concat([Buffer.from([0x8a, payload.length]), payload]);
        this.socket.write(pong); continue;
      }
      if (opcode === 0x1 || opcode === 0x2) {
        if (final) {
          if (opcode === 0x1) this.onMessage(payload.toString('utf8'));
        } else {
          this.fragmentOpcode = opcode;
          this.fragments = [payload];
        }
        continue;
      }
      if (opcode === 0x0 && this.fragmentOpcode !== null) {
        this.fragments.push(payload);
        if (final) {
          const complete = Buffer.concat(this.fragments);
          const originalOpcode = this.fragmentOpcode;
          this.fragments = [];
          this.fragmentOpcode = null;
          if (originalOpcode === 0x1) this.onMessage(complete.toString('utf8'));
        }
      }
    }
  }
}

export function acceptWebSocket(request, socket, head, expectedToken) {
  const host = request.headers.host || '127.0.0.1';
  const url = new URL(request.url, `http://${host}`);
  const remote = socket.remoteAddress || '';
  const loopback = remote === '127.0.0.1' || remote === '::1' || remote === '::ffff:127.0.0.1';
  if (!loopback || url.pathname !== '/bridge' || url.searchParams.get('token') !== expectedToken) {
    socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');
    return null;
  }
  const key = request.headers['sec-websocket-key'];
  if (!key) { socket.destroy(); return null; }
  const accept = crypto.createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
  socket.write([
    'HTTP/1.1 101 Switching Protocols',
    'Upgrade: websocket',
    'Connection: Upgrade',
    `Sec-WebSocket-Accept: ${accept}`,
    '\r\n',
  ].join('\r\n'));
  const peer = new WebSocketPeer(socket);
  if (head?.length) peer.buffer = Buffer.concat([peer.buffer, head]);
  return peer;
}
