# Preview-video contact sheets (#146 §3)

One still per second of each of the nine 2.0 app preview clips, thirty to a
sheet, so the clips can be read on a page instead of scrubbed. Cut by
`tools/preview-gate.py` and measured by `tools/capture-gate/gate.py --store
video`; the read is written up in [`../preview-videos.md`](../preview-videos.md).

The clips themselves are not in git — they are 0.4–4 MB each and a capture set
lives on the machine that shot it. These are thumbnails at about a sixth of
full width, for reading the story and spotting anything stale; the full-size
stills are on kdocker2 under `~/megapdf-video-work/{preview-out,mac-out}/frames/`.

| | en | fr-CA | fr-FR |
|---|---|---|---|
| iPhone 6.9" | [`en-iphone-6_9.jpg`](en-iphone-6_9.jpg) | [`fr-CA-iphone-6_9.jpg`](fr-CA-iphone-6_9.jpg) | [`fr-FR-iphone-6_9.jpg`](fr-FR-iphone-6_9.jpg) |
| iPad 13" | [`en-ipad-13.jpg`](en-ipad-13.jpg) | [`fr-CA-ipad-13.jpg`](fr-CA-ipad-13.jpg) | [`fr-FR-ipad-13.jpg`](fr-FR-ipad-13.jpg) |
| Mac | [`en-mac.jpg`](en-mac.jpg) | [`fr-CA-mac.jpg`](fr-CA-mac.jpg) | [`fr-FR-mac.jpg`](fr-FR-mac.jpg) |

Each file is named `t000`…`t029` by the second of the clip it was taken at.
