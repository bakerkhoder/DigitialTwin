import { useCallback, useEffect, useState } from 'react';
import { api, type ConversationDetail, type ConversationSummary, type Thread } from './api';
import { LEGACY_STORE, MIGRATED_STORE, parseLegacyConversations } from './conversations';

export function useConversationHistory(active: string) {
  const [threads, setThreads] = useState<Record<string, Thread>>({});
  const [loaded, setLoaded] = useState<Set<string>>(new Set());
  const [ready, setReady] = useState(false);
  const [error, setError] = useState('');
  const [revision, setRevision] = useState(0);
  const retry = useCallback(() => { setReady(false); setError(''); setRevision(value => value + 1); }, []);
  const markLoaded = useCallback((id: string) => setLoaded(items => new Set(items).add(id)), []);

  useEffect(() => {
    let cancelled = false;
    async function initialize() {
      try {
        let legacy: Record<string, Thread> = {};
        try {
          if (!localStorage.getItem(MIGRATED_STORE)) legacy = parseLegacyConversations(localStorage.getItem(LEGACY_STORE));
        } catch { /* Server history works even when browser storage is disabled. */ }
        if (Object.keys(legacy).length) {
          await api('/api/agent/sessions/import', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(Object.entries(legacy).map(([id, thread]) => ({ id, ...thread })))
          });
        }
        const summaries = await api<ConversationSummary[]>('/api/agent/sessions');
        if (cancelled) return;
        setThreads(Object.fromEntries(summaries.map(item => [item.id, { ...item, messages: [] }])));
        setLoaded(new Set());
        setReady(true);
        setError('');
        try { localStorage.setItem(MIGRATED_STORE, 'true'); localStorage.removeItem(LEGACY_STORE); } catch { /* Import is idempotent. */ }
      } catch (failure) {
        if (!cancelled) { setReady(false); setError(`Could not load saved conversations. ${(failure as Error).message}`); }
      }
    }
    void initialize();
    return () => { cancelled = true; };
  }, [revision]);

  const needsLoad = ready && !!threads[active] && !loaded.has(active);
  useEffect(() => {
    if (!needsLoad) return;
    const controller = new AbortController();
    let cancelled = false;
    void api<ConversationDetail>(`/api/agent/sessions/${active}`, { signal: controller.signal })
      .then(item => {
        if (cancelled) return;
        setThreads(items => ({ ...items, [active]: item }));
        markLoaded(active);
        setError('');
      }).catch(failure => {
        if (!cancelled) setError(`Could not restore this conversation. ${(failure as Error).message}`);
      });
    return () => { cancelled = true; controller.abort(); };
  }, [active, needsLoad, markLoaded, revision]);

  return { threads, setThreads, ready, loading: !ready || needsLoad, error, setError, retry, markLoaded };
}
