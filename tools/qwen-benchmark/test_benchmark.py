import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
import wave

import benchmark


class ScoringTests(unittest.TestCase):
    def test_known_substitution_deletion_and_insertion(self):
        self.assertEqual(benchmark.word_errors('one two three', 'one four three five'), (2, 3))
        self.assertEqual(benchmark.word_errors('one two three', 'one three'), (1, 3))

    def test_case_punctuation_and_title_normalization(self):
        self.assertEqual(benchmark.word_errors('MISTER QUILTER IS HERE', 'Mr. Quilter is here.'), (0, 4))
        self.assertEqual(benchmark.word_errors('do not', "don't"), (2, 2))

    def test_empty_reference_counts_hallucination(self):
        self.assertEqual(benchmark.word_errors('', 'Okay.'), (1, 0))

    def test_comparison_does_not_hide_one_regression_in_aggregate(self):
        def sample(identity, errors, seconds, digest):
            return {'id':identity,'median_seconds':seconds,'p95_seconds':seconds,'stable_text':True,
                    'runs':[{'errors':errors,'reference_words':10,'text_sha256':digest}]}
        baseline = {'completed':True,'manifest_sha256':'same','results':[sample('one',0,2,'a'),sample('two',5,2,'b')]}
        candidate = {'completed':True,'manifest_sha256':'same','results':[sample('one',1,1,'c'),sample('two',0,1,'d')]}
        result = benchmark.compare_reports(baseline, candidate)
        self.assertEqual(result['regressions'], ['one'])
        self.assertEqual(result['cases'][0]['speedup'], 2)

    def test_partial_or_different_corpus_cannot_compare(self):
        with self.assertRaises(ValueError):
            benchmark.compare_reports({'completed':False}, {'completed':True})


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def command(self, body):
        script = self.root / 'worker.py'
        script.write_text(body, encoding='utf-8')
        return [sys.executable, '-u', str(script)]

    def test_actual_process_readiness_request_id_and_text(self):
        command = self.command('import json,sys\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nfor line in sys.stdin:\n r=json.loads(line); print(json.dumps({"type":"result","id":r["id"],"text":"spoken words"}),flush=True)\n')
        with benchmark.Worker(command, 3, 3) as worker:
            text, elapsed = worker.transcribe(self.root / 'fixture.wav')
            self.assertEqual(text, 'spoken words')
            self.assertGreaterEqual(elapsed, 0)
        self.assertIsNotNone(worker.process.poll())

    def bootstrap_body(self, announcement):
        return ('import json,sys,os,pathlib\n'
                'print(json.dumps('+announcement+'),flush=True)\n'
                'assert json.loads(sys.stdin.readline()) == {"type":"start","protocol":1}\n'
                'pathlib.Path('+repr(str(self.root / 'loaded'))+').touch()\n'
                'print(json.dumps({"type":"ready","protocol":1}),flush=True)\n'
                'for line in sys.stdin:\n'
                ' r=json.loads(line); print(json.dumps({"type":"result","id":r["id"],"text":"bootstrapped"}),flush=True)\n')

    def test_bootstrap_ack_precedes_loading_and_preserves_requests(self):
        command = self.command(self.bootstrap_body('{"type":"bootstrap","protocol":1,"pid":os.getpid()}'))
        with benchmark.Worker(command, 3, 3) as worker:
            self.assertTrue((self.root / 'loaded').exists())
            self.assertEqual(worker.transcribe(self.root / 'fixture.wav')[0], 'bootstrapped')

    def test_bootstrap_accepts_owned_descendant_launcher(self):
        child = self.root / 'actual-worker.py'
        child.write_text(self.bootstrap_body('{"type":"bootstrap","protocol":1,"pid":os.getpid()}'))
        command = self.command('import subprocess,sys\nsubprocess.run([sys.executable,"-u",'+repr(str(child))+'],check=True)\n')
        with benchmark.Worker(command, 3, 3) as worker:
            self.assertEqual(worker.transcribe(self.root / 'fixture.wav')[0], 'bootstrapped')

    def test_unrelated_bootstrap_pid_rejected_before_loading(self):
        command = self.command(self.bootstrap_body(repr(dict(type='bootstrap', protocol=1, pid=os.getpid()))))
        with self.assertRaisesRegex(RuntimeError, 'owned'):
            benchmark.Worker(command, 3, 3)
        self.assertFalse((self.root / 'loaded').exists())

    def test_malformed_bootstrap_rejected_before_loading(self):
        for announcement in (dict(type='bootstrap', protocol=2, pid=1),
                             dict(type='bootstrap', protocol=1, pid=True),
                             dict(type='bootstrap', protocol=1, pid='1'),
                             dict(type='bootstrap', protocol=1, pid=-1),
                             dict(type='bootstrap', protocol=1),
                             dict(type='bootstrap', protocol=1, pid=1, extra=True)):
            with self.subTest(announcement=announcement):
                command = self.command(self.bootstrap_body(repr(announcement)))
                with self.assertRaisesRegex(RuntimeError, 'bootstrap'):
                    benchmark.Worker(command, 3, 3)
                self.assertFalse((self.root / 'loaded').exists())

    def test_bootstrap_does_not_remove_readiness_deadline(self):
        command = self.command('import json,sys,os,time\nprint(json.dumps({"type":"bootstrap","protocol":1,"pid":os.getpid()}),flush=True)\nsys.stdin.readline()\ntime.sleep(30)\n')
        with self.assertRaises(TimeoutError):
            benchmark.Worker(command, .2, 3)

    def test_wrong_response_id_is_rejected_and_process_stopped(self):
        command = self.command('import json,sys,time\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nsys.stdin.readline()\nprint(json.dumps({"type":"result","id":"wrong","text":"bad"}),flush=True)\ntime.sleep(30)\n')
        with self.assertRaisesRegex(RuntimeError, 'request id'):
            with benchmark.Worker(command, 3, 3) as worker:
                worker.transcribe(self.root / 'fixture.wav')
        self.assertIsNotNone(worker.process.poll())

    def test_request_timeout_stops_process(self):
        command = self.command('import json,time\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\ntime.sleep(30)\n')
        with self.assertRaises(TimeoutError):
            with benchmark.Worker(command, 3, .05) as worker:
                worker.transcribe(self.root / 'fixture.wav')
        self.assertIsNotNone(worker.process.poll())

    def test_readiness_deadline_is_bounded(self):
        command = self.command('import time\ntime.sleep(30)\n')
        start = time.perf_counter()
        with self.assertRaises(TimeoutError):
            benchmark.Worker(command, .05, 3)
        self.assertLess(time.perf_counter() - start, 3)

    @unittest.skipUnless(os.name == 'nt', 'Windows owned-process-tree check')
    def test_containment_or_resume_failure_stops_suspended_process(self):
        marker = self.root / 'executed'
        command = self.command('import pathlib,time\npathlib.Path('+repr(str(marker))+').touch()\ntime.sleep(30)\n')
        original = benchmark.subprocess.Popen
        for seam in ('WindowsJob', 'resume_suspended_process'):
            with self.subTest(seam=seam):
                spawned = []
                def capture(*args, **kwargs):
                    process = original(*args, **kwargs)
                    spawned.append(process)
                    return process
                with patch.object(benchmark.subprocess, 'Popen', side_effect=capture), \
                        patch.object(benchmark, seam, side_effect=OSError('injected containment failure')):
                    with self.assertRaisesRegex(OSError, 'injected containment'):
                        benchmark.Worker(command, 3, 3)
                self.assertIsNotNone(spawned[0].poll())
                self.assertFalse(marker.exists())

    def test_silence_only_manifest_completes_cli_without_warmup(self):
        audio = self.root / 'silence.wav'
        with wave.open(str(audio), 'wb') as wav:
            wav.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            wav.writeframes(bytes(320))
        manifest, command_file, output = [self.root / name for name in ('manifest.json', 'command.json', 'result.json')]
        manifest.write_text(json.dumps([dict(id='silence', path=str(audio), sha256=hashlib.sha256(audio.read_bytes()).hexdigest(), reference='', kind='synthetic')]))
        command = self.command('import json,sys\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nfor line in sys.stdin:\n r=json.loads(line); print(json.dumps({"type":"result","id":r["id"],"text":""}),flush=True)\n')
        command_file.write_text(json.dumps(command))
        with patch.object(sys, 'argv', ['benchmark', '--manifest', str(manifest), '--command', str(command_file), '--output', str(output), '--warmup', '0', '--repeats', '1']):
            benchmark.main()
        report = json.loads(output.read_text())
        self.assertTrue(report['completed'])
        self.assertEqual(report['results'][0]['runs'][0]['errors'], 0)

    @unittest.skipUnless(os.name == 'nt', 'Windows owned-process-tree check')
    def test_worker_cannot_spawn_before_job_assignment(self):
        pidfile = self.root / 'early-child.pid'
        command = self.command('import json,sys,subprocess,pathlib\nchild=subprocess.Popen([sys.executable,"-c","import time; time.sleep(30)"],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\npathlib.Path('+repr(str(pidfile))+').write_text(str(child.pid))\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nsys.stdin.readline()\n')
        original = benchmark.subprocess.Popen
        early = []
        def delayed_return(*args, **kwargs):
            process = original(*args, **kwargs)
            time.sleep(.35)
            early.append(pidfile.exists())
            return process
        try:
            with patch.object(benchmark.subprocess, 'Popen', side_effect=delayed_return):
                with benchmark.Worker(command, 3, 3):
                    self.assertTrue(pidfile.exists())
            self.assertEqual(early, [False], 'Worker executed before containment was installed')
        finally:
            # The red reproduction leaves an escaped child; clean only its known PID.
            if pidfile.exists():
                import ctypes
                from ctypes import wintypes
                kernel = ctypes.WinDLL('kernel32', use_last_error=True)
                kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
                kernel.OpenProcess.restype = wintypes.HANDLE
                kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
                kernel.CloseHandle.argtypes = [wintypes.HANDLE]
                handle = kernel.OpenProcess(1, False, int(pidfile.read_text()))
                if handle:
                    kernel.TerminateProcess(handle, 1)
                    kernel.CloseHandle(handle)

    @unittest.skipUnless(os.name == 'nt', 'Windows owned-process-tree check')
    def test_cleanup_releases_spawned_descendant(self):
        import ctypes
        from ctypes import wintypes
        pidfile = self.root / 'child.pid'
        command = self.command('import json,sys,subprocess,pathlib\nchild=subprocess.Popen([sys.executable,"-c","import time; time.sleep(30)"])\npathlib.Path('+repr(str(pidfile))+').write_text(str(child.pid))\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nsys.stdin.readline()\n')
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        with benchmark.Worker(command, 3, 3):
            handle = kernel.OpenProcess(0x100000, False, int(pidfile.read_text()))
            self.assertTrue(handle)
        try:
            self.assertEqual(kernel.WaitForSingleObject(handle, 3000), 0)
        finally:
            kernel.CloseHandle(handle)

    def test_changed_fixture_fails_before_model_start(self):
        path = self.root / 'a.wav'
        path.write_bytes(b'changed')
        manifest = self.root / 'manifest.json'
        manifest.write_text(json.dumps([{'id':'x','path':str(path),'sha256':hashlib.sha256(b'original').hexdigest(),'reference':'a','kind':'public'}]))
        with self.assertRaisesRegex(ValueError, 'hash'):
            benchmark.load_manifest(manifest)

    def test_network_fixture_rejected_before_access(self):
        manifest = self.root / 'manifest.json'
        manifest.write_text(json.dumps([{'id':'x','path':r'\\server\share\audio.wav','sha256':'unused','reference':'a','kind':'public'}]))
        with self.assertRaisesRegex(ValueError, 'local'):
            benchmark.load_manifest(manifest)


if __name__ == '__main__':
    unittest.main()
