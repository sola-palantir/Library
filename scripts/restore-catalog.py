#!/usr/bin/env python3
"""Restore an exported SQLite catalog into a NEW offline directory; never alter source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import sys

BASE_TABLES = {'Titles', 'Editions', 'Assets', 'Copies', 'Jobs', 'History', 'Discs', 'Settings', 'Integrations'}

def sync_directory(path: Path):
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_DIRECTORY', 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)

def write_report(path: Path, report):
    with path.open('x', encoding='utf-8') as file:
        file.write(json.dumps(report, indent=2) + '\n')
        file.flush()
        os.fsync(file.fileno())

def restore(source: Path, target: Path):
    source = source.resolve(strict=True)
    if not source.is_file():
        raise ValueError('Backup must be a regular SQLite file.')
    # Do not consume a potentially incomplete backup of a live WAL database.
    if Path(str(source) + '-wal').exists() or Path(str(source) + '-shm').exists():
        raise ValueError('Use an exported standalone backup, not an active catalog with WAL files.')
    schema = Path(__file__).resolve().parents[1] / 'server/Library.Api/CatalogSchema.cs'
    version_match = re.search(r'const int Version\s*=\s*(\d+)', schema.read_text())
    supported = int(version_match.group(1))
    # Standalone export only: immutable read-only avoids creating WAL/SHM beside the source.
    # Caller must keep the exported backup immutable while this offline tool runs.
    with sqlite3.connect(source.as_uri() + '?mode=ro&immutable=1', uri=True) as src:
        src.execute('PRAGMA query_only=ON')
        if src.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
            raise ValueError('Source failed SQLite integrity_check.')
        version = src.execute('PRAGMA user_version').fetchone()[0]
        if version < 3 or version > supported:
            raise ValueError(f'Unsupported schema {version}; this checkout supports 3–{supported}.')
        tables = {r[0] for r in src.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        required = set(BASE_TABLES)
        if version >= 4:
            required.update({'BackupSets', 'BackupPhysicalDiscs', 'BackupEvidence'})
        if version >= 6:
            required.update({'DerivedAssets', 'ProcessingLeases'})
        if required - tables:
            raise ValueError('Missing required tables: ' + ', '.join(sorted(required - tables)))
        if src.execute('PRAGMA foreign_key_check').fetchall():
            raise ValueError('Source failed SQLite foreign_key_check.')
        target = target.absolute()
        # mkdir without exist_ok is the only destination creation gate; refusal includes symlinks.
        target.mkdir(mode=0o700, parents=False, exist_ok=False)
        # Durable refusal marker must exist BEFORE any restored catalog can exist.
        # A crash/failure retains this marker; acknowledgement cannot bypass incomplete restore.
        report_path = target / 'restore-report.json'
        write_report(report_path, {'status': 'restore-incomplete', 'warning': 'Restore did not complete. Do not start this catalog; use a fresh target for another attempt.'})
        sync_directory(target)
        sync_directory(target.parent)
        destination = target / 'catalog.db'
        fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        os.close(fd)
        with sqlite3.connect(destination) as dst:
            src.backup(dst)
            if dst.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
                raise ValueError('Destination failed integrity check; retained for review, do not start service.')
            if dst.execute('PRAGMA user_version').fetchone()[0] != version:
                raise ValueError('Schema changed during restore; retained for review.')
            restored = {r[0] for r in dst.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            if restored != tables:
                raise ValueError('Table set differs after restore; retained for review.')
            # Complete backup includes tables, indexes, immutable-history triggers and pending jobs.
            if src.execute("SELECT type,name,sql FROM sqlite_master ORDER BY type,name").fetchall() != dst.execute("SELECT type,name,sql FROM sqlite_master ORDER BY type,name").fetchall():
                raise ValueError('Schema/history protections differ after restore.')
        with destination.open('rb') as file:
            os.fsync(file.fileno())
        with destination.open('rb') as file:
            if hasattr(hashlib, 'file_digest'):
                checksum = hashlib.file_digest(file, 'sha256').hexdigest()
            else:
                digest = hashlib.sha256()
                for block in iter(lambda: file.read(1024 * 1024), b''):
                    digest.update(block)
                checksum = digest.hexdigest()
        report = {'schemaVersion': version, 'tableCount': len(tables), 'sha256': checksum, 'status': 'restored-offline', 'warning': 'Review roots, disable scheduling and reconcile pending jobs before starting. No media was restored or verified.'}
        # Only replace our own metadata marker after the catalog and report are durable.
        complete_path = target / '.restore-report.complete'
        write_report(complete_path, report)
        os.replace(complete_path, report_path)
        sync_directory(target)
        return report

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('backup', type=Path)
    parser.add_argument('new_data_directory', type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(restore(args.backup, args.new_data_directory), indent=2))
    except (OSError, ValueError, sqlite3.Error) as error:
        print(f'Restore refused/failed: {error}', file=sys.stderr)
        sys.exit(1)
