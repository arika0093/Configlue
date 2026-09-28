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
            'getting-started/04-validation',
            'getting-started/05-encrypted-secrets',
            'getting-started/06-environment',
            'getting-started/07-http-source',
            'getting-started/08-json-schema',
            'getting-started/09-migration',
            'getting-started/10-native-aot',
            'getting-started/11-yaml',
          ],
        },
        {
          label: 'Functional guides',
          translations: { ja: '機能説明' },
          items: [
            {
              label: 'Setup and read-write',
              translations: { ja: '構成と読み書き' },
              items: [
                'basic-usage/app-setup',
                'basic-usage/reading-and-writing',
                'basic-usage/common-sources',
              ],
            },
            {
              label: 'Locations and formats',
              translations: { ja: '保存場所と形式' },
              items: [
                'sources/files-and-sections',
                'sources/environment-and-commandline',
                'sources/http-and-zip',
                'sources/fallback-and-custom',
              ],
            },
            {
              label: 'Layering',
              translations: { ja: '重ね合わせ' },
              items: [
                'layering/resolution-and-merge',
                'layering/write-routing',
                'layering/mount-and-project',
              ],
            },
            {
              label: 'Named instances and validation',
              translations: { ja: '名前付きと検証' },
              items: [
                'profiles/profiles',
                'profiles/dynamic-options',
                'basic-usage/changes-and-validation',
              ],
            },
            {
              label: 'Schema and migration',
              translations: { ja: 'スキーマと移行' },
              items: [
                'advanced/json-schema-and-testing',
                'migration/schema-migration',
                'migration/storage-migration',
                'migration/adopting-configuration-writable',
              ],
            },
            {
              label: 'Operations and diagnostics',
              translations: { ja: '運用と診断' },
              items: [
                'advanced/native-aot',
                'advanced/backups-and-observability',
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
