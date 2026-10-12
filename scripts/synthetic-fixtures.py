#!/usr/bin/env python3
"""Create tiny, invented media fixtures only, under this checkout's ignored .local/."""
import json
from pathlib import Path
root = Path(__file__).resolve().parents[1] / '.local'
for name in ('source', 'library', 'staging', 'disc', 'data'):
    (root / name).mkdir(parents=True, exist_ok=True)
files = [
    ('Northlight.2024.1080p.mkv', 'Northlight', '2024', 'A cartographer follows an unfamiliar constellation across a silent northern landscape.', 'Adventure'),
    ('The.Last.Observatory.2022.mkv', 'The Last Observatory', '2022', 'Two astronomers keep watch through the last winter at a remote observatory.', 'Drama'),
    ('Signal.Coast.2023.S01E01.mkv', 'Signal Coast', '2023', 'A forgotten radio station begins broadcasting from the coast.', 'Mystery'),
    ('Signal.Coast.2023.S01E02.mkv', 'Signal Coast', '2023', 'A forgotten radio station begins broadcasting from the coast.', 'Mystery'),
    ('Paper.Moons.2021.mkv', 'Paper Moons', '2021', 'An illustrator turns a small town into a canvas of impossible places.', 'Animation'),
    ('Stillwater.Road.2025.mkv', 'Stillwater Road', '2025', 'An overnight journey brings three strangers to an unexpected crossroads.', 'Drama'),
    ('The.Quiet.Atlas.2020.mp4', 'The Quiet Atlas', '2020', 'A field notebook opens a window onto a disappearing landscape.', 'Documentary'),
    ('After.the.Tide.2024.mkv', 'After the Tide', '2024', 'A lighthouse keeper returns to the island where she grew up.', 'Drama'),
    ('Velvet.Skies.2022.mkv', 'Velvet Skies', '2022', 'An amateur pilot takes one last flight before the summer ends.', 'Adventure'),
]
for i, (name, title, year, plot, genre) in enumerate(files):
    path = root / 'source' / name
    if not path.exists():
        path.write_bytes((f'SYNTHETIC FIXTURE {i}: {title}\n'.encode() * (12000 + i * 900)))
    sidecar = path.with_suffix('.nfo')
    if not sidecar.exists():
        kind = 'episodedetails' if '.S01E' in name else 'movie'
        episode = f'<season>1</season><episode>{int(name.split("S01E")[1][:2])}</episode>' if kind == 'episodedetails' else ''
        sidecar.write_text(f'<{kind}><title>{title}</title><year>{year}</year><plot>{plot}</plot><genre>{genre}</genre>{episode}<synthetic>keep this unknown field</synthetic></{kind}>', encoding='utf-8')
config = {'Library': {'DataPath': str(root / 'data'), 'Roots': [
    {'Id': name, 'Label': label, 'ContainerPath': str(root / name), 'WindowsPath': 'X:\\LibraryFixture\\' + name, 'Role': name}
    for name, label in [('source', 'Synthetic downloads'), ('library', 'Organized library'), ('staging', 'Disc staging'), ('disc', 'Disc read-back')]
]}}
(root / 'config.json').write_text(json.dumps(config, indent=2), encoding='utf-8')
print('Created synthetic fixtures and .local/config.json. These are not playable media.')
