# electricrv.ca/megapdf — site source

Deployed to `/public_html/megapdf/` on electricrv.ca via cPanel UAPI
(credentials: the Lighting Arduino project's `.env` — CPANEL_HOST/PORT/USER/TOKEN;
upload client pattern: that project's `server/deploy.py`).

- `megapdf/index.html` — landing page (linked from the main-page teaser)
- `megapdf/privacy/index.html` — privacy policy, the URL both app-store
  listings reference. **Update this file and redeploy BEFORE shipping any
  feature that collects data** (policy §8 promises that ordering).
- `icon.png` (512, from the iOS AppIcon), `screenshot-viewer.png` (from the
  appstore-screenshots CI artifact)

The main-page teaser block (`.megapdf-teaser` CSS + section) lives in
`/public_html/index.html` on the server; a pre-edit backup was uploaded as
`/public_html/index.pre-megapdf-backup.html`.
