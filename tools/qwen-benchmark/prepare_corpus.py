"""Download a verified public LibriSpeech subset and prepare local PCM fixtures."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import urllib.request
import wave

URL = 'https://openslr.elda.org/resources/12/dev-clean.tar.gz'
# Published by OpenSLR at https://openslr.trmal.net/resources/12/md5sum.txt
MD5 = '42e2234ba48799c1f50f24a7926300a1'


def pcm_digest(path):
    """Identify audio samples independently of RIFF headers and metadata."""
    with wave.open(str(path), 'rb') as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) != (1, 2, 16000, 'NONE'):
            raise ValueError('Deduplication requires 16 kHz mono PCM16 WAV')
        return hashlib.sha256(wav.readframes(wav.getnframes())).hexdigest()


def take_distinct(candidates, seen, count):
    selected = 0
    for sample in candidates:
        digest = pcm_digest(sample['path'])
        if digest in seen:
            continue
        seen.add(digest)
        yield dict(sample, pcm_sha256=digest)
        selected += 1
        if selected == count:
            return
    raise ValueError(f'Only {selected} distinct utterances available; expected {count}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--ffmpeg', required=True, type=Path)
    parser.add_argument('--existing-public', type=Path)
    parser.add_argument('--archive', type=Path, help='Use an existing verified archive without downloading')
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    archive = args.archive.resolve(strict=True) if args.archive else root / 'dev-clean.tar.gz'
    if not archive.exists():
        partial = archive.with_suffix('.partial')
        with urllib.request.urlopen(URL, timeout=60) as response, partial.open('wb') as destination:
            shutil.copyfileobj(response, destination, length=1024*1024)
        partial.replace(archive)
    with archive.open('rb') as source:
        digest = hashlib.file_digest(source, 'md5').hexdigest()
    if digest != MD5:
        raise ValueError('OpenSLR archive checksum mismatch')
    manifest, seen_pcm = [], set()
    if args.existing_public:
        old = json.loads(args.existing_public.read_text(encoding='utf-8-sig'))
        for sample in old:
            if sample.get('kind') == 'public_reference':
                path = (args.existing_public.parent/sample['file']).resolve()
                if hashlib.sha256(path.read_bytes()).hexdigest() != sample['sha256']:
                    raise ValueError('Existing public fixture checksum mismatch: ' + sample['id'])
                digest = pcm_digest(path)
                if digest in seen_pcm:
                    continue
                seen_pcm.add(digest)
                manifest.append(dict(id=sample['id'], path=str(path), pcm_sha256=digest,
                    sha256=sample['sha256'], reference=sample['reference'], kind='public',
                    speaker=str(sample['source_speaker_id']), source=sample['source_dataset']))
    with tarfile.open(archive, mode='r:gz') as tar:
        members = sorted((m for m in tar.getmembers() if m.isfile() and m.name.endswith('.flac')), key=lambda m:m.name)
        speaker_ids = sorted({PurePosixPath(m.name).parts[-3] for m in members}, key=int)[:10]
        references = {}
        for member in tar.getmembers():
            if member.isfile() and member.name.endswith('.trans.txt'):
                with tar.extractfile(member) as source:
                    for line in source.read().decode('utf-8').splitlines():
                        identity, text = line.split(' ', 1)
                        references[identity] = text
        def converted(candidates):
            for member in candidates:
                # Never extract archive paths; write only our validated numeric fixture filename.
                identity = PurePosixPath(member.name).stem
                if not all(part.isdigit() for part in identity.split('-')) or len(identity.split('-')) != 3:
                    raise ValueError('Unexpected LibriSpeech sample identity')
                flac = root / (identity + '.flac')
                wav = root / (identity + '.wav')
                with tar.extractfile(member) as source, flac.open('wb') as destination:
                    shutil.copyfileobj(source, destination)
                subprocess.run([str(args.ffmpeg.resolve()), '-hide_banner', '-loglevel', 'error', '-nostdin',
                    '-i', str(flac), '-ac', '1', '-ar', '16000', '-c:a', 'pcm_s16le', '-y', str(wav)],
                    check=True, timeout=30, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                yield dict(id='dev-'+identity, path=str(wav), sha256=hashlib.sha256(wav.read_bytes()).hexdigest(),
                    reference=references[identity], kind='public', speaker=identity.split('-')[0], source=URL)
        for speaker in speaker_ids:
            candidates = (m for m in members if PurePosixPath(m.name).parts[-3] == speaker)
            manifest.extend(take_distinct(converted(candidates), seen_pcm, 2))
    silence = root/'silence-3s.wav'
    with wave.open(str(silence), 'wb') as out:
        out.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
        out.writeframes(bytes(16000*2*3))
    manifest.append(dict(id='silence-3s',path=str(silence),sha256=hashlib.sha256(silence.read_bytes()).hexdigest(),reference='',kind='synthetic'))
    # Preserve complete sentences and add pauses without changing their expected text.
    long_sources = [m for m in manifest if m['id'].startswith('dev-')][::2][:5]
    long = root/'multi-speaker-pauses.wav'
    with wave.open(str(long),'wb') as out:
        out.setparams((1,2,16000,0,'NONE','not compressed'))
        for index, sample in enumerate(long_sources):
            if index:
                out.writeframes(bytes(16000*2*2))
            with wave.open(sample['path'],'rb') as source:
                out.writeframes(source.readframes(source.getnframes()))
    manifest.append(dict(id='multi-speaker-pauses',path=str(long),sha256=hashlib.sha256(long.read_bytes()).hexdigest(),
        reference=' '.join(m['reference'] for m in long_sources),kind='synthetic',source_ids=[m['id'] for m in long_sources]))
    (root/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
    (root/'provenance.json').write_text(json.dumps(dict(source=URL,md5=MD5,license='CC BY 4.0',
        selection='First two PCM-distinct lexical utterances from each of the ten lowest numeric dev-clean speaker ids, excluding existing public PCM and all prior selections',
        selected_speakers=speaker_ids,fixtures=len(manifest),unique_public_pcm=len(seen_pcm),synthetic_fixtures=2),indent=2),encoding='utf-8')
    print(json.dumps(dict(manifest=str(root/'manifest.json'),fixtures=len(manifest),new_speakers=speaker_ids)))


if __name__ == '__main__':
    main()
