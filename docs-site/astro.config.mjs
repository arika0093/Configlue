import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: process.env.CONFIGLUE_DOCS_SITE ?? 'https://arika0093.github.io',
  base: process.env.CONFIGLUE_DOCS_BASE || undefined,
  integrations: [
    starlight({
      title: 'Configlue',
      description: 'Typed configuration assembled from independent state sources.',
      social: [
        {
          icon: 'github',
          label: 'GitHub',
          href: 'https://github.com/arika0093/Configlue',
        },
      ],
      editLink: {
        baseUrl: 'https://github.com/arika0093/Configlue/edit/main/',
      },
      sidebar: [
        {
          label: 'Getting started',
          items: [
            'getting-started/quick-start',
            'getting-started/model',
          ],
        },
        {
          label: 'Core concepts',
          items: [
            'concepts/sources-and-priority',
            'concepts/sparse-fragments',
            'concepts/resources-and-codecs',
          ],
        },
        {
          label: 'Guides',
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
          items: [
            'reference/packages',
            'reference/architecture',
          ],
        },
      ],
    }),
  ],
});
