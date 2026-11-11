#!/usr/bin/env python3
"""Tests for website/deploy.py: its login-file lookup (#668) and its refusal to
upload a page that loads anything from a third party (#846).

Run as a script: python3 website/test_deploy.py -v

deploy.py must find its login file from a worktree as well as the shared checkout,
and --dry-run must say whether it is there without ever opening it. Everything here
uses throwaway folders and a throwaway git repository; nothing touches the real
checkout or the real login file, and the only "secret" is a fake sentinel string.
"""

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

DEPLOY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "deploy.py")
VARIABLE = "MEGAPDF_DEPLOY_ENV_DIR"


def load_deploy():
    spec = importlib.util.spec_from_file_location("megapdf_deploy", DEPLOY)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_dry_run(env_dir, cwd):
    return subprocess.run(
        [sys.executable, DEPLOY, "--dry-run"], cwd=cwd,
        env={**os.environ, VARIABLE: env_dir},
        capture_output=True, text=True, timeout=120)


class DryRunReportsTheLoginFile(unittest.TestCase):
    def test_found_from_a_temp_dir(self):
        with tempfile.TemporaryDirectory() as env_dir, tempfile.TemporaryDirectory() as cwd:
            open(os.path.join(env_dir, ".env"), "w").close()
            result = run_dry_run(env_dir, cwd)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = (f"Login file (from ${VARIABLE}): "
                        f"{os.path.join(env_dir, '.env')} — found")
            self.assertIn(expected, result.stdout)

    def test_missing_is_reported_not_refused(self):
        with tempfile.TemporaryDirectory() as env_dir, tempfile.TemporaryDirectory() as cwd:
            result = run_dry_run(env_dir, cwd)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("— NOT FOUND", result.stdout)
            self.assertIn(VARIABLE, result.stdout)

    def test_never_reads_or_prints_the_file(self):
        with tempfile.TemporaryDirectory() as env_dir, tempfile.TemporaryDirectory() as cwd:
            with open(os.path.join(env_dir, ".env"), "w") as f:
                f.write("CPANEL_TOKEN=sentinel-668-not-a-secret\n")
            result = run_dry_run(env_dir, cwd)
            self.assertNotIn("sentinel-668", result.stdout)
            self.assertNotIn("sentinel-668", result.stderr)


def git(cwd, *args):
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", *args],
                   cwd=cwd, check=True, capture_output=True)


class LoginFileFollowsTheMainCheckout(unittest.TestCase):
    def test_worktree_and_main_checkout_agree(self):
        with tempfile.TemporaryDirectory() as root:
            projects = os.path.join(root, "Projects")
            main = os.path.join(projects, "MegaPDF")
            login = os.path.join(projects, "Lighting Arduino")
            os.makedirs(main)
            os.makedirs(login)
            git(main, "init", "-q")
            git(main, "commit", "-q", "--allow-empty", "-m", "root")
            worktree = os.path.join(main, ".claude", "worktrees", "w")
            git(main, "worktree", "add", "-q", worktree)
            d = load_deploy()
            want = os.path.realpath(login)
            self.assertEqual(os.path.realpath(d.default_env_dir(main)), want)
            self.assertEqual(os.path.realpath(d.default_env_dir(worktree)), want)

    def test_outside_git_falls_back_beside_the_tree(self):
        with tempfile.TemporaryDirectory() as root:
            plain = os.path.join(root, "a", "MegaPDF")
            os.makedirs(plain)
            d = load_deploy()
            # A ceiling at root keeps an enclosing repository from answering.
            with mock.patch.dict(os.environ, {"GIT_CEILING_DIRECTORIES": root}):
                self.assertEqual(d.default_env_dir(plain),
                                 os.path.join(root, "a", "Lighting Arduino"))

    def test_override_wins(self):
        with tempfile.TemporaryDirectory() as folder:
            d = load_deploy()
            with mock.patch.dict(os.environ, {VARIABLE: folder}):
                self.assertEqual(d.env_path(), (os.path.join(folder, ".env"), True))


class NoPageLoadsFromAThirdParty(unittest.TestCase):
    """#846: the pages loaded Google Fonts while the privacy policy said no third
    party was involved. deploy.py now refuses any page that fetches from a host
    that is not ours, and every page in the tree has to pass, in every state a
    deploy can put it in."""

    def setUp(self):
        self.deploy = load_deploy()

    def test_every_page_passes_with_its_regions_live_and_held_back(self):
        site = os.path.join(os.path.dirname(DEPLOY), "megapdf")
        pages = [os.path.join(root, name) for root, _, files in os.walk(site)
                 for name in files if name.endswith(".html")]
        self.assertGreaterEqual(len(pages), 4)
        for page in pages:
            with open(page, encoding="utf-8") as f:
                source = f.read()
            for live in (True, False):
                html = self.deploy.resolve(source, {"linux": live, "snap": live, "support": live})
                with self.subTest(page=os.path.relpath(page, site), live=live):
                    self.assertEqual(self.deploy.third_party_loads(html), [])

    def test_google_fonts_is_refused(self):
        html = ('<link rel="preconnect" href="https://fonts.googleapis.com">\n'
                '<link href="https://fonts.googleapis.com/css2?family=Inter" rel="stylesheet">')
        self.assertEqual(len(self.deploy.third_party_loads(html)), 2)

    def test_scripts_images_frames_and_css_urls_are_checked(self):
        for html in ('<script src="https://cdn.example.com/x.js"></script>',
                     '<img alt="" src="//images.example.com/a.png">',
                     '<iframe src="https://www.youtube.com/embed/x"></iframe>',
                     '<style>@font-face { src: url("https://fonts.gstatic.com/a.woff2") }</style>',
                     '<style>@import "https://example.com/a.css";</style>'):
            with self.subTest(html=html):
                self.assertEqual(len(self.deploy.third_party_loads(html)), 1)

    def test_our_own_hosts_links_and_page_names_are_not_loads(self):
        html = ('<link rel="canonical" href="https://megapdf.ca/">'
                '<link rel="alternate" hreflang="fr" href="https://megapdf.ca/fr/">'
                '<script defer src="https://electricrv.ca/api/analytics/a.js"></script>'
                '<a href="https://apps.apple.com/app/id6799522972">App Store</a>'
                '<a href="https://github.com/SlyWombat/MegaPDF">source</a>'
                '<style>@font-face { src: url(fonts/space-grotesk-latin.woff2) }</style>'
                '<img alt="" src="shot-viewer.png">')
        self.assertEqual(self.deploy.third_party_loads(html), [])

    def test_a_dry_run_refuses_a_page_with_a_third_party_load(self):
        with tempfile.TemporaryDirectory() as tmp:
            page = os.path.join(tmp, "index.html")
            with open(page, "w", encoding="utf-8") as f:
                f.write('<link href="https://fonts.googleapis.com/css2?x" rel="stylesheet">')
            with self.assertRaises(SystemExit) as caught:
                self.deploy.staged([(page, "/public_html/megapdf")], {})
            self.assertIn("fonts.googleapis.com", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
