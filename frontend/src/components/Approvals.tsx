import { useEffect, useState } from 'react';
import { ArrowRight, CheckCircle2, ClipboardCheck, FileOutput, RefreshCw, XCircle, ShieldCheck } from 'lucide-react';
import { api, saveBlob, type Approval, type FolderScanResult } from '../lib/api';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
type Props = {
  activity: (title: string, detail: string) => void;
  onProjectConnected: (project: FolderScanResult) => void;
  onQueueChanged: () => void;
};
export default function Approvals({ activity, onProjectConnected, onQueueChanged }: Props) {
  const [items, setItems] = useState<Approval[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [connectedProject, setConnectedProject] = useState<FolderScanResult | null>(null);
  const refresh = () => api<Approval[]>('/api/approvals').then(setItems).catch(e => setError((e as Error).message));
  useEffect(() => { void refresh(); }, []);
  const decide = async (id: string, accept: boolean) => {
    setLoading(true); setError('');
    try {
      const item = items.find(candidate => candidate.id === id);
      if (!item) throw new Error('Approval request is no longer available. Refresh and try again.');
      if (item.kind === 'folder-access') {
        if (accept) {
          const project = await api<FolderScanResult>(`/api/approvals/${id}/approve`, { method:'POST' });
          setConnectedProject(project);
          activity('Folder access approved', `${project.name} scanned read-only`);
        } else {
          await api(`/api/approvals/${id}/reject`, { method:'POST' });
          activity('Folder access rejected', item.summary);
        }
      } else {
        const url = `/api/approvals/${id}/${accept ? 'approve?format=pdf' : 'reject'}`;
        const response = await fetch(url, { method:'POST' });
        if (!response.ok) { const problem = await response.json().catch(() => ({})) as {detail?: string;error?:string}; throw new Error(problem.error || problem.detail || `Request failed: HTTP ${response.status}`); }
        if (accept) { saveBlob(await response.blob(), 'approved-handoff.pdf'); activity('Approved handoff', 'PDF exported'); }
        else activity('Proposal rejected', id);
      }
      await refresh();
      onQueueChanged();
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  };
  const preview = (payload: string) => {
    try {
      const draft = JSON.parse(payload) as { Title?: string; Content?: string; title?: string; content?: string };
      return draft.Content || draft.content || payload;
    } catch { return payload; }
  };
  const requestedPath = (payload: string) => {
    try {
      const draft = JSON.parse(payload) as { path?: string; Path?: string };
      return draft.path || draft.Path || 'Path not available';
    }
    catch { return 'Path not available'; }
  };
  return <div className="page-scroll"><div className="page-head"><div className="eyebrow">HUMAN-IN-THE-LOOP DEMO</div><h1>Approval center</h1><p>Review folder-access requests before scanning, and handoff reports before exporting.</p></div>
    <div className="help-banner"><ShieldCheck size={20}/><div><strong>You stay in control</strong><p>Only folders outside <code>Twin:AllowedProjectRoots</code> require review. Approving grants read-only access to that specific folder; the scan never copies, executes, or edits project files.</p></div></div>
    {connectedProject && <div className="help-banner" role="status"><CheckCircle2 size={20}/><div><strong>{connectedProject.name} is connected</strong><p>Read-only scan completed. Project ID: <code>{connectedProject.project}</code>. It is now available to the project tools.</p><button className="secondary" onClick={() => onProjectConnected(connectedProject)}>Open in Projects &amp; folders <ArrowRight size={15}/></button><details><summary>View scanned tree</summary><pre className="approval-tree">{connectedProject.tree}</pre></details></div></div>}
    <div className="panel"><div className="panel-title"><h2>Pending requests ({items.length})</h2><button className="ghost" onClick={refresh}><RefreshCw size={15}/> Refresh</button></div>
      {!items.length && <div className="empty"><ClipboardCheck size={38}/><h3>Nothing needs approval</h3><p>Folder access requests and proposed handoff exports will appear here for review.</p></div>}
      {items.map(item => <div className="approval-item" key={item.id}><div className="panel-icon">{item.kind === 'folder-access' ? <ShieldCheck size={19}/> : <FileOutput size={19}/>}</div><div className="approval-main"><strong>{item.summary}</strong><span>{new Date(item.createdAt).toLocaleString()} · {item.kind}</span>{item.kind === 'folder-access' ? <div className="folder-approval-details"><strong>Folder</strong><code>{requestedPath(item.payload)}</code><p>Approving grants read-only access only to this folder and its descendants. The app rejects symbolic links and reparse points.</p></div> : <details><summary>Preview proposed report</summary><div className="markdown approval-preview"><Markdown remarkPlugins={[remarkGfm]}>{preview(item.payload)}</Markdown></div></details>}</div><div className="approval-buttons"><button className="primary" disabled={loading} onClick={() => decide(item.id,true)}><CheckCircle2 size={15}/>{item.kind === 'folder-access' ? 'Approve & scan' : 'Approve PDF'}</button><button className="secondary" disabled={loading} onClick={() => decide(item.id,false)}><XCircle size={15}/> Reject</button></div></div>)}
    </div>{error && <div className="alert error-alert" role="alert">{error}</div>}
  </div>;
}
