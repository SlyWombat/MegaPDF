#!/usr/bin/env python3
"""Tests for website/deploy.py: its login-file lookup (#668), its TLS verification (#726),
its refusal to upload a page that loads anything from a third party (#846), and its
checks on the support page in all three languages (#859).

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


class SupportPagesInEveryLanguage(unittest.TestCase):
    """#859: support/ has French and Spanish twins under fr/ and es/. --support has to
    check them as it checks the English one, hold them to the English one's ids, and
    upload them; without --support none of the three may go up."""

    SITE = os.path.join(os.path.dirname(DEPLOY), "megapdf")
    PAGES = ["support/index.html", "fr/support/index.html", "es/support/index.html"]

    def setUp(self):
        self.deploy = load_deploy()

    def uploaded(self, **flags):
        return {os.path.relpath(path, self.SITE).replace(os.sep, "/")
                for path, _ in self.deploy.plan("/d", True, **flags)}

    def test_all_three_go_up_with_support_and_none_without(self):
        with_support = self.uploaded(include_support=True)
        without = self.uploaded(include_support=False)
        for page in self.PAGES:
            with self.subTest(page=page):
                self.assertIn(page, with_support)
                self.assertNotIn(page, without)
        # The landing pages in fr/ and es/ are not gated by it.
        self.assertIn("fr/index.html", without)
        self.assertIn("es/index.html", without)

    def test_the_real_pages_pass(self):
        note = self.deploy.check_support(True)
        self.assertIn("3 languages", note)

    def copy_site(self, tmp):
        """The parts of the site check_support reads, in a throwaway folder."""
        import shutil
        for rel in self.PAGES + ["privacy/index.html"]:
            os.makedirs(os.path.dirname(os.path.join(tmp, rel)), exist_ok=True)
            shutil.copy(os.path.join(self.SITE, rel), os.path.join(tmp, rel))

    def refused_after(self, rel, old, new):
        with tempfile.TemporaryDirectory() as tmp:
            self.copy_site(tmp)
            path = os.path.join(tmp, rel)
            with open(path, encoding="utf-8") as f:
                page = f.read()
            self.assertIn(old, page)
            with open(path, "w", encoding="utf-8") as f:
                f.write(page.replace(old, new, 1))
            with mock.patch.object(self.deploy, "SITE", tmp):
                with self.assertRaises(SystemExit) as caught:
                    self.deploy.check_support(True)
            return str(caught.exception)

    def test_a_missing_language_page_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.copy_site(tmp)
            os.remove(os.path.join(tmp, "es", "support", "index.html"))
            with mock.patch.object(self.deploy, "SITE", tmp):
                with self.assertRaises(SystemExit) as caught:
                    self.deploy.check_support(True)
            self.assertIn("es/support/index.html", str(caught.exception))

    def test_a_renamed_id_is_refused(self):
        why = self.refused_after("fr/support/index.html", 'id="a-print"', 'id="a-impression"')
        self.assertIn("missing a-print", why)

    def test_another_platform_list_is_refused(self):
        why = self.refused_after("es/support/index.html",
                                 'id="a-crash" data-plat="windows mac linux"',
                                 'id="a-crash" data-plat="all"')
        self.assertIn("other data-plat on a-crash", why)

    def test_a_translated_interface_line_is_refused(self):
        why = self.refused_after("fr/support/index.html", '"Answer ids: "', '"Réponses\u00a0: "')
        self.assertIn("Answer ids", why)

    def test_a_translated_subject_tag_is_refused(self):
        why = self.refused_after("es/support/index.html", 'bug: "Something is broken"',
                                 'bug: "Algo no funciona"')
        self.assertIn("Something is broken", why)

    def test_a_term_with_punctuation_is_refused(self):
        why = self.refused_after("fr/support/index.html", 'data-strong="planté|',
                                 'data-strong="l\'appli a planté|')
        self.assertIn("unmatchable term", why)

    def test_a_term_in_both_lists_accents_aside_is_refused(self):
        why = self.refused_after("es/support/index.html", 'data-match="imprimir|',
                                 'data-match="imprimir|impresion|')
        self.assertIn("impresion", why)

    def test_a_page_without_the_accent_folding_matcher_is_refused(self):
        why = self.refused_after("es/support/index.html", '.normalize("NFD")', "")
        self.assertIn("folds accents", why)

    def test_a_missing_hreflang_alternate_is_refused(self):
        why = self.refused_after("support/index.html",
                                 '<link rel="alternate" hreflang="es" href="https://megapdf.ca/es/support/">',
                                 "")
        self.assertIn('hreflang="es"', why)

    def test_accented_terms_fold_as_the_browser_folds_them(self):
        fold = self.deploy.fold_term
        self.assertEqual(fold("contraseña"), "contrasena")
        self.assertEqual(fold("où est passé"), "ou est passe")
        self.assertEqual(fold("cœur"), "cœur")  # œ has no decomposition, as in the browser


class TheTokenOnlyGoesToAVerifiedServer(unittest.TestCase):
    """#726: the connection that carries the cPanel token checks the server's certificate."""

    def test_the_context_verifies(self):
        import ssl
        deploy = load_deploy()
        self.assertEqual(deploy.CTX.verify_mode, ssl.CERT_REQUIRED)
        self.assertTrue(deploy.CTX.check_hostname)

    def test_the_dry_run_says_so(self):
        with tempfile.TemporaryDirectory() as env_dir, tempfile.TemporaryDirectory() as cwd:
            result = run_dry_run(env_dir, cwd)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(load_deploy().TLS_NOTE, result.stdout)

    def test_a_self_signed_server_is_refused_before_the_token_is_sent(self):
        """A real TLS server on localhost with a certificate nobody trusts: the request
        must fail the handshake, and the server must never see the Authorization header."""
        import http.server
        import shutil
        import ssl
        import threading
        if not shutil.which("openssl"):
            self.skipTest("openssl is not installed")
        deploy = load_deploy()
        seen = []

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                seen.append(self.headers.get("Authorization"))
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b"{}")

            def log_message(self, *args):
                pass

        with tempfile.TemporaryDirectory() as d:
            # A throwaway self-signed pair made for this test and deleted with the folder.
            pair_key, pair_cert = os.path.join(d, "throwaway-k.tmp"), os.path.join(d, "throwaway-c.tmp")
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                            "-keyout", pair_key, "-out", pair_cert, "-days", "1",
                            "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost"],
                           check=True, capture_output=True)
            httpd = http.server.HTTPServer(("127.0.0.1", 0), Handler)
            server_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_ctx.load_cert_chain(pair_cert, pair_key)
            httpd.socket = server_ctx.wrap_socket(httpd.socket, server_side=True)
            threading.Thread(target=httpd.serve_forever, daemon=True).start()
            try:
                server = deploy.Server.__new__(deploy.Server)  # skips the login file
                server.base_url = f"https://localhost:{httpd.server_address[1]}/execute"
                server.auth = "cpanel user:sentinel-726-not-a-real-token"
                with self.assertRaises(Exception) as caught:
                    server._post("Fileman/upload_files", b"x", "text/plain")
                reason = getattr(caught.exception, "reason", caught.exception)
                self.assertIsInstance(reason, ssl.SSLCertVerificationError, repr(caught.exception))
                self.assertEqual(seen, [])
            finally:
                httpd.shutdown()
                httpd.server_close()


if __name__ == "__main__":
    unittest.main()
