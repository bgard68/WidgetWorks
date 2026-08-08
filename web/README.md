# WidgetWorks web

React + TypeScript SPA (Vite) for the WidgetWorks store.

## Configuration (no secrets committed)

Build-time config comes from `VITE_*` environment variables, injected from the environment:

- **Local dev:** copy `.env.example` to `.env.local` (git-ignored) and fill in values.
- **CI/deploy:** set them as **GitHub Actions Variables** (`VITE_API_BASE_URL`, `VITE_GOOGLE_CLIENT_ID`) — see `.github/workflows/web-ci.yml`.

`VITE_GOOGLE_CLIENT_ID` is the **public** Google OAuth client id (safe in the browser bundle); there is no client secret in this flow. Nothing sensitive is committed.

## Scripts

```bash
npm install
npm run dev      # http://localhost:5173
npm run build    # type-check + production bundle
```

The app expects the WidgetWorks API at `VITE_API_BASE_URL` (default `http://localhost:5080`).
