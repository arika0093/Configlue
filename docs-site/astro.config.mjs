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
      sidebar: [
        {
          label: 'Getting started',
          translations: { ja: 'はじめる' },
          items: [
            'getting-started/why-configlue',
            'getting-started/installation',
            'getting-started/quick-start',
            'getting-started/examples',
          ],
        },
        {
          label: 'Tutorial',
          translations: { ja: 'チュートリアル' },
          items: [
            'getting-started/01-first-file-app',
            'getting-started/02-real-world-model',
            'getting-started/03-global-local',
            'getting-started/06-environment',
            'getting-started/04-validation',
          ],
        },
        {
          label: 'Guides',
          translations: { ja: '目的別ガイド' },
          items: [
            {
              label: 'Application setup and editing',
              translations: { ja: 'アプリ構成と編集' },
              items: [
                'basic-usage/app-setup',
                'basic-usage/reading-and-writing',
                'basic-usage/common-sources',
                'basic-usage/changes-and-validation',
              ],
            },
            {
              label: 'Formats and sources',
              translations: { ja: '形式とソース' },
              items: [
                'sources/files-and-sections',
                'getting-started/11-yaml',
                'sources/environment-and-commandline',
                'getting-started/07-http-source',
                'sources/http-and-zip',
                'sources/fallback-and-custom',
              ],
            },
            {
              label: 'Layering and routing',
              translations: { ja: '重ね合わせと書き込み先' },
              items: [
                'layering/resolution-and-merge',
                'layering/write-routing',
                'layering/mount-and-project',
              ],
            },
            {
              label: 'Profiles and named instances',
              translations: { ja: 'プロファイルと名前付き設定' },
              items: [
                'profiles/profiles',
                'profiles/dynamic-options',
              ],
            },
            {
              label: 'Schema and migration',
              translations: { ja: 'スキーマと移行' },
              items: [
                'getting-started/08-json-schema',
                'advanced/json-schema-and-testing',
                'getting-started/09-migration',
                'migration/schema-migration',
                'migration/storage-migration',
                'migration/adopting-configuration-writable',
              ],
            },
            {
              label: 'Operations and deployment',
              translations: { ja: '運用と配布' },
              items: [
                'getting-started/05-encrypted-secrets',
                'advanced/backups-and-observability',
                'getting-started/10-native-aot',
                'advanced/native-aot',
              ],
            },
          ],
        },
        {
          label: 'Design',
          translations: { ja: '設計' },
          items: ['design/overview', 'design/options'],
        },
        {
          label: 'Reference',
          translations: { ja: 'リファレンス' },
          items: [
            'reference/packages',
            'reference/http-resource-protocol',
            'reference/s3-object-resource',
            'reference/dapr-state-resource',
          ],
        },
      ],
    }),
    mdx(),
  ],
  markdown: {
    remarkPlugins: [rewriteDocLinks],
  },
});
