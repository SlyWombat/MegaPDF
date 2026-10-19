# Linux — what's new, 2.3

The Linux channel's what's new is a `<release>` entry in
[`tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`](../../../tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml),
English only, no limit. **Linux does not get the scanned-pages line:** it has no
text recogniser (#676), so Find on a scan behaves there as it always has.

## What's new — the `<release>` entry to add

First in `<releases>`, above the newest entry.

```xml
    <release version="2.3" date="REPLACE-AT-TAG">
      <description>
        <p>Various fixes and improvements.</p>
      </description>
    </release>
```

Nothing here has been applied to the metainfo; that happens at release time, per
`docs/RELEASING.md`.
