import { useEffect, useState } from 'react';
import { CheckCircle2, ClipboardCheck, FileOutput, RefreshCw, XCircle, ShieldCheck } from 'lucide-react';
import { api, saveBlob, type Approval } from '../lib/api';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
type Props = { activity: (title: string, detail: string) => void };
export default function Approvals({ activity }: Props) {
  const [items, setItems] = useState<Approval[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const refresh = () => api<Approval[]>('/api/approvals').then(setItems).catch(e => setError((e as Error).message));
  useEffect(() => { void refresh(); }, []);
  const decide = async (id: string, accept: boolean) => {
    setLoading(true); setError('');
    try {
      const url = `/api/approvals/${id}/${accept ? 'approve?format=pdf' : 'reject'}`;
      const response = await fetch(url, { method:'POST' });
      if (!response.ok) { const problem = await response.json().catch(() => ({})) as {detail?: string;error?:string}; throw new Error(problem.error || problem.detail || `Request failed: HTTP ${response.status}`); }
      if (accept) { saveBlob(await response.blob(), 'approved-handoff.pdf'); activity('Approved handoff', 'PDF exported'); }
      else activity('Proposal rejected', id);
      await refresh();
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  };
  const preview = (payload: string) => {
    try {
      const draft = JSON.parse(payload) as { Title?: string; Content?: string; title?: string; content?: string };
      return draft.Content || draft.content || payload;
    } catch { return payload; }
  };
  return <div className="page-scroll"><div className="page-head"><div className="eyebrow">HUMAN-IN-THE-LOOP DEMO</div><h1>Approval center</h1><p>Review the agent's proposed handoff report before exporting a reviewed copy.</p></div>
    <div className="help-banner"><ShieldCheck size={20}/><div><strong>You stay in control</strong><p>This demonstration approves report exports only. It no longer publishes Azure DevOps tickets or changes external applications.</p></div></div>
    <div className="panel"><div className="panel-title"><h2>Pending proposals ({items.length})</h2><button className="ghost" onClick={refresh}><RefreshCw size={15}/> Refresh</button></div>
      {!items.length && <div className="empty"><ClipboardCheck size={38}/><h3>Nothing needs approval</h3><p>Ask DeveloperTwin to “propose this handoff for approval” and the request will appear here.</p></div>}
      {items.map(item => <div className="approval-item" key={item.id}><div className="panel-icon"><FileOutput size={19}/></div><div className="approval-main"><strong>{item.summary}</strong><span>{new Date(item.createdAt).toLocaleString()} · {item.kind}</span><details><summary>Preview proposed report</summary><div className="markdown approval-preview"><Markdown remarkPlugins={[remarkGfm]}>{preview(item.payload)}</Markdown></div></details></div><div className="approval-buttons"><button className="primary" disabled={loading} onClick={() => decide(item.id,true)}><CheckCircle2 size={15}/> Approve PDF</button><button className="secondary" disabled={loading} onClick={() => decide(item.id,false)}><XCircle size={15}/> Reject</button></div></div>)}
    </div>{error && <div className="alert error-alert">{error}</div>}
  </div>;
}
