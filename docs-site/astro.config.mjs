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
          label: 'Basic usage',
          translations: { ja: '基本的な使い方' },
          items: [
            'basic-usage/reading-and-writing',
            'basic-usage/app-setup',
            'basic-usage/common-sources',
            'basic-usage/changes-and-validation',
          ],
        },
        {
          label: 'Sources',
          translations: { ja: 'ソース' },
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
          label: 'Profiles',
          translations: { ja: 'プロファイル' },
          items: ['profiles/profiles', 'profiles/dynamic-options'],
        },
        {
          label: 'Migration',
          translations: { ja: '移行' },
          items: [
            'migration/schema-migration',
            'migration/storage-migration',
            'migration/adopting-configuration-writable',
          ],
        },
        {
          label: 'Advanced',
          translations: { ja: '応用' },
          items: [
            'advanced/native-aot',
            'advanced/json-schema-and-testing',
            'advanced/backups-and-observability',
          ],
        },
        {
          label: 'Reference',
          translations: { ja: 'リファレンス' },
          items: [
            'reference/packages',
            'reference/http-resource-protocol',
            'reference/design-notes',
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
