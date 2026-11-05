import { useEffect, useState, type FormEvent } from 'react';
import { Database, UploadCloud, Search, RefreshCw, CheckCircle2, AlertCircle, FileText, HardDrive, Trash2, Loader2 } from 'lucide-react';
import { api, type KnowledgeHealth, type KnowledgeHit, type KnowledgeDocument } from '../lib/api';

type Outcome = { file: string; chunks?: number; error?: string };
type Props = { activity: (name: string, description: string) => void };
export default function Knowledge({ activity }: Props) {
  const [health, setHealth] = useState<KnowledgeHealth | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [loadingDocuments, setLoadingDocuments] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [progress, setProgress] = useState(false);
  const [results, setResults] = useState<Outcome[]>([]);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<KnowledgeHit[]>([]);
  const [busySearch, setBusySearch] = useState(false);
  const [error, setError] = useState('');
  const refreshDocuments = async () => {
    setLoadingDocuments(true);
    try { setDocuments(await api<KnowledgeDocument[]>('/api/knowledge/documents')); }
    catch (e) { setError((e as Error).message); }
    finally { setLoadingDocuments(false); }
  };
  const deleteDocument = async (doc: KnowledgeDocument) => {
    if (!window.confirm(`Remove '${doc.source}' from the knowledge base? This permanently deletes its ${doc.chunks} indexed chunks and vectors, but not the original file on your computer.`)) return;
    setDeletingId(doc.id); setError('');
    try {
      await api<void>(`/api/knowledge/documents/${encodeURIComponent(doc.id)}`, { method: 'DELETE' });
      setDocuments(old => old.filter(item => item.id !== doc.id));
      setHits([]); // Old search results are invalid after deletion.
      activity('Knowledge removed', `${doc.source}: vectors and passages deleted; server agent sessions reset`);
    } catch (e) { setError((e as Error).message); }
    finally { setDeletingId(null); }
  };
  const checkHealth = () => api<KnowledgeHealth>('/api/knowledge/health').then(setHealth).catch(e => setHealth({ qdrant: false, ollama: false, message: e.message }));
  useEffect(() => { void checkHealth(); void refreshDocuments(); }, []);
  const ingest = async (e: FormEvent) => {
    e.preventDefault(); if (!files.length) return;
    setProgress(true); setError('');
    const body = new FormData(); files.forEach(f => body.append('files', f));
    try {
      const response = await api<{ results: Outcome[] }>('/api/knowledge/batch', { method: 'POST', body });
      setResults(response.results); activity('Knowledge indexed', `${response.results.filter(r => !r.error).length}/${files.length} files succeeded`);
      setFiles([]); setHits([]); await refreshDocuments();
    } catch (e) { setError((e as Error).message); activity('Knowledge failed', (e as Error).message); }
    finally { setProgress(false); void checkHealth(); }
  };
  const search = async (e: FormEvent) => {
    e.preventDefault(); if (!query.trim()) return; setBusySearch(true); setError('');
    try { setHits(await api<KnowledgeHit[]>(`/api/knowledge/search?q=${encodeURIComponent(query)}`)); }
    catch (e) { setError((e as Error).message); }
    finally { setBusySearch(false); }
  };
  return <div className="page-scroll">
    <div className="page-head"><div className="eyebrow">RETRIEVAL AUGMENTED GENERATION</div><h1>Knowledge library</h1><p>Index documents once, then let the agent retrieve the relevant passages as needed.</p></div>
    <div className="status-panel"><div className="status-icon"><Database size={23}/></div><div><strong>Vector database status</strong><p>{health?.message ?? 'Checking Qdrant and Ollama...'}</p><div className="status-pills"><span className={health?.qdrant ? 'pill ok' : 'pill bad'}>{health?.qdrant ? <CheckCircle2 size={13}/> : <AlertCircle size={13}/>} Qdrant</span><span className={health?.ollama ? 'pill ok' : 'pill bad'}>{health?.ollama ? <CheckCircle2 size={13}/> : <AlertCircle size={13}/>} Ollama embeddings</span></div></div><button className="ghost icon-only" onClick={checkHealth} aria-label="Recheck services"><RefreshCw size={17}/></button></div>
    {!health?.qdrant && <div className="help-banner"><HardDrive size={18}/><div><strong>Start your local vector database</strong><p>In the project's root folder run <code>docker compose up -d</code>, then open <code>http://localhost:6333/dashboard</code>. Ollama must also be running with <code>nomic-embed-text:latest</code>.</p></div></div>}
    <form className="panel" onSubmit={ingest}><div className="panel-title"><h2>Upload knowledge</h2><span>Up to 15 files · 8 MB each</span></div>
      <label className="drop-zone" onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); setFiles(Array.from(e.dataTransfer.files).slice(0,15)); }}><UploadCloud size={29}/><strong>Choose multiple files or drop them here</strong><span>PDF, text, code, JSON, Markdown and images</span><input type="file" multiple accept=".pdf,.txt,.md,.cs,.js,.ts,.tsx,.json,.xml,.sql,.html,.css,.csv,.png,.jpg,.jpeg,.webp" onChange={e => setFiles(Array.from(e.target.files ?? []).slice(0,15))}/></label>
      {files.length > 0 && <div className="file-rows">{files.map((f,i) => <div className="file-row" key={i}><FileText size={15}/>{f.name}<span>{(f.size/1024/1024).toFixed(2)} MB</span></div>)}</div>}
      <button className="primary" disabled={!files.length || progress || !health?.qdrant || !health?.ollama}>{progress ? 'Embedding files...' : 'Embed into knowledge base'}</button>
      {results.length > 0 && <div className="results-list">{results.map((r,i) => <div className={`result ${r.error ? 'failed' : 'passed'}`} key={i}><strong>{r.file}</strong><span>{r.error ?? `${r.chunks} chunks stored in Qdrant`}</span></div>)}</div>}
    </form>
    <section className="panel" aria-label="Indexed documents">
      <div className="panel-title"><h2>Indexed documents ({documents.length})</h2>
        <button type="button" className="secondary" disabled={loadingDocuments} onClick={() => void refreshDocuments()}><RefreshCw size={14}/> Refresh</button></div>
      <p className="knowledge-hint">Re-upload a file with the <strong>same filename</strong> to replace its earlier embeddings and remove stale chunks. Updates reset the backend agent session context so old retrieved facts are not reused; your visible chat history remains. Use unique filenames for unrelated documents. Deleting removes Qdrant vectors and text payload; it does not remove files from your PC.</p>
      {loadingDocuments && <p className="small-muted"><Loader2 size={13}/> Loading document index...</p>}
      {!loadingDocuments && documents.length === 0 && <div className="empty compact-empty">No indexed files yet. Upload your TXT knowledge examples above.</div>}
      <div className="indexed-documents">{documents.map(doc =>
        <div className="indexed-document" key={doc.id}>
          <FileText size={18}/><div className="doc-summary"><strong title={doc.source}>{doc.source}</strong>
            <small>{doc.chunks} chunks · version {doc.revision.slice(0,10)} · {doc.indexedAtUtc ? new Date(doc.indexedAtUtc).toLocaleString() : 'Legacy record'}</small></div>
          <button type="button" className="delete-action" disabled={deletingId !== null} aria-label={`Delete ${doc.source} from knowledge`}
            onClick={() => void deleteDocument(doc)}>{deletingId===doc.id ? <Loader2 size={15}/> : <Trash2 size={15}/>} Delete</button>
        </div>)}
      </div>
    </section>
    <form className="panel" onSubmit={search}><div className="panel-title"><h2>Semantic knowledge search</h2><span>Returns source passages and similarity scores</span></div><div className="search-row"><Search size={18}/><input placeholder="Example: What are the approval rules?" value={query} onChange={e => setQuery(e.target.value)}/><button className="primary" disabled={busySearch}>{busySearch ? 'Searching...' : 'Search'}</button></div>
      {hits.map((h,i) => <div className="knowledge-hit" key={i}><strong>{h.source}</strong><span>Score {h.score.toFixed(2)}</span><p>{h.text.slice(0,1300)}</p></div>)}
      {hits.length === 0 && query && !busySearch && <p className="small-muted">No results yet. Ingest a document and search again.</p>}
    </form>
    {error && <div className="alert error-alert"><AlertCircle size={18}/>{error}</div>}
  </div>;
}
