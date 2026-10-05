import { defineConfig } from 'astro/config';
import mdx from '@astrojs/mdx';
import starlight from '@astrojs/starlight';
import starlightGithubAlerts from 'starlight-github-alerts';
import starlightThemeNova from 'starlight-theme-nova';
import path from 'node:path';
import rewriteDocLinks from './src/remark/rewrite-doc-links.mjs';

const docsSite = process.env.CONFIGLUE_DOCS_SITE || 'https://arika0093.github.io';
const docsBase = process.env.CONFIGLUE_DOCS_BASE || undefined;
const repoRoot = path.resolve(process.cwd(), '..');

export default defineConfig({
  site: docsSite,
  base: docsBase,
  server: {
    host: true,
    fs: { allow: [repoRoot] },
  },
  vite: {
    resolve: { dedupe: ['@astrojs/starlight'] },
  },
  integrations: [
    starlight({
      title: 'Configlue',
      description: 'A typed configuration library that glues multiple setting sources together.',
      expressiveCode: true,
      editLink: {
        // Content entries are rooted in ../docs, so Starlight appends a ../ path.
        baseUrl: 'https://github.com/arika0093/Configlue/edit/main/docs-site/',
      },
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/arika0093/Configlue' },
      ],
      defaultLocale: 'en',
      locales: {
        en: { label: 'English', lang: 'en' },
        ja: { label: '日本語', lang: 'ja' },
      },
      // Top-level order is explicit and stable (#321). Each section owns one
      // directory; future child pages inside a section are picked up via
      // autogenerate without reordering the top level.
      sidebar: [
        {
          label: 'Getting Started',
          translations: { ja: 'はじめる' },
          items: [{ autogenerate: { directory: 'getting-started' } }],
        },
        {
          label: 'Concepts',
          translations: { ja: '概念' },
          items: [{ autogenerate: { directory: 'concepts' } }],
        },
        {
          label: 'Guides',
          translations: { ja: 'ガイド' },
          items: [{ autogenerate: { directory: 'guides' } }],
        },
        {
          label: 'Integrations',
          translations: { ja: 'インテグレーション' },
          items: [{ autogenerate: { directory: 'integrations' } }],
        },
        {
          label: 'Reference',
          translations: { ja: 'リファレンス' },
          items: [{ autogenerate: { directory: 'reference' } }],
        },
        {
          label: 'Troubleshooting',
          translations: { ja: 'トラブルシューティング' },
          items: [{ autogenerate: { directory: 'troubleshooting' } }],
        },
      ],
      components: {
        PageTitle: './src/components/PageTitle.astro',
        ThemeProvider: 'starlight-theme-nova/components/ThemeProvider.astro',
        ThemeSelect: 'starlight-theme-nova/components/ThemeSelect.astro',
      },
      markdown: {
        processedDirs: ['../docs'],
      },
      customCss: [
        '@fontsource/jetbrains-mono/400.css',
        '@fontsource/jetbrains-mono/600.css',
        './src/styles/custom.css',
      ],
      plugins: [starlightThemeNova(), starlightGithubAlerts()],
    }),
    mdx(),
  ],
  markdown: {
    remarkPlugins: [rewriteDocLinks],
  },
});
