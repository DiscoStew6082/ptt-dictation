"""Native adapter behavioral tests: local fake server, no model or GPU."""
import base64
import contextlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import wave

import native_qwen as native
import qwen_worker as worker


FAKE_SERVER = r'''
import argparse,json,sys,time,pathlib
from http.server import BaseHTTPRequestHandler,HTTPServer
p=argparse.ArgumentParser();p.add_argument('--port',type=int);p.add_argument('--key');p.add_argument('--alias');p.add_argument('--response');p.add_argument('--capture');a=p.parse_args()
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def do_GET(self):
  if self.headers.get('Authorization')!='Bearer '+a.key: self.send_error(401);return
  if self.path=='/redirected': pathlib.Path(a.capture+'.redirected').touch()
  self.send_response(200);self.end_headers();self.wfile.write(json.dumps({'data':[{'id':a.alias}]}).encode())
 def do_POST(self):
  if self.headers.get('Authorization')!='Bearer '+a.key: self.send_error(401);return
  body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
  open(a.capture,'w').write(json.dumps(body))
  response=json.load(open(a.response))
  if response.get('_redirect'):
   self.send_response(302);self.send_header('Location','/redirected');self.end_headers();return
  time.sleep(response.get('_delay',0))
  self.send_response(200);self.end_headers();self.wfile.write(json.dumps(response).encode())
HTTPServer(('127.0.0.1',a.port),Handler).serve_forever()
'''


class NativeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.model = self.root / 'hf'
        self.model.mkdir()
        self.asset = self.root / 'model.gguf'
        self.asset.write_bytes(b'fixture')
        self.config = self.root / 'native.json'
        self.configuration = dict(schema=1, serverPath=sys.executable, modelPath=str(self.asset),
            projectorPath=str(self.asset), modelId=native.MODEL_ID, llamaCommit=native.LLAMA_COMMIT,
            modelRevision=native.MODEL_REVISION)
        self.config.write_text(json.dumps(self.configuration))
        self.script = self.root / 'server.py'
        self.script.write_text(FAKE_SERVER)
        self.response = self.root / 'response.json'
        self.capture = self.root / 'capture.json'
        self.set_response()
        self.audio = self.root / 'speech.wav'
        with wave.open(str(self.audio), 'wb') as wav:
            wav.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            wav.writeframes(struct.pack('<hh', 100, -100) * 16000)
        self.recording = worker.read_recording(str(self.audio))

    def set_response(self, text='language English<asr_text>Spoken words.', count=4, finish='stop', **extra):
        response = {'choices':[{'message':{'content':text}, 'finish_reason':finish}],
                    'usage':{'completion_tokens':count,'prompt_tokens':100}}
        response.update(extra)
        self.response.write_text(json.dumps(response))

    def command(self, config, port, key, alias):
        return [sys.executable, '-u', str(self.script), '--port', str(port), '--key', key,
                '--alias', alias, '--response', str(self.response), '--capture', str(self.capture)]

    @contextlib.contextmanager
    def backend(self):
        with patch.object(native, 'server_command', side_effect=self.command):
            backend = native.NativeBackend(self.config, startup_timeout=3, request_timeout=2)
            try:
                yield backend
            finally:
                backend.close()

    def test_whole_audio_greedy_english_and_no_prompt_cache(self):
        with self.backend() as backend:
            result = backend.transcribe(self.recording)
        self.assertEqual(result, worker.GeneratedTranscript('Spoken words.', 4, 512))
        request = json.loads(self.capture.read_text())
        self.assertEqual(request['messages'][0], {'role':'system','content':'English'})
        audio = request['messages'][1]['content'][0]['input_audio']
        with wave.open(io.BytesIO(base64.b64decode(audio['data'])), 'rb') as wav:
            self.assertEqual(wav.readframes(wav.getnframes()), self.recording.pcm)
        self.assertEqual((request['temperature'],request['top_k'],request['repeat_penalty']), (0,1,1))
        self.assertEqual(request['max_tokens'],512)
        self.assertIs(request['cache_prompt'],False)
        self.assertIs(request['stream'],False)
        self.assertIsNotNone(backend.process.poll())

    def test_generation_budget_retains_4096_maximum(self):
        recording = worker.Recording(b'\x64\x00',16000,999,100)
        with self.backend() as backend:
            backend.transcribe(recording)
        self.assertEqual(json.loads(self.capture.read_text())['max_tokens'],4096)

    def test_truncation_and_unexpected_output_never_return_partial_text(self):
        with self.backend() as backend:
            for kwargs in ({'finish':'length'}, {'count':512}, {'count':-1}, {'count':True},
                           {'text':'Spoken words.'}, {'text':'language French<asr_text>Bonjour'},
                           {'text':'language English<asr_text>one<asr_text>two'},
                           {'truncated':True}):
                with self.subTest(kwargs=kwargs):
                    self.set_response(**kwargs)
                    with self.assertRaises(worker.WorkerError):
                        backend.transcribe(self.recording)

    def test_schema_revision_and_local_asset_validation_precedes_launch(self):
        for key, value in [('schema',2),('schema',True),('modelId','other'),('llamaCommit','other'),
                           ('modelRevision','other'),('modelPath','https://remote/model')]:
            config = dict(self.configuration, **{key:value})
            self.config.write_text(json.dumps(config))
            with self.subTest(key=key), patch.object(native.subprocess,'Popen') as launch:
                with self.assertRaises(worker.WorkerError): native.NativeBackend(self.config)
                launch.assert_not_called()

    def test_command_binds_only_loopback_disables_context_shift_and_logs(self):
        command = native.server_command(native.read_config(self.config),12345,'ephemeral-key','own-alias')
        for flag,value in [('--host','127.0.0.1'),('--port','12345'),('-c','8192'),('-np','1'),
                           ('--api-key','ephemeral-key'),('--alias','own-alias'),('--device','CUDA0')]:
            self.assertEqual(command[command.index(flag)+1], value)
        for flag in ('--no-context-shift','--log-disable','--no-webui','--jinja'):
            self.assertIn(flag,command)

    def test_startup_failure_stops_only_owned_process(self):
        processes = []
        original = native.subprocess.Popen
        def capture(*args, **kwargs):
            process = original(*args, **kwargs); processes.append(process); return process
        with patch.object(native,'server_command',return_value=[sys.executable,'-c','import time;time.sleep(30)']), \
                patch.object(native.subprocess,'Popen',side_effect=capture):
            with self.assertRaises(worker.WorkerError):
                native.NativeBackend(self.config,startup_timeout=.15)
        self.assertEqual(len(processes),1)
        self.assertIsNotNone(processes[0].poll())

    def test_collision_does_not_accept_or_stop_unrelated_server(self):
        with self.backend() as existing:
            with patch.object(native,'unused_port',return_value=existing.port), \
                    patch.object(native,'server_command',side_effect=self.command):
                with self.assertRaises(worker.WorkerError):
                    native.NativeBackend(self.config,startup_timeout=.4)
            self.assertIsNone(existing.process.poll())
            self.assertEqual(existing.transcribe(self.recording).text,'Spoken words.')

    def test_eof_cleanup_and_silence_reuses_public_protocol(self):
        with wave.open(str(self.audio),'wb') as wav:
            wav.setparams((1,2,16000,0,'NONE','not compressed')); wav.writeframes(bytes(16000*2))
        destination = io.StringIO()
        with self.backend() as backend:
            with patch.object(native,'NativeBackend',return_value=backend):
                code = native.run_native_worker(str(self.model),str(self.config),
                    io.StringIO('{"type":"start","protocol":1}\n'+json.dumps({'id':'silence','audioPath':str(self.audio)})+'\n'),destination)
            self.assertIsNotNone(backend.process.poll())
        messages = [json.loads(line) for line in destination.getvalue().splitlines()]
        self.assertEqual(code,0)
        self.assertEqual(messages,[{'type':'bootstrap','protocol':1,'pid':os.getpid()},
            {'type':'ready','protocol':1},{'id':'silence','type':'result','text':''}])
        self.assertFalse(self.capture.exists())

    def test_request_failure_is_redacted_and_next_request_can_recover(self):
        with self.backend() as backend:
            self.response.write_text('{}')
            request = json.dumps({'id':'bad','audioPath':str(self.audio)})
            failed = worker.process_request(request,backend)
            self.assertEqual(failed['type'],'error')
            self.assertNotIn(str(self.root),failed['message'])
            self.set_response()
            self.assertEqual(worker.process_request(request,backend)['text'],'Spoken words.')

    def test_bootstrap_requires_ownership_ack_before_any_child(self):
        for acknowledgement in ('','{}\n','{"type":"start","protocol":2}\n','{"type":"start","protocol":true}\n'):
            with self.subTest(acknowledgement=acknowledgement), patch.object(native,'NativeBackend') as backend:
                with self.assertRaises(worker.WorkerError):
                    native.run_native_worker(str(self.model),str(self.config),io.StringIO(acknowledgement),io.StringIO())
                backend.assert_not_called()

    def test_long_audio_uses_original_request_in_separate_hf_worker_then_reloads_native(self):
        from unittest.mock import Mock
        first, second = Mock(), Mock()
        first.transcribe.return_value = second.transcribe.return_value = worker.GeneratedTranscript('native',2,512)
        request = json.dumps({'id':'original-id','audioPath':str(self.audio)})
        def fallback(model,line):
            first.close.assert_called_once()
            self.assertEqual(model,self.model)
            self.assertEqual(line,request)
            return 'full original recording'
        with patch.object(native,'NativeBackend',side_effect=[first,second]) as factory, \
                patch.object(native,'run_hf_request',side_effect=fallback) as hf:
            router = native.RequestRouter(self.model,self.config)
            try:
                with patch.object(worker,'read_recording',return_value=worker.Recording(b'\x64\x00',16000,300,100)):
                    self.assertEqual(router.process(request)['text'],'native')
                    hf.assert_not_called()
                with patch.object(worker,'read_recording',return_value=worker.Recording(b'\x64\x00',16000,300.001,100)):
                    self.assertEqual(router.process(request)['text'],'full original recording')
                    hf.assert_called_once()
                self.assertEqual(factory.call_count,1)
                self.assertEqual(router.process(request)['text'],'native')
                self.assertEqual(factory.call_count,2)
            finally:
                router.close()
            second.close.assert_called_once()

    def test_hf_subprocess_protocol_limits_errors_and_cleanup(self):
        script = self.root/'hf.py'
        script.write_text('import json,sys\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nr=json.loads(sys.stdin.readline());print(json.dumps({"id":r["id"],"type":"result","text":"whole audio"}),flush=True)\n')
        request = json.dumps({'id':'same','audioPath':str(self.audio)})
        with patch.object(native,'hf_command',return_value=[sys.executable,'-u',str(script)]):
            self.assertEqual(native.run_hf_request(self.model,request,timeout=3),'whole audio')
        script.write_text('import json,sys\nprint(json.dumps({"type":"ready","protocol":1}),flush=True)\nsys.stdin.readline();print(json.dumps({"id":"wrong","type":"result","text":"partial"}),flush=True)\n')
        with patch.object(native,'hf_command',return_value=[sys.executable,'-u',str(script)]):
            with self.assertRaises(worker.WorkerError): native.run_hf_request(self.model,request,timeout=3)
        script.write_text('import time\ntime.sleep(30)\n')
        processes=[]
        original=native.subprocess.Popen
        def capture(*args,**kwargs):
            process=original(*args,**kwargs);processes.append(process);return process
        with patch.object(native,'hf_command',return_value=[sys.executable,'-u',str(script)]), \
                patch.object(native.subprocess,'Popen',side_effect=capture):
            with self.assertRaises(worker.WorkerError): native.run_hf_request(self.model,request,timeout=.1)
        self.assertIsNotNone(processes[0].poll())

    def test_server_cannot_redirect_audio_or_credentials(self):
        with self.backend() as backend:
            self.set_response(_redirect=True)
            with self.assertRaises(worker.WorkerError): backend.transcribe(self.recording)
            self.assertFalse(Path(str(self.capture)+'.redirected').exists())

    @unittest.skipUnless(os.name == 'nt','Windows parent cancellation and nested jobs')
    def test_actual_wrapper_cancellation_kills_resident_server(self):
        import ctypes
        from ctypes import wintypes
        import time
        entry=self.root/'entry.py'
        server_pid=self.root/'server.pid'
        command=[sys.executable,'-u',str(self.script)]
        entry.write_text('import sys,pathlib\nsys.path.insert(0,'+repr(str(Path(native.__file__).parent))+')\nimport native_qwen as n\n'+
            'n.server_command=lambda c,p,k,a: '+repr(command)+'+["--port",str(p),"--key",k,"--alias",a,"--response",'+repr(str(self.response))+',"--capture",'+repr(str(self.capture))+']\n'+
            'original=n.NativeBackend\nclass Backend(original):\n def __init__(self,path):\n  super().__init__(path,startup_timeout=3);pathlib.Path('+repr(str(server_pid))+').write_text(str(self.process.pid))\nn.NativeBackend=Backend\nraise SystemExit(n.main())\n')
        self.set_response(_delay=30)
        process,job=native.start_owned_process([sys.executable,'-B','-u',str(entry),'--model',str(self.model),'--native-config',str(self.config)],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf-8')
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];kernel.OpenProcess.restype=wintypes.HANDLE
        kernel.TerminateProcess.argtypes=[wintypes.HANDLE,wintypes.UINT]
        kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]
        kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        wrapper_handle=server_handle=None
        try:
            bootstrap=json.loads(process.stdout.readline())
            self.assertEqual(bootstrap['type'],'bootstrap')
            process.stdin.write('{"type":"start","protocol":1}\n');process.stdin.flush()
            self.assertEqual(json.loads(process.stdout.readline()),{'type':'ready','protocol':1})
            wrapper_handle=kernel.OpenProcess(0x100001,False,bootstrap['pid'])
            server_handle=kernel.OpenProcess(0x100000,False,int(server_pid.read_text()))
            self.assertTrue(wrapper_handle and server_handle)
            process.stdin.write(json.dumps({'id':'cancelled','audioPath':str(self.audio)})+'\n');process.stdin.flush()
            deadline=time.monotonic()+3
            while not self.capture.exists() and time.monotonic()<deadline: time.sleep(.01)
            self.assertTrue(self.capture.exists(),'Request must actually be in flight before cancellation')
            self.assertTrue(kernel.TerminateProcess(wrapper_handle,1))
            self.assertEqual(kernel.WaitForSingleObject(wrapper_handle,3000),0)
            self.assertEqual(kernel.WaitForSingleObject(server_handle,3000),0,'Worker death must close its nested server job')
        finally:
            native.stop_owned_process(process,job)
            for handle in (wrapper_handle,server_handle):
                if handle: kernel.CloseHandle(handle)


if __name__ == '__main__':
    unittest.main()
