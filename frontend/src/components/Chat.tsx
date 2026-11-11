import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ArrowUp, Paperclip, Square, FileText, Download, Sparkles, Globe2, FolderTree, X, BrainCircuit } from 'lucide-react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { Message } from '../lib/api';
import ImageGenerator from './ImageGenerator';

type Props = {
  messages: Message[]; busy: boolean; onSend: (text: string, files: File[], allowExternalAnalysis: boolean) => void;
  onStop: () => void; onExport: (content: string, format: 'md' | 'pdf' | 'pptx' | 'svg' | 'png') => void;
  draft: string; setDraft: (draft: string) => void; onReview: (content: string) => void;
  loadingHistory?: boolean; disabled?: boolean;
};
export default function Chat({ messages, busy, onSend, onStop, onExport, draft, setDraft, onReview, loadingHistory = false, disabled = false }: Props) {
  const [files, setFiles] = useState<File[]>([]);
  const [allowExternalAnalysis, setAllowExternalAnalysis] = useState(false);
  const [showImageGenerator, setShowImageGenerator] = useState(false);
  const end = useRef<HTMLDivElement>(null);
  const picker = useRef<HTMLInputElement>(null);
  // An effect may only return a cleanup function (or nothing). The block body
  // prevents an implicit return from scrollIntoView or a browser polyfill.
  useEffect(() => {
    end.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }, [messages, busy]);
  const submit = (e?: FormEvent) => {
    e?.preventDefault(); if (busy || loadingHistory || disabled || (!draft.trim() && !files.length)) return;
    onSend(draft.trim(), files, allowExternalAnalysis);
    setDraft(''); setFiles([]); setAllowExternalAnalysis(false); if (picker.current) picker.current.value = '';
  };
  const lastAnswer = [...messages].reverse().find(m => m.role === 'assistant' && !m.error)?.text;
  return <div className="chat-page">
    <div className="chat-head"><div className="eyebrow">YOUR ENGINEERING WORKSPACE</div>
      <h1>Developer<span>Twin.</span></h1><p>Explore your codebase, understand your documents and turn findings into shareable handoffs.</p></div>
    <div className="chat-messages">
      {loadingHistory && <p role="status">Loading saved conversation…</p>}
      {!messages.length && <div className="welcome">
        <div className="welcome-symbol"><Sparkles size={24}/></div>
        <h2>What can I help you build?</h2>
        <p>Ask about your files and projects, or generate a technical handoff for someone joining the team.</p>
        <div className="prompts">
          <button onClick={() => setDraft('Inspect my imported project and explain its architecture, frameworks, major files and dependencies.')}><FolderTree size={16}/> Explain a project <span>→</span></button>
          <button onClick={() => setDraft('Search my knowledge base and extract business requirements, business rules, exceptions and missing information.')}><BrainCircuit size={16}/> Extract requirements <span>→</span></button>
          <button onClick={() => setDraft('Search official Microsoft Agent Framework documentation and explain hosted agents and tool calling with examples.')}><Globe2 size={16}/> Research MAF <span>→</span></button>
        </div>
      </div>}
      {messages.map((m, i) => <div className={`chat-line ${m.role}`} key={i}>
        <div className="avatar">{m.role === 'assistant' ? <Sparkles size={15}/> : 'YOU'}</div>
        <div className={`message-body ${m.error ? 'error' : ''}`}>
          <div className="message-author">{m.role === 'assistant' ? 'DeveloperTwin' : 'You'}</div>
          {m.attachments?.length ? <div className="attachment-tags">{m.attachments.map((x, n) => <span key={n}><Paperclip size={12}/>{x}</span>)}</div> : null}
          {m.role === 'assistant' && !m.error
            ? <div className="markdown"><Markdown remarkPlugins={[remarkGfm]} components={{
                img: ({ alt }) => <span>{alt ? `Image reference: ${alt}` : 'Image reference'}</span>,
                a: ({ href, children }) => <a href={href} target="_blank" rel="noopener noreferrer" referrerPolicy="no-referrer">{children}</a>
              }}>{m.text}</Markdown></div>
            : <div className="message-plain">{m.text}</div>}
          {m.role === 'assistant' && !m.error && <div className="inline-actions"><button onClick={() => navigator.clipboard.writeText(m.text)}>Copy response</button></div>}
        </div>
      </div>)}
      {busy && <div className="chat-line assistant"><div className="avatar"><Sparkles size={15}/></div><div className="message-body"><div className="message-author">DeveloperTwin</div><div className="thinking"><i/><i/><i/> Thinking · Tools may be running</div></div></div>}
      <div ref={end}/>
    </div>
    <div className="composer-area">
      {showImageGenerator && <ImageGenerator onClose={() => setShowImageGenerator(false)}/>}
      {lastAnswer && <div className="exports"><button type="button" onClick={() => onReview(lastAnswer)}>Request review</button><span><Download size={13}/> Export last answer:</span>{(['md','pdf','pptx','png','svg'] as const).map(f => <button type="button" key={f} onClick={() => onExport(lastAnswer, f)}>{f.toUpperCase()}</button>)}</div>}
      <form className="composer" onSubmit={submit} onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); setFiles(current => [...current,...Array.from(e.dataTransfer.files)].slice(0,6)); }}>
        {!!files.length && <div className="attachment-tags">{files.map((file,i) => <span key={i}><FileText size={12}/>{file.name}<button type="button" aria-label="Remove attachment" onClick={() => setFiles(files.filter((_,n) => n!==i))}><X size={12}/></button></span>)}</div>}
        {!!files.length && <label className="external-analysis-option"><input type="checkbox" checked={allowExternalAnalysis} onChange={e => setAllowExternalAnalysis(e.target.checked)}/> Allow external analysis of scanned PDFs and images in this message</label>}
        <textarea value={draft} rows={2} placeholder="Ask about your code, business documents, errors or project..." onChange={e => setDraft(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); } }} />
        <div className="composer-bottom">
          <div className="composer-tools"><input ref={picker} type="file" multiple accept=".pdf,.docx,.txt,.md,.cs,.js,.jsx,.ts,.tsx,.json,.xml,.sql,.html,.css,.csv,.png,.jpg,.jpeg,.webp" hidden onChange={e => setFiles(current => [...current, ...Array.from(e.target.files ?? [])].slice(0,6))}/>
            <button type="button" className="tool-button" onClick={() => picker.current?.click()}><Paperclip size={16}/> Attach</button>
            <button type="button" className="tool-button" onClick={() => setShowImageGenerator(value => !value)}><Sparkles size={16}/> Generate image</button>
          </div>
          <div className="composer-buttons">{busy && <button className="stop-button" type="button" onClick={onStop}><Square size={13}/> Stop</button>}
          <button disabled={busy || loadingHistory || disabled || (!draft.trim() && !files.length)} type="submit" className="send-button"><ArrowUp size={17}/></button></div>
        </div>
      </form>
      <div className="composer-disclaimer">Local demo · Review AI output before sharing · Files may be sent to OpenRouter when enabled</div>
    </div>
  </div>;
}
