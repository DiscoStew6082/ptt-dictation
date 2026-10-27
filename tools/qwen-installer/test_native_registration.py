"""Registration rejection tests; never load a model or modify real app settings."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.name == 'nt' and shutil.which('pwsh'), 'Windows PowerShell 7 required')
class NativeRegistrationTests(unittest.TestCase):
    def test_corrupt_source_cannot_replace_registration_or_create_installation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, install, app = root/'source', root/'installed', root/'app'
            source.mkdir(); app.mkdir()
            sentinel = b'{"existing":"native registration must survive"}'
            manifest = app/'qwen-native-installation.json'
            manifest.write_bytes(sentinel)
            plan = json.loads((ROOT/'tools/qwen-installer/native-assets.json').read_text())
            first = source/plan['files'][0]['path']
            first.parent.mkdir(parents=True)
            first.write_bytes(b'corrupted pinned binary')
            process = subprocess.run([shutil.which('pwsh'), '-NoProfile', '-File',
                str(ROOT/'scripts/Register-QwenNativeInstallation.ps1'),
                '-SourceDirectory', str(source), '-InstallDirectory', str(install),
                '-AppDataPath', str(app)], capture_output=True, text=True, timeout=30)
            self.assertNotEqual(process.returncode, 0)
            self.assertIn('failed pinned size/SHA256 validation', process.stderr)
            self.assertEqual(manifest.read_bytes(), sentinel)
            self.assertFalse(install.exists())
            self.assertEqual(sorted(path.name for path in app.iterdir()), [manifest.name])


if __name__ == '__main__':
    unittest.main()
