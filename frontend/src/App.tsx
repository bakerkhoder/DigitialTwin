import { useEffect, useRef, useState } from 'react';
import { Bot, BrainCircuit, FolderGit2, ShieldCheck, Plus, Activity, Sparkles, RefreshCw, PanelRightClose, PanelRightOpen, Circle, ChevronRight, Info, Trash2 } from 'lucide-react';
import Chat from './components/Chat';
import Knowledge from './components/Knowledge';
import Projects from './components/Projects';
import Approvals from './components/Approvals';
import { api, downloadReport, downloadDiagramPng, type ProviderStatus, type KnowledgeHealth, type Message, type Thread } from './lib/api';

type View = 'chat' | 'knowledge' | 'projects' | 'approvals';
type LogItem = { name: string; detail: string; time: number };
const STORE = 'developertwin-v4-chat-demo';
const ACTIVE_STORE = 'developertwin-v4-active';
const newId = () => crypto.randomUUID();

// Browser storage is external input: it can be old, malformed, or unavailable.
// Validating it prevents a previous chat record from crashing the entire UI.
function load(): Record<string, Thread> {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(STORE) || '{}');
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
    const result: Record<string, Thread> = {};
    for (const [id, item] of Object.entries(raw).slice(0, 30)) {
      if (!item || typeof item !== 'object' || Array.isArray(item)) continue;
      const entry = item as Record<string, unknown>;
      if (!Array.isArray(entry.messages)) continue;
      const messages: Message[] = entry.messages.filter((message: unknown): message is Message => {
        if (!message || typeof message !== 'object') return false;
        const m = message as Record<string, unknown>;
        return (m.role === 'assistant' || m.role === 'user') && typeof m.text === 'string';
      }).map(message => ({
        role: message.role,
        text: message.text,
        error: message.error === true,
        attachments: Array.isArray(message.attachments)
          ? message.attachments.filter((name): name is string => typeof name === 'string')
          : undefined
      }));
      result[id] = {
        title: typeof entry.title === 'string' ? entry.title : 'Conversation',
        created: typeof entry.created === 'number' && Number.isFinite(entry.created) ? entry.created : Date.now(),
        messages
      };
    }
    return result;
  } catch (error) {
    console.warn('Saved conversations could not be loaded:', error);
    return {};
  }
}
function loadActiveId(): string {
  try { return localStorage.getItem(ACTIVE_STORE) || newId(); }
  catch { return newId(); }
}

