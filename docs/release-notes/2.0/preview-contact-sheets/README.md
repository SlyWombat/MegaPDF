# Preview-video contact sheets (#146 §3)

One still per second of each of the nine 2.0 app preview clips, thirty to a
sheet, so the clips can be read on a page instead of scrubbed. Cut by
`tools/preview-gate.py` and measured by `tools/capture-gate/gate.py --store
video`; the read is written up in [`../preview-videos.md`](../preview-videos.md).

The clips themselves are not in git — they are 0.4–4 MB each and a capture set
lives on the machine that shot it. These are thumbnails, 170 px wide for the
two phones' tall frames and 300 px for the Mac's wide one: enough to read the
story and to notice a screen that should not be there, not enough to judge a
glyph. The full-size stills are on kdocker2 under
`~/megapdf-video-work/{preview-out,mac-out}/frames/<lang>/`.

| | en | fr-CA | fr-FR |
|---|---|---|---|
| iPhone 6.9" | [`en-iphone-6_9.jpg`](en-iphone-6_9.jpg) | [`fr-CA-iphone-6_9.jpg`](fr-CA-iphone-6_9.jpg) | [`fr-FR-iphone-6_9.jpg`](fr-FR-iphone-6_9.jpg) |
| iPad 13" | [`en-ipad-13.jpg`](en-ipad-13.jpg) | [`fr-CA-ipad-13.jpg`](fr-CA-ipad-13.jpg) | [`fr-FR-ipad-13.jpg`](fr-FR-ipad-13.jpg) |
| Mac | [`en-mac.jpg`](en-mac.jpg) | [`fr-CA-mac.jpg`](fr-CA-mac.jpg) | [`fr-FR-mac.jpg`](fr-FR-mac.jpg) |

Each tile is labelled `t000`…`t029`: the second of the clip that still was
taken at, counting from the clip's own first frame.
