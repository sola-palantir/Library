#!/usr/bin/env python3
"""Build an offline preview bundle. Developer/release-host tool, not an operator prerequisite.
No registry push, GitHub Release, signing, install or service startup is performed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)

def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as file:
        for block in iter(lambda: file.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET', 'dotnet'))
    parser.add_argument('--skip-zip', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+-preview\.\d+', args.version):
        parser.error('Only explicitly unsigned preview versions may be packaged by this workflow.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    image = 'library-local-preview:' + args.version
    run('docker', 'build', '--platform', 'linux/amd64', '--build-arg', 'LIBRARY_VERSION=' + args.version, '-t', image, '.')
    image_id = subprocess.check_output(['docker', 'image', 'inspect', '--format', '{{.Id}}', image], text=True).strip()
    run('docker', 'save', '--output', str(output / 'api-image.tar'), image_id)
    for project, target in [('companion/Library.Companion', 'companion'), ('deploy/Library.Manager', 'manager')]:
        run(args.dotnet, 'publish', project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:Version=' + args.version, '-p:RuntimeFrameworkVersion=10.0.13', '-p:PublishSingleFile=false', '-o', str(output / target))
        runtime = json.loads((output / target / ('Library.Companion.runtimeconfig.json' if target == 'companion' else 'Library.Manager.runtimeconfig.json')).read_text())
        if runtime['runtimeOptions']['includedFrameworks'][0]['version'] != '10.0.13':
            raise ValueError('Unexpected self-contained runtime version.')
        package = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages'))) / 'microsoft.netcore.app.runtime.win-x64/10.0.13'
        for notice in ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']:
            shutil.copy2(package / notice, output / target / notice)
    docs_url = 'https://github.com/sola-palantir/Library/blob/feat/library-foundation/docs/'
    readme = (ROOT / 'README.md').read_text(encoding='utf-8-sig').replace('](docs/deployment.md)', '](deployment.md)').replace('](docs/', '](' + docs_url)
    (output / 'README.md').write_text(readme, encoding='utf-8')
    guide = (ROOT / 'docs/deployment.md').read_text(encoding='utf-8-sig').replace('](development.md)', '](' + docs_url + 'development.md)').replace('](end-to-end-qualification.md)', '](' + docs_url + 'end-to-end-qualification.md)')
    (output / 'deployment.md').write_text(guide, encoding='utf-8')
    (output / 'NOTICE.txt').write_text(
        'Unsigned Library preview. No signing identity or public redistribution approval is implied.\n'
        'The companion/manager include .NET 10.0.13; retain their LICENSE.TXT and THIRD-PARTY-NOTICES.TXT.\n'
        'The Linux API image includes distro FFmpeg and system package licensing metadata.\n'
        'Windows FFmpeg is not bundled or needed for catalog mode; production processing stays off.\n'
        'No open-source license for Library has been selected. Public release publication remains gated.\n', encoding='utf-8')
    files = [{'path': p.relative_to(output).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p)}
             for p in sorted(output.rglob('*')) if p.is_file()]
    manifest = dict(format=1, version=args.version, protocol=1, deploymentProtocol=1, catalogSchema=6,
                    mode='catalog-only', windowsRuntimeVersion='10.0.13', imageId=image_id, imageFile='api-image.tar', signed=False, files=files)
    (output / 'release.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    manifest_hash = sha(output / 'release.json')
    (output.parent / (output.name + '.manifest.sha256')).write_text(manifest_hash + '  release.json\n', encoding='ascii')
    if not args.skip_zip:
        archive = Path(shutil.make_archive(str(output), 'zip', output))
        archive.with_suffix('.zip.sha256').write_text(sha(archive) + '  ' + archive.name + '\n', encoding='ascii')
    print(json.dumps({'bundle': str(output), 'version': args.version, 'manifestSha256': manifest_hash, 'imageId': image_id, 'signed': False}))

if __name__ == '__main__':
    main()
