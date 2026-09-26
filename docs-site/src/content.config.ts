import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsSchema } from '@astrojs/starlight/schema';
import { z } from 'astro/zod';

export const collections = {
  docs: defineCollection({
    loader: glob({
      base: new URL('../../docs/', import.meta.url),
      pattern: '{en,ja}/**/[^_]*.{md,mdx}',
    }),
    schema: docsSchema({
      extend: z.object({ since: z.string().optional() }),
    }),
  }),
};
