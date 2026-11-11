import { useCallback, useEffect, useRef, useState } from 'react';
import { Bot, BrainCircuit, FolderGit2, ShieldCheck, Plus, Activity, Sparkles, RefreshCw, PanelRightClose, PanelRightOpen, Circle, ChevronRight, Info, Trash2 } from 'lucide-react';
import Chat from './components/Chat';
import Knowledge from './components/Knowledge';
import Projects from './components/Projects';
import Approvals from './components/Approvals';
import Memory from './components/Memory';
import { useConversationHistory } from './lib/useConversationHistory';
import { api, downloadReport, downloadDiagramPng, type ProviderStatus, type KnowledgeHealth, type Message, type Approval, type FolderScanResult } from './lib/api';

type View = 'chat' | 'knowledge' | 'projects' | 'approvals' | 'memory';
type LogItem = { name: string; detail: string; time: number };
const ACTIVE_STORE = 'developertwin-v4-active';
const newId = () => crypto.randomUUID();

function loadActiveId(): string {
  try {
    const saved = localStorage.getItem(ACTIVE_STORE);
    return saved && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(saved) ? saved.toLowerCase() : newId();
  }
  catch { return newId(); }
}

export default function App() {
  const [view, setView] = useState<View>('chat');
  const [active, setActive] = useState<string>(loadActiveId);
  const { threads, setThreads, ready: historyReady, loading: historyLoading, error: historyError,
    setError: setHistoryError, retry: retryHistory, markLoaded } = useConversationHistory(active);
  const [pendingIds, setPendingIds] = useState<string[]>([]);
  const [historyDeleting, setHistoryDeleting] = useState(false);
  const busy = pendingIds.includes(active);
  const [draft, setDraft] = useState('');
  const [status, setStatus] = useState<ProviderStatus | null>(null);
  const [health, setHealth] = useState<KnowledgeHealth | null>(null);
  const [approvalCount, setApprovalCount] = useState(0);
  const [approvals, setApprovals] = useState<Approval[]>([]);
  const [approvalError, setApprovalError] = useState('');
  const [approvedProject, setApprovedProject] = useState<FolderScanResult | null>(null);
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
  const activity = (name: string, detail: string) => setLogs(items => [{ name, detail, time:Date.now() }, ...items].slice(0,16));
  const refreshStatus = async () => {
    try { setStatus(await api<ProviderStatus>('/api/status')); } catch { setStatus(null); }
    try { setHealth(await api<KnowledgeHealth>('/api/knowledge/health')); } catch { setHealth(null); }
  };
  const refreshApprovals = useCallback(async () => {
    try {
      const current = await api<Approval[]>('/api/approvals');
      setApprovals(current);
      setApprovalCount(current.length);
      setApprovalError('');
    } catch (error) {
      setApprovalError((error as Error).message);
    }
  }, []);
  useEffect(() => { void refreshStatus(); }, []);
  useEffect(() => {
    if (view === 'approvals') void refreshApprovals();
  }, [view, refreshApprovals]);
  const updateMessages = (id:string, fn:(messages:Message[])=>Message[]) => setThreads(prev => {
    const prior = prev[id]; const messages = fn(prior?.messages || []);
    return { ...prev, [id]: { title: prior?.title ?? (messages[0]?.text.slice(0,80) || 'Attachment conversation'),
      created: prior?.created ?? Date.now(), updated: Date.now(), messages } };
  });
  const openChat = (id:string) => { setActive(id); setView('chat'); };
  const stopActive = () => controllers.current.get(activeRef.current)?.abort();
  const deleteThread = async (id: string) => {
    if (historyDeleting || !window.confirm('Delete this saved conversation and its agent context? This cannot be undone.')) return;
    setHistoryDeleting(true);
    deletedIds.current.add(id);
    controllers.current.get(id)?.abort();
    setPendingIds(old => old.filter(x => x !== id));
    try {
      await api<void>(`/api/agent/sessions/${id}`, { method: 'DELETE' });
      setThreads(old => { const next = { ...old }; delete next[id]; return next; });
      if (activeRef.current === id) openChat(newId());
    } catch (error) { deletedIds.current.delete(id); setHistoryError(`Conversation could not be deleted. ${(error as Error).message}`); }
    finally { setHistoryDeleting(false); }
  };
  const deleteAllThreads = async () => {
    if (historyDeleting || !window.confirm('Delete ALL saved conversations and their agent context? Saved memories are managed separately.')) return;
    setHistoryDeleting(true);
    const ids = Object.keys(threads);
    for (const id of ids) {
      deletedIds.current.add(id);
      controllers.current.get(id)?.abort();
    }
    controllers.current.clear();
    setPendingIds([]);
    try {
      await api<void>('/api/agent/sessions', { method: 'DELETE' });
      setThreads({}); openChat(newId());
    } catch (error) { ids.forEach(id => deletedIds.current.delete(id)); setHistoryError(`History could not be deleted. ${(error as Error).message}`); }
    finally { setHistoryDeleting(false); }
  };
  const send = async (text:string, files:File[], projectPath?:string, allowExternalAnalysis = false) => {
    const id = activeRef.current;
    if (!historyReady || historyLoading || historyDeleting || controllers.current.has(id)) return;
    markLoaded(id);
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
        form.append('allowExternalAnalysis', String(allowExternalAnalysis));
        files.forEach(f => form.append('files',f));
        result=await api('/api/chat/attachments',{method:'POST',body:form,signal:abort.signal});
      } else if (projectPath) {
        result=await api('/api/agent/analyze-project',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sessionId:id,project:projectPath}),signal:abort.signal});
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
      .then(()=>{
        activity('Handoff submitted','Review requested');
        setApprovalCount(count => count + 1);
        if (view === 'approvals') void refreshApprovals();
        else setView('approvals');
      })
      .catch(e=>window.alert(`Review request failed: ${(e as Error).message}`));
  };
  const analyzeProject = (projectPath:string) => { void send(`Analyze the connected project ${projectPath}.`, [], projectPath); };
  const openApprovedProject = (project: FolderScanResult) => { setApprovedProject(project); setView('projects'); };
  const onFolderApprovalRequested = () => setApprovalCount(count => count + 1);
  const threadEntries = Object.entries(threads).sort((a,b) => (b[1].updated ?? b[1].created) - (a[1].updated ?? a[1].created));
  const menu: {id:View;label:string;icon:typeof Bot}[] = [
    {id:'chat',label:'Assistant',icon:Bot},{id:'knowledge',label:'Knowledge library',icon:BrainCircuit},
    {id:'projects',label:'Projects & folders',icon:FolderGit2},{id:'memory',label:'Saved memory',icon:BrainCircuit},{id:'approvals',label:'Approvals',icon:ShieldCheck}
  ];
  return <div className={`app-shell ${rightVisible ? '' : 'hide-right'}`}>
    <aside className="sidebar"><div className="brand"><div className="brand-glyph">◇</div><div><strong>DeveloperTwin</strong><small>ENGINEERING AGENT</small></div></div>
      <button className="new-chat" onClick={() => openChat(newId())}><Plus size={18}/> New conversation <kbd>Ctrl K</kbd></button>
      <div className="menu-label">WORKSPACE</div>
      <nav>{menu.map(item => <button key={item.id} className={`nav-link ${view===item.id?'active':''}`} onClick={()=>setView(item.id)}><item.icon size={18}/><span>{item.label}</span>{item.id==='approvals'&&approvalCount>0&&<b className="nav-badge" aria-label={`${approvalCount} pending approvals`}>{approvalCount}</b>}{view===item.id&&<ChevronRight size={15}/>}</button>)}</nav>
      <div className="history-header"><div className="menu-label history-label">RECENT CONVERSATIONS</div>
        {threadEntries.length>0 && <button className="clear-history" disabled={historyDeleting} onClick={() => void deleteAllThreads()}>Clear all</button>}</div>
      <div className="history-list">{threadEntries.map(([id, thread]) =>
        <div className="history-row" key={id}>
          <button className={`history-link ${active===id&&view==='chat'?'selected':''}`} onClick={()=>openChat(id)} title={thread.title}>
            {pendingIds.includes(id) ? <span className="mini-pulse" title="Agent working"/> : <Circle size={9} fill="currentColor"/>}
            <span className="history-title">{thread.title}</span></button>
          <button className="history-delete" disabled={historyDeleting} title={`Delete ${thread.title}`} aria-label={`Delete conversation ${thread.title}`}
            onClick={()=>void deleteThread(id)}><Trash2 size={14}/></button>
        </div>)}{!threadEntries.length&&<span className="small-muted">No previous conversations yet.</span>}</div>
      <div className="sidebar-bottom"><div className={`status-dot ${status?'ready':''}`}/><div><strong>{status ? `${status.provider} ready`:'Backend unavailable'}</strong><small>{status?.chatModel ?? 'Check dotnet run'}</small></div><button aria-label="Refresh status" onClick={refreshStatus}><RefreshCw size={15}/></button></div>
    </aside>
    <div className="main-frame"><header className="topbar"><div className="crumb">WORKSPACE <span>/</span> <strong>{menu.find(m=>m.id===view)?.label}</strong></div><div className="top-actions"><div className="provider-pill"><span className={status ? 'online':'offline'}/>{status?.provider?.toUpperCase() ?? 'OFFLINE'} · {status?.chatModel ?? 'Disconnected'}</div><button onClick={()=>setRightVisible(!rightVisible)} aria-label="Toggle activity panel" className="ghost icon-only">{rightVisible?<PanelRightClose size={18}/>:<PanelRightOpen size={18}/>}</button></div></header>
      <main className="main-area">{historyError && <div className="storage-warning" role="alert">{historyError}<button type="button" disabled={pendingIds.length > 0} onClick={retryHistory}>Retry loading</button></div>}{view==='chat' ? <Chat messages={threads[active]?.messages || []} busy={busy} loadingHistory={historyLoading} disabled={historyDeleting} onSend={(text, files, external) => { void send(text, files, undefined, external); }} onStop={stopActive} onExport={exportAnswer} onReview={requestReview} draft={draft} setDraft={setDraft}/>
        : view==='knowledge' ? <Knowledge activity={activity}/> : view==='memory' ? <Memory activity={activity}/> : view==='projects' ? <Projects activity={activity} onAnalyzeProject={analyzeProject} onApprovalRequested={onFolderApprovalRequested} openApprovals={() => setView('approvals')} initialProject={approvedProject}/> : <Approvals activity={activity} items={approvals} loadError={approvalError} onRefresh={refreshApprovals} onProjectConnected={openApprovedProject} onQueueChanged={refreshApprovals}/>}</main>
    </div>
    <aside className="activity-panel"><div className="activity-heading"><Activity size={17}/><strong>Agent activity</strong></div><p className="small-muted">Your operations, model and service health.</p>
      <div className="timeline">{logs.map((log,i)=><div className="timeline-item" key={log.time+i}><div className="timeline-icon">{i===0?<Sparkles size={15}/>:<Activity size={15}/>}</div><div><strong>{log.name}</strong><small>{log.detail}</small><span>{new Date(log.time).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'})}</span></div></div>)}</div>
      <div className="right-divider"/><div className="side-section-title">PROVIDER CONFIGURATION</div><dl className="provider-list"><dt>Chat</dt><dd>{status?.chatModel??'Offline'}</dd><dt>Embeddings</dt><dd>{status?.embeddings??'—'}</dd><dt>Web search</dt><dd>{status?.webSearch??'—'}</dd><dt>Qdrant</dt><dd className={health?.qdrant?'positive':'negative'}>{health?.qdrant?'Connected':'Offline'}</dd></dl>
      <div className="right-divider"/><div className="side-section-title">EXPLAINABLE BY DESIGN</div><div className="trust"><Info size={15}/> One MAF agent, typed C# tools, local project sandbox, human approval and deterministic exports.</div>
    </aside>
  </div>;
}
