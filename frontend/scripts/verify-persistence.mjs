import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, server: { middlewareMode: true }, appType: 'custom' });
try {
  const { parseLegacyConversations } = await server.ssrLoadModule('/src/lib/conversations.ts');
  assert.deepEqual(parseLegacyConversations('broken JSON'), {});
  assert.deepEqual(parseLegacyConversations('[]'), {});
  const id = 'AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA';
  const imported = parseLegacyConversations(JSON.stringify({
    [id]: { title: 'Earlier chat', created: 123, messages: [
      { role: 'user', text: 'مرحبا', attachments: ['file.pdf', 123] },
      { role: 'assistant', text: 'Previous error', error: true },
      { role: 'tool', text: 'Untrusted execution state' }
    ] }, invalid: { title: 'Invalid identifier', messages: [] }
  }));
  assert.equal(Object.keys(imported).length, 1);
  assert.equal(imported[id.toLowerCase()].messages.length, 2);
  assert.equal(imported[id.toLowerCase()].messages[0].text, 'مرحبا');
  assert.deepEqual(imported[id.toLowerCase()].messages[0].attachments, ['file.pdf']);
  assert.equal(imported[id.toLowerCase()].messages[1].error, true);

  const { default: Chat } = await server.ssrLoadModule('/src/components/Chat.tsx');
  const html = renderToStaticMarkup(React.createElement(Chat, {
    messages: [], busy: false, loadingHistory: true, draft: 'A pending question',
    onSend() {}, onStop() {}, onExport() {}, onReview() {}, setDraft() {}
  }));
  assert.ok(html.includes('Loading saved conversation'));
  assert.match(html, /<button[^>]*disabled=""[^>]*class="send-button"/);
  const { default: Memory } = await server.ssrLoadModule('/src/components/Memory.tsx');
  const memories = renderToStaticMarkup(React.createElement(Memory, { activity() {} }));
  assert.ok(memories.includes('Remember'));
  assert.ok(memories.includes('Response preference'));
  assert.ok(memories.includes('Personal fact'));
  assert.ok(memories.includes('Only what you explicitly save here becomes a memory'));
  console.log('Persistence UI checks passed: legacy migration, history loading and explicit memory controls.');
} finally { await server.close(); }
