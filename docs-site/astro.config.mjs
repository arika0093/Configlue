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
      description: 'Typed configuration assembled from independent state sources.',
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
      components: {
        Hero: './src/components/HomeHero.astro',
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
      sidebar: [
        {
          label: 'Getting started',
          translations: { ja: 'はじめに' },
          items: ['getting-started/quick-start', 'getting-started/model'],
        },
        {
          label: 'Examples',
          translations: { ja: 'サンプル' },
          items: ['examples'],
        },
        {
          label: 'Core concepts',
          translations: { ja: '基本概念' },
          items: [
            'concepts/sources-and-priority',
            'concepts/sparse-fragments',
            'concepts/resources-and-codecs',
          ],
        },
        {
          label: 'Guides',
          translations: { ja: 'ガイド' },
          items: [
            'guides/dependency-injection',
            'guides/writes-and-routing',
            'guides/providers',
            'guides/profiles',
            'guides/migrations',
            'guides/json-schema',
          ],
        },
        {
          label: 'Reference',
          translations: { ja: 'リファレンス' },
          items: ['reference/packages', 'reference/architecture', 'reference/design-notes'],
        },
      ],
    }),
    mdx(),
  ],
  markdown: {
    remarkPlugins: [rewriteDocLinks],
  },
});
