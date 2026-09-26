# Configlue documentation site

This directory contains the Astro and Starlight source for the Configlue documentation website.

From this directory, install dependencies and start the local site:

```sh
npm ci
npm run dev
```

Create a production build with `npm run build`, or preview one with `npm run preview`. The GitHub Actions workflow builds pull requests and deploys successful builds from `main` to GitHub Pages. It sets `CONFIGLUE_DOCS_BASE` from the Pages configuration so project-site links work under the repository path.
