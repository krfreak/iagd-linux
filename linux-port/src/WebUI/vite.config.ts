import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';

// Built output lands in the host's wwwroot, which iagd-host serves as static files.
// During development, `npm run dev` proxies the API to the running host instead.
export default defineConfig({
  plugins: [preact()],
  build: {
    outDir: '../IAGrim.Host/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:5680',
      '/ws': { target: 'ws://127.0.0.1:5680', ws: true },
    },
  },
});
