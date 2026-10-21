#!/usr/bin/env python3
"""Tests for website/deploy.py's login-file lookup (#668).

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


if __name__ == "__main__":
    unittest.main()
