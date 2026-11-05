import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': {
      target: 'http://127.0.0.1:5000', changeOrigin: true,
      configure(proxy) { proxy.on('proxyReq', req => req.setHeader('Origin','http://127.0.0.1:5000')); }
    } }
  },
  // Frontend is independent in development. Only `npm run build:api`
  // produces the static files ASP.NET Core can host for deployment.
  build: { outDir: 'dist', emptyOutDir: true }
});
