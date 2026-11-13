import { readFileSync } from 'node:fs';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig(({ command, mode }) => {
  const certificatePath = process.env.SCOUT_DEV_TLS_CERT;
  const keyPath = process.env.SCOUT_DEV_TLS_KEY;
  const serveHttps = command === 'serve' && mode === 'development';

  if (serveHttps && (!certificatePath || !keyPath)) {
    throw new Error('Vite development requires SCOUT_DEV_TLS_CERT and SCOUT_DEV_TLS_KEY from the trusted ASP.NET Core development certificate.');
  }

  return {
    base: '/react/',
    plugins: [react()],
    server: {
      host: 'localhost',
      port: 5173,
      strictPort: true,
      ...(serveHttps
        ? { https: { cert: readFileSync(certificatePath!), key: readFileSync(keyPath!) } }
        : {}),
      proxy: {
        '/auth': {
          target: 'https://localhost:7276',
          changeOrigin: false,
          secure: true,
        },
        '/bff': {
          target: 'https://localhost:7276',
          changeOrigin: false,
          secure: true,
        },
      },
    },
    build: {
      outDir: '../SaturdayScout.Player/wwwroot/react',
      emptyOutDir: true,
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,
    },
  };
});
