#!/usr/bin/env python3
"""Print the processing state of every screenshot and preview on the editable
App Store version (ASC_PLATFORM picks the platform). Exit 0 when everything is
COMPLETE, 1 while anything is still processing, 2 if anything FAILED.
Credentials as tools/asc_publish.py takes them."""
import collections
import sys

import asc_publish as a


def main():
    v = a.editable_version()
    if v is None:
        sys.exit("no editable version")
    counts, failed, pending = collections.Counter(), [], 0
    for loc in a.paged(f"/v1/appStoreVersions/{v['id']}/appStoreVersionLocalizations?limit=50"):
        locale = loc["attributes"]["locale"]
        for kind, sets_rel, items_rel, type_attr in (
                ("shot", "appScreenshotSets", "appScreenshots", "screenshotDisplayType"),
                ("preview", "appPreviewSets", "appPreviews", "previewType")):
            for s in a.paged(f"/v1/appStoreVersionLocalizations/{loc['id']}/{sets_rel}?limit=50"):
                for it in a.paged(f"/v1/{sets_rel}/{s['id']}/{items_rel}?limit=50"):
                    st = (it["attributes"].get("assetDeliveryState") or {})
                    state = st.get("state", "?")
                    counts[(kind, state)] += 1
                    if state == "FAILED":
                        failed.append((locale, s["attributes"][type_attr],
                                       it["attributes"].get("fileName"), st.get("errors")))
                    elif state != "COMPLETE":
                        pending += 1
    try:
        detail = a.api("GET", f"/v1/appStoreVersions/{v['id']}/appStoreReviewDetail", quiet=True)["data"]
    except Exception:
        detail = None
    if detail:
        for it in a.paged(f"/v1/appStoreReviewDetails/{detail['id']}/appStoreReviewAttachments?limit=50"):
            st = (it["attributes"].get("assetDeliveryState") or {})
            state = st.get("state", "?")
            counts[("review", state)] += 1
            if state == "FAILED":
                failed.append(("review", "attachment", it["attributes"].get("fileName"), st.get("errors")))
            elif state != "COMPLETE":
                pending += 1
    for (kind, state), n in sorted(counts.items()):
        print(f"  {kind:8} {state:22} {n}")
    for f in failed:
        print("  FAILED:", *f)
    sys.exit(2 if failed else 1 if pending else 0)


if __name__ == "__main__":
    main()
