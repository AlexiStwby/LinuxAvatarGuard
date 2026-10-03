# SPDX-License-Identifier: MIT
"""CPU-only adversarial checks of the study store; no Unity/graphics/GPU access."""
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

import research


class PrivateStudyStore(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="lag-fingerprint-cpu-")
        self.base = Path(self.temporary.name)
        self.environment = patch.dict(os.environ, {"XDG_DATA_HOME": str(self.base), "LAG_FP_RESEARCH_DATA_HOME": ""})
        self.environment.start()
        self.dataset = {"schema": 1, "purpose": "LAG-stage14-cpu-study-only", "trials": []}

    def tearDown(self):
        self.environment.stop(); self.temporary.cleanup()

    def saved(self):
        run = research.write_private_dataset(self.dataset)
        return research.private_root() / run / "fingerprint-private.json"

    def test_roundtrip_and_modes(self):
        path = self.saved()
        self.assertEqual(research.read_private(path), self.dataset)
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        for folder in (path.parent, path.parent.parent, path.parent.parent.parent):
            self.assertEqual(stat.S_IMODE(folder.stat().st_mode), 0o700)

    def test_initial_modes_under_restrictive_umask(self):
        previous = os.umask(0o377)
        try: path = self.saved()
        finally: os.umask(previous)
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(path.parent.stat().st_mode), 0o700)
        self.assertEqual(research.read_private(path), self.dataset)

    def test_collision_never_overwrites(self):
        with patch.object(research.secrets, "token_hex", return_value="a" * 32):
            path = self.saved(); before = path.read_bytes()
            with self.assertRaises(FileExistsError): research.write_private_dataset({"different": True})
        self.assertEqual(path.read_bytes(), before)

    def test_reject_public_parent_without_chmod(self):
        folder = self.base / "linux-avatar-guard"; folder.mkdir(mode=0o755); folder.chmod(0o755)
        with self.assertRaises(ValueError): self.saved()
        self.assertEqual(stat.S_IMODE(folder.stat().st_mode), 0o755)

    def test_git_root_rejected(self):
        (self.base / ".git").mkdir()
        with self.assertRaises(ValueError): self.saved()

    def test_unity_project_rejected(self):
        (self.base / "ProjectSettings").mkdir()
        with self.assertRaises(ValueError): self.saved()

    def test_unity_assets_segment_rejected(self):
        assets = self.base / "Assets"; assets.mkdir()
        with patch.dict(os.environ, {"XDG_DATA_HOME": str(assets)}):
            with self.assertRaises(ValueError): self.saved()

    def test_explicit_relative_root_rejected(self):
        with patch.dict(os.environ, {"LAG_FP_RESEARCH_DATA_HOME": "relative-data"}):
            with self.assertRaises(ValueError): research.private_root()

    def test_explicit_root_does_not_bypass_git_rule(self):
        explicit = self.base / "explicit"; explicit.mkdir(); (explicit / ".git").mkdir()
        with patch.dict(os.environ, {"LAG_FP_RESEARCH_DATA_HOME": str(explicit)}):
            with self.assertRaises(ValueError): research.private_root()

    def test_parent_symlink_rejected(self):
        target = self.base / "target"; target.mkdir(mode=0o700)
        (self.base / "linux-avatar-guard").symlink_to(target, target_is_directory=True)
        with self.assertRaises(OSError): self.saved()
        self.assertEqual(list(target.iterdir()), [])

    def test_file_symlink_rejected(self):
        path = self.saved(); target = path.with_name("original.json"); path.rename(target); path.symlink_to(target.name)
        with self.assertRaises(OSError): research.read_private(path)
        self.assertEqual(json.loads(target.read_text()), self.dataset)

    def test_hardlink_rejected(self):
        path = self.saved(); os.link(path, path.with_name("link.json"))
        with self.assertRaises(ValueError): research.read_private(path)

    def test_public_file_mode_rejected(self):
        path = self.saved(); path.chmod(0o644)
        with self.assertRaises(ValueError): research.read_private(path)

    def test_public_folder_mode_rejected(self):
        path = self.saved(); path.parent.chmod(0o755)
        with self.assertRaises(ValueError): research.read_private(path)

    def test_fifo_rejected_without_blocking(self):
        path = self.saved(); path.unlink(); os.mkfifo(path, 0o600)
        with self.assertRaises(ValueError): research.read_private(path)

    def test_oversize_record_rejected(self):
        path = self.saved(); path.write_bytes(b" " * (1024 * 1024 + 1))
        with self.assertRaises(ValueError): research.read_private(path)

    def test_unknown_schema_rejected(self):
        path = self.saved(); path.write_text('{"schema":2,"purpose":"other"}')
        with self.assertRaises(ValueError): research.read_private(path)


if __name__ == "__main__":
    unittest.main()
