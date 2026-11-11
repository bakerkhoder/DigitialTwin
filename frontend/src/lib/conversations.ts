import type { Message, Thread } from './api';

export const LEGACY_STORE = 'developertwin-v4-chat-demo';
export const MIGRATED_STORE = 'developertwin-v4-sqlite-imported';

// Browser history is untrusted input. It is imported only as visible messages,
// never as a fabricated MAF session or tool execution history.
export function parseLegacyConversations(value: string | null): Record<string, Thread> {
  try {
    const raw: unknown = JSON.parse(value || '{}');
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
    const result: Record<string, Thread> = {};
    for (const [id, item] of Object.entries(raw).slice(0, 30)) {
      if (!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(id) || !item || typeof item !== 'object' || Array.isArray(item)) continue;
      const entry = item as Record<string, unknown>;
      if (!Array.isArray(entry.messages)) continue;
      const messages: Message[] = entry.messages.filter((message: unknown): message is Message => {
        if (!message || typeof message !== 'object') return false;
        const m = message as Record<string, unknown>;
        return (m.role === 'assistant' || m.role === 'user') && typeof m.text === 'string';
      }).map(message => ({
        role: message.role, text: message.text, error: message.error === true,
        attachments: Array.isArray(message.attachments) ? message.attachments.filter((name): name is string => typeof name === 'string') : undefined
      }));
      result[id.toLowerCase()] = {
        title: typeof entry.title === 'string' && entry.title.trim() ? entry.title.slice(0, 200) : 'Conversation',
        created: typeof entry.created === 'number' && Number.isFinite(entry.created) && entry.created >= 0 ? Math.floor(entry.created) : Date.now(), messages
      };
    }
    return result;
  } catch { return {}; }
}
