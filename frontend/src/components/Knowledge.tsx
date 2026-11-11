import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Database, UploadCloud, Search, RefreshCw, CheckCircle2, AlertCircle, FileText, HardDrive, Trash2, Loader2 } from 'lucide-react';
import { api, type KnowledgeHealth, type KnowledgeSearchResult, type KnowledgeDocument, type KnowledgeReviewPreview } from '../lib/api';

type Outcome = { file: string; chunks?: number; unchanged?: boolean; status?: string; reviewStatus?: string; extraction?: string; error?: string };
type Props = { activity: (name: string, description: string) => void };
export default function Knowledge({ activity }: Props) {
  const [health, setHealth] = useState<KnowledgeHealth | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [loadingDocuments, setLoadingDocuments] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [review, setReview] = useState<KnowledgeReviewPreview | null>(null);
  const [progress, setProgress] = useState(false);
  const [results, setResults] = useState<Outcome[]>([]);
  const [query, setQuery] = useState('');
  const [searchResult, setSearchResult] = useState<KnowledgeSearchResult | null>(null);
  const [allowExternalAnalysis, setAllowExternalAnalysis] = useState(false);
  const searchVersion = useRef(0);
  const clearSearch = () => { searchVersion.current++; setSearchResult(null); };
  const hits = searchResult?.hits ?? [];
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
      clearSearch();
      activity('Knowledge removed', `${doc.source}: vectors and passages deleted; server agent sessions reset`);
    } catch (e) { setError((e as Error).message); }
    finally { setDeletingId(null); }
  };
  const changeStatus = async (doc: KnowledgeDocument) => {
    const status = doc.status === 'deprecated' ? 'active' : 'deprecated';
    setDeletingId(doc.id); setError('');
    try {
      await api<void>(`/api/knowledge/documents/${encodeURIComponent(doc.id)}/status`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ status })
      });
      clearSearch();
      await refreshDocuments();
      activity(status === 'active' ? 'Knowledge restored' : 'Knowledge deprecated', doc.source);
    } catch (e) { setError((e as Error).message); }
    finally { setDeletingId(null); }
  };
  const openReview = async (doc: KnowledgeDocument) => {
    setError(''); setDeletingId(doc.id);
    try { setReview(await api<KnowledgeReviewPreview>(`/api/knowledge/documents/${doc.id}/review`)); }
    catch (e) { setError((e as Error).message); }
    finally { setDeletingId(null); }
  };
  const approveReview = async () => {
    if (!review) return;
    const { document } = review;
    setError(''); setDeletingId(document.id);
    try {
      await api<void>(`/api/knowledge/documents/${document.id}/review/approve`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ revision: document.revision })
      });
      setReview(null); clearSearch(); await refreshDocuments();
      activity('Document reviewed', document.source);
    } catch (e) { setError((e as Error).message); }
    finally { setDeletingId(null); }
  };
  const checkHealth = () => api<KnowledgeHealth>('/api/knowledge/health').then(setHealth).catch(e => setHealth({ qdrant: false, ollama: false, message: e.message }));
  useEffect(() => { void checkHealth(); void refreshDocuments(); }, []);
  const ingest = async (e: FormEvent) => {
    e.preventDefault(); if (!files.length) return;
    setProgress(true); setError('');
    const body = new FormData(); files.forEach(f => body.append('files', f));
    body.append('allowExternalAnalysis', String(allowExternalAnalysis));
    try {
      const response = await api<{ results: Outcome[] }>('/api/knowledge/batch', { method: 'POST', body });
      setResults(response.results); activity('Knowledge indexed', `${response.results.filter(r => !r.error).length}/${files.length} files succeeded`);
      setFiles([]); clearSearch(); setAllowExternalAnalysis(false); await refreshDocuments();
    } catch (e) { setError((e as Error).message); activity('Knowledge failed', (e as Error).message); }
    finally { setProgress(false); void checkHealth(); }
  };
  const search = async (e: FormEvent) => {
    e.preventDefault(); if (!query.trim()) return; setBusySearch(true); setError('');
    const version = ++searchVersion.current;
    setSearchResult(null);
    try {
      const result = await api<KnowledgeSearchResult>(`/api/knowledge/search/diagnostics?q=${encodeURIComponent(query)}`);
      if (version === searchVersion.current) setSearchResult(result);
    }
    catch (e) { if (version === searchVersion.current) setError((e as Error).message); }
    finally { setBusySearch(false); }
  };
  return <div className="page-scroll">
    <div className="page-head"><div className="eyebrow">RETRIEVAL AUGMENTED GENERATION</div><h1>Knowledge library</h1><p>Index documents once, then let the agent retrieve the relevant passages as needed.</p></div>
    <div className="status-panel"><div className="status-icon"><Database size={23}/></div><div><strong>Vector database status</strong><p>{health?.message ?? 'Checking Qdrant and Ollama...'}</p><div className="status-pills"><span className={health?.qdrant ? 'pill ok' : 'pill bad'}>{health?.qdrant ? <CheckCircle2 size={13}/> : <AlertCircle size={13}/>} Qdrant</span><span className={health?.ollama ? 'pill ok' : 'pill bad'}>{health?.ollama ? <CheckCircle2 size={13}/> : <AlertCircle size={13}/>} Ollama embeddings</span></div></div><button className="ghost icon-only" onClick={checkHealth} aria-label="Recheck services"><RefreshCw size={17}/></button></div>
    {!health?.qdrant && <div className="help-banner"><HardDrive size={18}/><div><strong>Start your local vector database</strong><p>In the project's root folder run <code>docker compose up -d</code>, then open <code>http://localhost:6333/dashboard</code>. Ollama must also be running with <code>nomic-embed-text:latest</code>.</p></div></div>}
    <form className="panel" onSubmit={ingest}><div className="panel-title"><h2>Upload knowledge</h2><span>Up to 15 files · 8 MB each</span></div>
      <label className="drop-zone" onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); setFiles(Array.from(e.dataTransfer.files).slice(0,15)); }}><UploadCloud size={29}/><strong>Choose multiple files or drop them here</strong><span>PDF, DOCX, text, code, JSON, Markdown and images</span><input type="file" multiple accept=".pdf,.docx,.txt,.md,.cs,.js,.jsx,.ts,.tsx,.json,.xml,.sql,.html,.css,.csv,.png,.jpg,.jpeg,.webp" onChange={e => setFiles(Array.from(e.target.files ?? []).slice(0,15))}/></label>
      <label className="external-analysis-option"><input type="checkbox" checked={allowExternalAnalysis} onChange={e => setAllowExternalAnalysis(e.target.checked)}/> Allow scanned PDFs and images in this upload to be sent to the configured external analyzer</label>
      <p className="small-muted">Searchable PDFs and DOCX are extracted locally. Scanned PDFs need OCR; images need a vision model.</p>
      {files.length > 0 && <div className="file-rows">{files.map((f,i) => <div className="file-row" key={i}><FileText size={15}/>{f.name}<span>{(f.size/1024/1024).toFixed(2)} MB</span></div>)}</div>}
      <button className="primary" disabled={!files.length || progress || !health?.qdrant}>{progress ? 'Processing files...' : 'Index knowledge'}</button>
      {results.length > 0 && <div className="results-list">{results.map((r,i) => <div className={`result ${r.error ? 'failed' : 'passed'}`} key={i}><strong>{r.file}</strong><span>{r.error ?? `${r.unchanged ? 'Unchanged — embeddings reused' : `${r.chunks} chunks indexed`} · ${r.reviewStatus === 'pending' ? 'Review required before retrieval' : r.status} · ${r.extraction}`}</span></div>)}</div>}
    </form>
    <section className="panel" aria-label="Indexed documents">
      <div className="panel-title"><h2>Indexed documents ({documents.length})</h2>
        <button type="button" className="secondary" disabled={loadingDocuments} onClick={() => void refreshDocuments()}><RefreshCw size={14}/> Refresh</button></div>
      <p className="knowledge-hint">Re-upload the <strong>same filename</strong> to replace a document. Identical text reuses its embeddings. Deprecated documents stay in the library and are excluded from search until restored; re-uploading keeps their status. Changes reset the agent’s conversation context; your visible chat history remains. Deleting permanently removes indexed text and vectors.</p>
      <p className="knowledge-hint">Documents flagged for suspicious instructions or possible credentials stay out of retrieval until you review them. Approval allows reference use; document text cannot grant project access or authorize actions.</p>
      {loadingDocuments && <p className="small-muted"><Loader2 size={13}/> Loading document index...</p>}
      {!loadingDocuments && documents.length === 0 && <div className="empty compact-empty">No indexed files yet. Upload your TXT knowledge examples above.</div>}
      <div className="indexed-documents">{documents.map(doc =>
        <div className="indexed-document" key={doc.id}>
          <FileText size={18}/><div className="doc-summary"><strong title={doc.source}>{doc.source}</strong>
            <small>{doc.status} · {doc.reviewStatus === 'pending' ? 'Review required' : doc.reviewStatus === 'approved' ? 'Reviewed' : doc.reviewStatus === 'legacy' ? 'Legacy screening on retrieval' : 'Screened'} · {doc.chunks} chunks · version {doc.revision.slice(0,10)}</small></div>
          {doc.reviewStatus === 'pending' && <button type="button" className="secondary" disabled={deletingId !== null || progress}
            onClick={() => void openReview(doc)}>Review</button>}
          <button type="button" className="secondary" disabled={deletingId !== null || progress}
            onClick={() => void changeStatus(doc)}>{doc.status === 'deprecated' ? 'Restore' : 'Deprecate'}</button>
          <button type="button" className="delete-action" disabled={deletingId !== null || progress} aria-label={`Delete ${doc.source} from knowledge`}
            onClick={() => void deleteDocument(doc)}>{deletingId===doc.id ? <Loader2 size={15}/> : <Trash2 size={15}/>} Delete</button>
        </div>)}
      </div>
    </section>
    {review && <section className="panel" aria-label="Document safety review">
      <div className="panel-title"><h2>Review {review.document.source}</h2><button type="button" className="secondary" onClick={() => setReview(null)}>Close</button></div>
      <p className="knowledge-hint">Flagged for: {(review.document.riskReasons ?? []).join(', ')}. Read the content before approving it for retrieval. Known credential patterns are redacted in this preview and in retrieval results.</p>
      {review.document.riskReasons?.includes('possible-credential') && <p className="knowledge-hint">Remove possible credentials from the original document and re-upload it before approval.</p>}
      <div className="review-document-text">{review.chunks.map(chunk => <div key={chunk.chunk}><strong>Chunk {chunk.chunk + 1}</strong><pre>{chunk.text}</pre></div>)}</div>
      <button type="button" className="primary" disabled={deletingId !== null || progress || review.document.riskReasons?.includes('possible-credential')} onClick={() => void approveReview()}>Approve this revision for retrieval</button>
    </section>}
    <form className="panel" onSubmit={search}><div className="panel-title"><h2>Knowledge search</h2><span>Retrieve candidates, rerank, then select passages</span></div><div className="search-row"><Search size={18}/><input maxLength={500} placeholder="Example: What are the approval rules?" value={query} onChange={e => setQuery(e.target.value)}/><button className="primary" disabled={busySearch}>{busySearch ? 'Searching...' : 'Search'}</button></div>
      {searchResult?.warning && <p role="status" className="knowledge-hint">{searchResult.warning}</p>}
      {hits.map(h => <div className="knowledge-hit" key={h.id}><strong>#{h.rank} {h.source}</strong><span>Vector #{h.vectorRank} · similarity {h.score.toFixed(3)}{h.rerankScore !== null && ` · relevance ${h.rerankScore.toFixed(1)}/100`}</span><p>{h.text.slice(0,1300)}</p></div>)}
      {!!searchResult?.candidates.length && <details className="retrieval-diagnostics"><summary>Compare all {searchResult.candidates.length} candidates</summary>
        <p className="small-muted">Reranker: {searchResult.reranker}{searchResult.fallbackUsed ? ' (vector fallback)' : ''}</p>
        <p className="small-muted">Relevance scores are model judgments, not probabilities. Up to five passages are passed to the agent.</p>
        <table><thead><tr><th>Source / chunk</th><th>Vector rank</th><th>Similarity</th><th>Relevance</th><th>Final rank</th><th>Selected</th></tr></thead>
          <tbody>{searchResult.candidates.map(h => <tr key={h.id}><td>{h.source} / {h.chunk + 1}</td><td>{h.vectorRank}</td><td>{h.score.toFixed(3)}</td><td>{h.rerankScore?.toFixed(1) ?? '—'}</td><td>{h.rank}</td><td>{hits.some(hit => hit.id === h.id) ? 'Yes' : 'No'}</td></tr>)}</tbody></table>
      </details>}
      {hits.length === 0 && query && !busySearch && <p className="small-muted">No results yet. Ingest a document and search again.</p>}
    </form>
    {error && <div className="alert error-alert"><AlertCircle size={18}/>{error}</div>}
  </div>;
}
