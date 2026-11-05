# DeveloperTwin v4.2 — React effect cleanup crash

## Reported behavior
The page initially renders on the Vite development server at `127.0.0.1:5173`, then React's error boundary displays `TypeError: destroy is not a function`. The browser warning states `useEffect must not return anything besides a function` and the component stack identifies `Chat`.

## Root cause and remedy
`frontend/src/components/Chat.tsx` previously used a concise arrow function for the automatic scroll effect:

```tsx
useEffect(() => end.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }), [messages, busy]);
```

This implicitly returned the result of `scrollIntoView`. Most browser implementations return `undefined`, but React requires an effect callback to reliably return either `undefined` or a cleanup function; a different return value can cause React to throw during cleanup. In particular, React development Strict Mode deliberately repeats effect setup/cleanup, exposing this type of issue.

Fixed code:

```tsx
useEffect(() => {
  end.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
}, [messages, busy]);
```

The block body has no `return`, so the effect callback returns `undefined` unconditionally.

## Verification on Windows

1. Run backend: `powershell -ExecutionPolicy Bypass -File .\scripts\start-backend.ps1`.
2. Run React separately: `powershell -ExecutionPolicy Bypass -File .\scripts\start-frontend.ps1`.
3. Open **http://127.0.0.1:5173** and use **Ctrl+Shift+R** for a hard refresh.
4. Confirm the chat UI remains visible and the console does not show `destroy is not a function` or the `useEffect must not return...` warning.
5. Send a chat message. Confirm the conversation remains visible as `busy` changes from `false` to `true` and back, and that messages scroll into view.
6. Switch from chat to the Knowledge page and back; no render crash should occur.

If the same error persists, ensure your Vite server is reading the **updated project directory**. Inspect `frontend/src/components/Chat.tsx` and confirm the effect has braces. Stop/restart Vite if needed. Do not remove `React.StrictMode`: it helps expose lifecycle defects.

## Boundaries
This patch only addresses this identified React effect crash. It does **not** prove end-to-end success of OpenRouter, Qdrant, knowledge ingestion, project scanning, or export features. Validate those independently.
