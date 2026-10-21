#!/usr/bin/env python3
"""Print the editable App Store version's release type, copyright and attached
build (ASC_PLATFORM picks the platform). Credentials as tools/asc_publish.py."""
import asc_publish as a

v = a.editable_version()
if v is None:
    raise SystemExit("no editable version")
attrs = v["attributes"]
print(f"{a.PLATFORM} {attrs['versionString']} {attrs.get('appStoreState')} "
      f"releaseType={attrs.get('releaseType')} copyright={attrs.get('copyright')!r}")
b = a.api("GET", f"/v1/appStoreVersions/{v['id']}/build")["data"]
if b:
    ba = b["attributes"]
    print(f"  build {ba['version']} {ba['processingState']} uploaded {ba['uploadedDate'][:16]} "
          f"nonExemptEncryption={ba.get('usesNonExemptEncryption')}")
for loc in a.paged(f"/v1/appStoreVersions/{v['id']}/appStoreVersionLocalizations?limit=50"):
    la = loc["attributes"]
    mark = "" if la.get("supportUrl") == a.SUPPORT_URL else "  <-- NOT the support page"
    print(f"  {la['locale']}: whatsNew {len(la.get('whatsNew') or '')}, description {len(la.get('description') or '')}, "
          f"support {la.get('supportUrl')}, marketing {la.get('marketingUrl')}{mark}")
# Privacy is on the app info, not the version; same appInfos choice as asc_publish.cmd_copy.
infos = a.api("GET", f"/v1/apps/{a.APP_ID}/appInfos")["data"]
info = next((i for i in infos if i["attributes"].get("appStoreState") not in ("READY_FOR_SALE",)), infos[0])
for l in a.paged(f"/v1/appInfos/{info['id']}/appInfoLocalizations?limit=50"):
    print(f"  {l['attributes']['locale']}: privacy {l['attributes'].get('privacyPolicyUrl')}")
