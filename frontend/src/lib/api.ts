export type ProviderStatus = { provider: string; chatModel: string; fallback: string; visionModel: string; visionProvider?: string; pdfAnalysisProvider?: string; openRouterApiKeyConfigured?: boolean; embeddings: string; webSearch: string };
export type KnowledgeHealth = { qdrant: boolean; ollama: boolean; message: string };
export type KnowledgeDocument = { id: string; source: string; chunks: number; revision: string; indexedAtUtc?: string | null; status: 'active' | 'deprecated' };
export type KnowledgeHit = { source: string; text: string; score: number; id: string; documentId: string; revision: string; chunk: number; vectorRank: number; rerankScore: number | null; rank: number };
export type KnowledgeSearchResult = { candidates: KnowledgeHit[]; hits: KnowledgeHit[]; reranker: string; fallbackUsed: boolean; warning: string | null };
export type Approval = { id: string; kind: string; summary: string; payload: string; createdAt: string };
export type FolderScanResult = { project: string; name: string; tree: string };
export type Message = { role: 'user' | 'assistant'; text: string; attachments?: string[]; error?: boolean };
export type Thread = { title: string; messages: Message[]; created: number };

export async function api<T>(url: string, options?: RequestInit): Promise<T> {
  let response: Response;
  try { response = await fetch(url, options); }
  catch (e) { if ((e as Error).name === 'AbortError') throw e; throw new Error('The backend is unreachable. Check that dotnet run is active.'); }
  if (response.ok && response.status === 204) return undefined as T;
  const data = await response.json().catch(() => ({})) as Record<string, unknown>;
  if (!response.ok) throw new Error(String(data.error || data.detail || data.title || `HTTP ${response.status}`));
  return data as T;
}
export async function downloadReport(title: string, content: string, format: 'md' | 'pdf' | 'pptx' | 'svg', signal?: AbortSignal) {
  const response = await fetch('/api/reports/export', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, content, format }), signal
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.error || error.detail || `Export failed (${response.status})`);
  }
  const blob = await response.blob();
  saveBlob(blob, `DeveloperTwin-${title.replace(/[^a-z0-9-]/gi, '_').slice(0, 60)}.${format}`);
}
/** Browser-only rasterization: the server exports vector SVG, we convert it to PNG. */
export async function downloadDiagramPng(title: string, content: string): Promise<void> {
  const response = await fetch('/api/reports/export', {
    method: 'POST', headers: { 'Content-Type':'application/json' },
    body: JSON.stringify({ title, content, format: 'svg' })
  });
  if (!response.ok) throw new Error(`Diagram export failed: HTTP ${response.status}`);
  const imageBlob = await response.blob();
  const url = URL.createObjectURL(imageBlob);
  try {
    const image = new Image(); image.src = url;
    await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.naturalWidth * 2; canvas.height = image.naturalHeight * 2;
    const ctx = canvas.getContext('2d'); if (!ctx) throw new Error('Canvas unavailable in this browser.');
    ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob>((resolve,reject)=>canvas.toBlob(b=>b ? resolve(b) : reject(new Error('PNG generation failed')), 'image/png'));
    saveBlob(blob, `DeveloperTwin-${title.replace(/[^a-z0-9-]/gi, '_').slice(0, 60)}.png`);
  } finally { URL.revokeObjectURL(url); }
}
export function saveBlob(blob: Blob, name: string) {
  const href = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = href; anchor.download = name; document.body.appendChild(anchor); anchor.click(); anchor.remove();
  setTimeout(() => URL.revokeObjectURL(href), 5000);
}
