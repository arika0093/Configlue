import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const siteDir = fileURLToPath(new URL('..', import.meta.url));
const astro = spawn('astro', ['dev', ...process.argv.slice(2)], {
  cwd: siteDir,
  stdio: 'inherit',
  shell: process.platform === 'win32',
});

const stop = (signal) => {
  if (!astro.killed) astro.kill(signal);
};

process.on('SIGINT', () => stop('SIGINT'));
process.on('SIGTERM', () => stop('SIGTERM'));
astro.on('exit', (code, signal) => {
  stop(signal ?? 'SIGTERM');
  process.exit(code ?? 1);
});
