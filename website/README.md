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
- `shot-*.png` — the gallery, from the same artifact: take the
  `iphone-6_9-<state>.png` captures (1320×2868) and resize to exactly a third
  (440×956). Refresh them by running the **iOS Screenshots** workflow after a
  UI change; `shot-search.png` came from the `search` state.

The main-page teaser block (`.megapdf-teaser` CSS + section) lives in
`/public_html/index.html` on the server; a pre-edit backup was uploaded as
`/public_html/index.pre-megapdf-backup.html`.

## Launch swap (when a store approves)

`megapdf/index.html` carries the store links already written, commented out
directly under the "in testing now" paragraph. On approval: delete that
paragraph, uncomment the `.storelinks` block (drop the link for whichever store
is still pending), redeploy.

Both listings 404 until they are actually public, and the Play API cannot report
Google's verdict — the store URL returning 200 is the signal, not a track status
of `completed`, which only describes the rollout:

- `https://play.google.com/store/apps/details?id=ca.electricrv.megapdf`
- `https://apps.apple.com/app/id6799522972`
