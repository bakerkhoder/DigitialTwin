import { useEffect, useState, type FormEvent } from 'react';
import { FolderGit2, FolderOpen, UploadCloud, FileArchive, ShieldCheck, AlertCircle, ArrowRight, Search } from 'lucide-react';
import { api, type FolderScanResult } from '../lib/api';
type Props = {
  onAnalyzeProject: (projectPath: string) => void;
  activity: (title: string, detail: string) => void;
  onApprovalRequested: () => void;
  openApprovals: () => void;
  initialProject: FolderScanResult | null;
};
export default function Projects({ onAnalyzeProject, activity, onApprovalRequested, openApprovals, initialProject }: Props) {
  const [zip, setZip] = useState<File | null>(null);
  const [folder, setFolder] = useState('');
  const [path, setPath] = useState('');
  const [tree, setTree] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [approvalNotice, setApprovalNotice] = useState('');
  useEffect(() => {
    if (!initialProject) return;
    setPath(initialProject.project);
    setTree(initialProject.tree);
  }, [initialProject]);
  const importZip = async (e: FormEvent) => {
    e.preventDefault(); if (!zip) return;
    setBusy(true); setError(''); const form = new FormData(); form.append('file', zip);
    try {
      const result = await api<{ project: string; tree: string; files: number }>('/api/projects/import', { method:'POST', body:form });
      setPath(result.project); setTree(result.tree); activity('Project imported', `${result.files} source files · ${result.project}`);
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  const connect = async (e: FormEvent) => {
    e.preventDefault(); if (!folder.trim()) return; setBusy(true); setError(''); setApprovalNotice('');
    try {
      const request = await api<
        | { project: string; tree: string; name: string; status: 'connected' }
        | { approvalId: string; status: 'pending'; message: string }
      >('/api/projects/connect', {
        method:'POST', headers: {'Content-Type':'application/json'}, body:JSON.stringify({ path:folder })
      });
      if (request.status === 'connected') {
        setPath(request.project);
        setTree(request.tree);
        activity('Read-only folder connected', request.name);
      } else {
        setApprovalNotice(request.message);
        activity('Folder approval requested', 'Waiting for Approval Center review');
        onApprovalRequested();
      }
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  const inspect = async () => {
    setBusy(true); setError('');
    try { const result = await api<{tree:string}>(`/api/workspace/tree?path=${encodeURIComponent(path || '.')}`); setTree(result.tree); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  };
  return <div className="page-scroll"><div className="page-head"><div className="eyebrow">CODEBASE UNDERSTANDING</div><h1>Projects & folders</h1><p>Inspect another application's source without compiling it or making changes.</p></div>
    <div className="two-panels"><form className="panel" onSubmit={importZip}><div className="panel-icon"><FileArchive size={20}/></div><h2>Import a ZIP project</h2><p className="small-muted">A snapshot of source files is safely extracted to data/workspace. Maximum 50 MB compressed.</p><label className="drop-zone compact"><UploadCloud size={24}/><strong>{zip ? zip.name : 'Choose a ZIP archive'}</strong><span>Source only · no execution</span><input type="file" accept=".zip" onChange={e => setZip(e.target.files?.[0] ?? null)}/></label><button className="primary" disabled={busy || !zip}>{busy ? 'Processing...' : 'Import project'}</button></form>
    <form className="panel" onSubmit={connect}><div className="panel-icon"><FolderOpen size={20}/></div><h2>Connect local folder</h2><p className="small-muted">Folders under configured allowed roots connect directly. Folders outside those roots require Approval Center review before access.</p><label className="field-label">Absolute Windows folder path</label><input className="field" value={folder} onChange={e => setFolder(e.target.value)} placeholder="E:\\Engineering\\MyProject"/><button className="secondary" disabled={busy || !folder.trim()}>{busy ? 'Submitting...' : 'Connect read-only'} <ArrowRight size={15}/></button></form></div>
    <div className="help-banner"><ShieldCheck size={18}/><div><strong>Read-only by design</strong><p>Folders under <code>Twin:AllowedProjectRoots</code> keep their direct-connect behavior. Outside folders appear in the Approval Center; approval grants access only to that requested folder.</p></div></div>
    {approvalNotice && <div className="help-banner approval-notice" role="status"><ShieldCheck size={18}/><div><strong>Approval required</strong><p>{approvalNotice}</p><button className="secondary" type="button" onClick={openApprovals}>Open Approval Center <ArrowRight size={15}/></button></div></div>}
    <section className="panel"><div className="panel-title"><h2>Project explorer</h2><span>Safe text-only inspection</span></div><div className="search-row"><FolderGit2 size={17}/><input value={path} onChange={e => setPath(e.target.value)} placeholder="projects/project-... or linked-..."/><button className="secondary" onClick={inspect} type="button" disabled={busy}><Search size={16}/> Scan</button></div>
      <div className="code-tree">{tree || 'Import a project or connect a folder to view its structure here.'}</div><button className="primary" disabled={!path} onClick={() => onAnalyzeProject(path)}><ArrowRight size={16}/> Ask agent to analyze this project</button>
    </section>{error && <div className="alert error-alert" role="alert"><AlertCircle size={18}/>{error}</div>}
  </div>;
}
