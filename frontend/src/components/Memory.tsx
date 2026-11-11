import { useEffect, useState, type FormEvent } from 'react';
import { BrainCircuit, Pencil, Trash2 } from 'lucide-react';
import { api, type SavedMemory } from '../lib/api';

type Props = { activity: (name: string, detail: string) => void };

export default function Memory({ activity }: Props) {
  const [items, setItems] = useState<SavedMemory[]>([]);
  const [content, setContent] = useState('');
  const [kind, setKind] = useState<SavedMemory['kind']>('preference');
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const refresh = async () => {
    setLoading(true);
    try { setItems(await api<SavedMemory[]>('/api/memories')); setError(''); }
    catch (failure) { setError((failure as Error).message); }
    finally { setLoading(false); }
  };
  useEffect(() => { void refresh(); }, []);

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (busy || loading || !content.trim()) return;
    setBusy(true); setError('');
    try {
      const saved = await api<SavedMemory>(editing ? `/api/memories/${editing}` : '/api/memories', {
        method: editing ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ content, kind })
      });
      setItems(previous => [saved, ...previous.filter(item => item.id !== saved.id)]);
      setContent(''); setEditing(null);
      activity('Memory saved', 'Your preference is available in new questions.');
    } catch (failure) { setError((failure as Error).message); }
    finally { setBusy(false); }
  };
  const forget = async (item: SavedMemory) => {
    if (busy || loading || !window.confirm('Forget this saved memory? Previous chat messages will remain in conversation history.')) return;
    setBusy(true); setError('');
    try {
      await api(`/api/memories/${item.id}`, { method: 'DELETE' });
      setItems(previous => previous.filter(memory => memory.id !== item.id));
      if (editing === item.id) { setEditing(null); setContent(''); }
      activity('Memory forgotten', 'The saved preference was deleted.');
    } catch (failure) { setError((failure as Error).message); }
    finally { setBusy(false); }
  };

  return <section className="memory-page">
    <div className="eyebrow">PERSONAL PREFERENCES</div>
    <h1><BrainCircuit size={25}/> Saved memory</h1>
    <p>Save useful facts or preferences for future conversations. Only what you explicitly save here becomes a memory.</p>
    <form className="memory-form" onSubmit={event => void save(event)}>
      <label htmlFor="memory-kind">Use this as</label>
      <select id="memory-kind" value={kind} disabled={busy || loading} onChange={event => setKind(event.target.value as SavedMemory['kind'])}>
        <option value="preference">Response preference</option><option value="fact">Personal fact</option>
      </select>
      <small>Recent response preferences help shape answers. Personal facts are retrieved when relevant to your question.</small>
      <label htmlFor="memory-content">{editing ? 'Edit memory' : 'What should DeveloperTwin remember?'}</label>
      <textarea id="memory-content" maxLength={1000} rows={3} value={content} disabled={busy || loading}
        placeholder="For example: I prefer Celsius and concise answers in English."
        onChange={event => setContent(event.target.value)}/>
      <div className="memory-actions"><button type="submit" disabled={busy || loading || !content.trim()}>{editing ? 'Save changes' : 'Remember'}</button>
        {editing && <button type="button" disabled={busy} onClick={() => { setEditing(null); setContent(''); }}>Cancel</button>}</div>
    </form>
    {error && <div role="alert" className="storage-warning">{error}<button disabled={busy || loading} onClick={() => void refresh()}>Reload memories</button></div>}
    {loading ? <p role="status">Loading saved memories…</p> : !items.length ? <p>No saved memories yet.</p> :
      <div className="memory-list">{items.map(item => <article className="memory-card" key={item.id}>
        <p>{item.content}</p><small>{item.kind === 'preference' ? 'Response preference' : 'Personal fact'} · Saved {new Date(item.updated).toLocaleDateString()}</small>
        <div className="memory-actions"><button disabled={busy || loading} onClick={() => { setEditing(item.id); setContent(item.content); setKind(item.kind); }}><Pencil size={14}/> Edit</button>
          <button disabled={busy || loading} onClick={() => void forget(item)}><Trash2 size={14}/> Forget</button></div>
      </article>)}</div>}
  </section>;
}