export default function App() {
  const [view, setView] = useState<View>('chat');
  const [threads, setThreads] = useState<Record<string, Thread>>(load);
  const [active, setActive] = useState<string>(loadActiveId);
  const [pendingIds, setPendingIds] = useState<string[]>([]);
  const [historyDeleting, setHistoryDeleting] = useState(false);
  const busy = pendingIds.includes(active);
  const [draft, setDraft] = useState('');
  const [storageWarning, setStorageWarning] = useState('');
  const [status, setStatus] = useState<ProviderStatus | null>(null);
  const [health, setHealth] = useState<KnowledgeHealth | null>(null);
  const [rightVisible, setRightVisible] = useState(true);
  const [logs, setLogs] = useState<LogItem[]>([{ name:'Workspace ready', detail:'DeveloperTwin UI loaded', time: Date.now() }]);
  // Each conversation owns its request. Changing tab/thread never aborts it.
  const controllers = useRef<Map<string, AbortController>>(new Map());
  const deletedIds = useRef<Set<string>>(new Set());
  const activeRef = useRef(active);
  useEffect(() => {
    activeRef.current = active;
    try { localStorage.setItem(ACTIVE_STORE, active); }
    catch (error) { console.warn('Unable to store the active conversation:', error); }
  }, [active]);
  useEffect(() => {
    try {
      localStorage.setItem(STORE, JSON.stringify(threads));
      setStorageWarning('');
    } catch (error) {
      // QuotaExceededError can occur with long AI reports. Never let a storage
      // exception unmount the application: the current conversation stays in memory.
      console.warn('Conversation history could not be saved:', error);
      setStorageWarning('Browser storage is full or disabled. You can continue chatting, but new history may not survive a reload. Export important reports.');
    }
  }, [threads]);
  const activity = (name: string, detail: string) => setLogs(items => [{ name, detail, time:Date.now() }, ...items].slice(0,16));
  const refreshStatus = async () => {
    try { setStatus(await api<ProviderStatus>('/api/status')); } catch { setStatus(null); }
    try { setHealth(await api<KnowledgeHealth>('/api/knowledge/health')); } catch { setHealth(null); }
  };
  useEffect(() => { void refreshStatus(); }, []);
  const updateMessages = (id:string, fn:(messages:Message[])=>Message[]) => setThreads(prev => {
    const prior = prev[id]; const messages = fn(prior?.messages || []);
    const revised = { ...prev, [id]: { title: prior?.title ?? messages[0]?.text.slice(0,55) ?? 'New conversation', created: prior?.created ?? Date.now(), messages } };
    const keys = Object.keys(revised).sort((a,b) => revised[b].created - revised[a].created);
    for (const key of keys.slice(30)) delete revised[key];
    return revised;
  });
  const openChat = (id:string) => { setActive(id); setView('chat'); };
  const stopActive = () => controllers.current.get(activeRef.current)?.abort();
  const deleteThread = async (id: string) => {
    if (!window.confirm('Delete this conversation and its in-memory agent session? This cannot be undone.')) return;
    deletedIds.current.add(id);
    controllers.current.get(id)?.abort();
    setPendingIds(old => old.filter(x => x !== id));
    setThreads(old => { const next = { ...old }; delete next[id]; return next; });
    if (activeRef.current === id) openChat(newId());
    try { await api<void>(`/api/agent/sessions/${id}`, { method: 'DELETE' }); }
    catch (error) { activity('Session cleanup warning', (error as Error).message); }
  };
  const deleteAllThreads = async () => {
    if (!window.confirm('Delete ALL saved conversations and their active agent sessions?')) return;
    setHistoryDeleting(true);
    const ids = Object.keys(threads);
    for (const id of ids) {
      deletedIds.current.add(id);
      controllers.current.get(id)?.abort();
    }
    controllers.current.clear();
    setThreads({}); setPendingIds([]); openChat(newId());
    try { await Promise.all(ids.map(id => api<void>(`/api/agent/sessions/${id}`, { method: 'DELETE' }).catch(() => {}))); }
    finally { setHistoryDeleting(false); }
  };
  const send = async (text:string, files:File[]) => {
    const id = activeRef.current;
    if (controllers.current.has(id)) return;
    const abort = new AbortController();
    controllers.current.set(id, abort);
    setPendingIds(old => [...old, id]);
    setView('chat');
    updateMessages(id, ms => [...ms, { role:'user', text, attachments:files.map(f => f.name) }]);
    activity('Agent running', files.length ? `${files.length} attachments · processing` : 'Waiting for model and tools');
    try {
      let result: { reply: string; sessionId: string };
      if (files.length) {
        const form = new FormData(); form.append('sessionId',id); form.append('message',text);
        files.forEach(f => form.append('files',f));
        result=await api('/api/chat/attachments',{method:'POST',body:form,signal:abort.signal});
      } else result=await api('/api/agent/ask',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sessionId:id,message:text}),signal:abort.signal});
      if (!deletedIds.current.has(id)) updateMessages(id, ms => [...ms, { role:'assistant',text:result.reply || 'No response was generated.' }]);
      activity('Response completed', 'Agent returned a response');
    } catch (e) {
      const error=(e as Error);
      if (!deletedIds.current.has(id)) updateMessages(id, ms => [...ms, { role:'assistant', error:true, text: error.name==='AbortError' ? 'Request cancelled.' : error.message }]);
      activity('Request ended', error.message);
    } finally {
      controllers.current.delete(id);
      setPendingIds(old => old.filter(x => x !== id));
    }
  };
  const exportAnswer = (content:string, format:'md'|'pdf'|'pptx'|'svg'|'png') => {
    const title = threads[active]?.title || 'Engineering handoff';
    void (format === 'png' ? downloadDiagramPng(title, content) : downloadReport(title, content, format)).then(()=>activity('Handoff exported',format.toUpperCase()))
      .catch(e=>{ activity('Export error',(e as Error).message); window.alert(`Export failed: ${(e as Error).message}`); });
  };
  const requestReview = (content:string) => {
    const title = threads[active]?.title || 'Engineering handoff';
    void api('/api/approvals/propose', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({title,content}) })
      .then(()=>{activity('Handoff submitted','Review requested');setView('approvals');})
      .catch(e=>window.alert(`Review request failed: ${(e as Error).message}`));
  };
  const askProject = (query:string) => { setDraft(query); setView('chat'); };
  const threadEntries = Object.entries(threads).sort((a,b) => b[1].created - a[1].created);
  const menu: {id:View;label:string;icon:typeof Bot}[] = [
    {id:'chat',label:'Assistant',icon:Bot},{id:'knowledge',label:'Knowledge library',icon:BrainCircuit},
    {id:'projects',label:'Projects & folders',icon:FolderGit2},{id:'approvals',label:'Approvals',icon:ShieldCheck}
  ];
  return <div className={`app-shell ${rightVisible ? '' : 'hide-right'}`}>
    <aside className="sidebar"><div className="brand"><div className="brand-glyph">◇</div><div><strong>DeveloperTwin</strong><small>ENGINEERING AGENT</small></div></div>
      <button className="new-chat" onClick={() => openChat(newId())}><Plus size={18}/> New conversation <kbd>Ctrl K</kbd></button>
      <div className="menu-label">WORKSPACE</div>
      <nav>{menu.map(item => <button key={item.id} className={`nav-link ${view===item.id?'active':''}`} onClick={()=>setView(item.id)}><item.icon size={18}/><span>{item.label}</span>{view===item.id&&<ChevronRight size={15}/>}</button>)}</nav>
      <div className="history-header"><div className="menu-label history-label">RECENT CONVERSATIONS</div>
        {threadEntries.length>0 && <button className="clear-history" disabled={historyDeleting} onClick={() => void deleteAllThreads()}>Clear all</button>}</div>
      <div className="history-list">{threadEntries.map(([id, thread]) =>
        <div className="history-row" key={id}>
          <button className={`history-link ${active===id&&view==='chat'?'selected':''}`} onClick={()=>openChat(id)} title={thread.title}>
            {pendingIds.includes(id) ? <span className="mini-pulse" title="Agent working"/> : <Circle size={9} fill="currentColor"/>}
            <span className="history-title">{thread.title}</span></button>
          <button className="history-delete" title={`Delete ${thread.title}`} aria-label={`Delete conversation ${thread.title}`}
            onClick={()=>void deleteThread(id)}><Trash2 size={14}/></button>
        </div>)}{!threadEntries.length&&<span className="small-muted">No previous conversations yet.</span>}</div>
      <div className="sidebar-bottom"><div className={`status-dot ${status?'ready':''}`}/><div><strong>{status ? `${status.provider} ready`:'Backend unavailable'}</strong><small>{status?.chatModel ?? 'Check dotnet run'}</small></div><button aria-label="Refresh status" onClick={refreshStatus}><RefreshCw size={15}/></button></div>
    </aside>
    <div className="main-frame"><header className="topbar"><div className="crumb">WORKSPACE <span>/</span> <strong>{menu.find(m=>m.id===view)?.label}</strong></div><div className="top-actions"><div className="provider-pill"><span className={status ? 'online':'offline'}/>{status?.provider?.toUpperCase() ?? 'OFFLINE'} · {status?.chatModel ?? 'Disconnected'}</div><button onClick={()=>setRightVisible(!rightVisible)} aria-label="Toggle activity panel" className="ghost icon-only">{rightVisible?<PanelRightClose size={18}/>:<PanelRightOpen size={18}/>}</button></div></header>
      <main className="main-area">{storageWarning && <div className="storage-warning" role="status">{storageWarning}<button type="button" onClick={() => setStorageWarning('')}>Dismiss</button></div>}{view==='chat' ? <Chat messages={threads[active]?.messages || []} busy={busy} onSend={send} onStop={stopActive} onExport={exportAnswer} onReview={requestReview} draft={draft} setDraft={setDraft}/>
        : view==='knowledge' ? <Knowledge activity={activity}/> : view==='projects' ? <Projects activity={activity} onAsk={askProject}/> : <Approvals activity={activity}/>}</main>
    </div>
    <aside className="activity-panel"><div className="activity-heading"><Activity size={17}/><strong>Agent activity</strong></div><p className="small-muted">Your operations, model and service health.</p>
      <div className="timeline">{logs.map((log,i)=><div className="timeline-item" key={log.time+i}><div className="timeline-icon">{i===0?<Sparkles size={15}/>:<Activity size={15}/>}</div><div><strong>{log.name}</strong><small>{log.detail}</small><span>{new Date(log.time).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'})}</span></div></div>)}</div>
      <div className="right-divider"/><div className="side-section-title">PROVIDER CONFIGURATION</div><dl className="provider-list"><dt>Chat</dt><dd>{status?.chatModel??'Offline'}</dd><dt>Embeddings</dt><dd>{status?.embeddings??'—'}</dd><dt>Web search</dt><dd>{status?.webSearch??'—'}</dd><dt>Qdrant</dt><dd className={health?.qdrant?'positive':'negative'}>{health?.qdrant?'Connected':'Offline'}</dd></dl>
      <div className="right-divider"/><div className="side-section-title">EXPLAINABLE BY DESIGN</div><div className="trust"><Info size={15}/> One MAF agent, typed C# tools, local project sandbox, human approval and deterministic exports.</div>
    </aside>
  </div>;
}
