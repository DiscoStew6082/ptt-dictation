import hashlib
from pathlib import Path
import struct
import tempfile
import unittest
import wave

import prepare_corpus


class CorpusTests(unittest.TestCase):
    def test_pcm_dedup_ignores_riff_metadata_and_selects_next_distinct_recordings(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = []
            for index, pcm in enumerate((bytes([1, 0]) * 32, bytes([2, 0]) * 32, bytes([3, 0]) * 32)):
                path = root / f'{index}.wav'
                with wave.open(str(path), 'wb') as wav:
                    wav.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
                    wav.writeframes(pcm)
                paths.append(path)
            def with_metadata(source, name):
                data = source.read_bytes()
                chunk = b'JUNK' + struct.pack('<I', 4) + b'test'
                changed = data[:4] + struct.pack('<I', len(data) - 8 + len(chunk)) + data[8:12] + chunk + data[12:]
                target = root / name
                target.write_bytes(changed)
                self.assertNotEqual(hashlib.sha256(data).digest(), hashlib.sha256(changed).digest())
                return target
            same_existing = with_metadata(paths[0], 'same-existing.wav')
            same_new = with_metadata(paths[1], 'same-new.wav')
            seen = {prepare_corpus.pcm_digest(paths[0])}
            candidates = [dict(id=str(i), path=str(p)) for i, p in enumerate((same_existing, paths[1], same_new, paths[2]))]
            selected = list(prepare_corpus.take_distinct(candidates, seen, 2))
            self.assertEqual([row['id'] for row in selected], ['1', '3'])
            self.assertEqual(len(seen), 3)
            self.assertTrue(all('pcm_sha256' in row for row in selected))


if __name__ == '__main__':
    unittest.main()
