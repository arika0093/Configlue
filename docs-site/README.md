# Configlue documentation site

This directory contains the Astro and Starlight app for the Configlue documentation website. The English and Japanese pages live under the repository's root `docs/en/` and `docs/ja/` directories.

From this directory, install dependencies and start the local site:

```sh
npm ci
npm run dev
```

Create a production build with `npm run build`, or preview one with `npm run preview`. The GitHub Actions workflow builds pull requests and deploys successful builds from `main` to GitHub Pages. It sets `CONFIGLUE_DOCS_BASE` from the Pages configuration and prefixes root-relative links for the repository path. The site uses Starlight's Nova theme and GitHub-style Markdown alerts.
