import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Download, Image as ImageIcon, Loader2, X } from 'lucide-react';
import { saveBlob } from '../lib/api';

/** Optional image-generation panel. User explicitly submits text to a hosted provider. */
export default function ImageGenerator({ onClose }: { onClose: () => void }) {
  const [prompt, setPrompt] = useState('');
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState('');
  const [image, setImage] = useState<Blob | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const controller = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!image) { setPreview(null); return; }
    const url = URL.createObjectURL(image);
    setPreview(url);
    return () => { URL.revokeObjectURL(url); };
  }, [image]);
  useEffect(() => () => { controller.current?.abort(); }, []);

  const generate = async (event: FormEvent) => {
    event.preventDefault();
    if (generating || !prompt.trim()) return;
    const abort = new AbortController();
    controller.current = abort;
    setGenerating(true); setError(''); setImage(null);
    try {
      const response = await fetch('/api/images/generate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ prompt: prompt.trim() }),
        signal: abort.signal
      });
      if (!response.ok) {
        const detail = await response.json().catch(() => ({})) as { error?: string; detail?: string };
        throw new Error(detail.error || detail.detail || `Image generation failed (${response.status})`);
      }
      const blob = await response.blob();
      if (!blob.type.startsWith('image/')) throw new Error('The server did not return an image.');
      setImage(blob);
    } catch (exception) {
      const caught = exception as Error;
      if (caught.name !== 'AbortError') setError(caught.message);
    } finally { setGenerating(false); controller.current = null; }
  };

  return <section className="image-generator" aria-label="AI image generation" style={{ padding: '16px', border: '1px solid #526078', borderRadius: 12, marginBottom: 12, maxHeight: '45vh', overflowY: 'auto' }}>
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
      <strong><ImageIcon size={16} style={{ verticalAlign: 'middle', marginRight: 8 }} />Generate an AI image</strong>
      <button type="button" onClick={onClose} aria-label="Close image generator"><X size={16}/></button>
    </div>
    <p style={{ fontSize: 12, opacity: 0.75 }}>Type an image description. Your prompt is sent to the configured image provider. Do not include private source code or secrets.</p>
    <form onSubmit={generate}>
      <textarea aria-label="Image prompt" rows={3} maxLength={1800} value={prompt} onChange={event => setPrompt(event.target.value)}
        placeholder="A polished isometric illustration of a modular software architecture, dark indigo background..."
        style={{ width: '100%', padding: 10, borderRadius: 8 }} />
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <button type="submit" disabled={generating || !prompt.trim()}>{generating ? <><Loader2 size={14}/> Generating…</> : 'Generate image'}</button>
        {generating && <button type="button" onClick={() => controller.current?.abort()}>Cancel</button>}
        {image && <button type="button" onClick={() => saveBlob(image, image.type === 'image/jpeg' ? 'developer-twin.jpg' : image.type === 'image/webp' ? 'developer-twin.webp' : 'developer-twin.png')}><Download size={14}/> Save image</button>}
      </div>
    </form>
    {error && <p role="alert" style={{ color: '#fca5a5' }}>{error}</p>}
    {preview && <img src={preview} alt="AI-generated illustration" style={{ maxWidth: '100%', maxHeight: 240, objectFit: 'contain', marginTop: 12, borderRadius: 8 }} />}
  </section>;
}
