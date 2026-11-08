"""Собирает только текстовые IOC; образцы вредоносных файлов не скачиваются."""
import concurrent.futures
import hashlib
import io
import json
import pathlib
import re
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT / 'rules'
OUT.mkdir(exist_ok=True)
HEADERS = {'User-Agent': 'PCGuard-local-build/3.0'}

def get(url, limit=30_000_000):
    with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=90) as response:
        content = response.read(limit + 1)
    if len(content) > limit:
        raise ValueError('Превышен размер источника: ' + url)
    return content

def snapshot(repo, branch):
    commit = json.loads(get(f'https://api.github.com/repos/{repo}/commits/{branch}'))['sha']
    data = get(f'https://api.github.com/repos/{repo}/git/trees/{commit}?recursive=1')
    return commit, json.loads(data)['tree']

rules = {}
sources = []
old_repo = 'pan-unit42/iocs'
old_commit, old_tree = snapshot(old_repo, 'master')
selected = [x['path'] for x in old_tree if x['type'] == 'blob' and
            (x['path'].startswith('emotet/sha256-hashes/emotet-') or
             x['path'] in ['Trickbot/trickbot_cashcore_hashes.txt', 'DearCry/DearCry.txt'])]

def historical(file):
    url = f'https://raw.githubusercontent.com/{old_repo}/{old_commit}/{file}'
    raw = get(url, 2_000_000)
    family = 'Emotet' if file.startswith('emotet/') else file.split('/')[0]
    return file, raw, family

with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    for file, raw, family in pool.map(historical, selected):
        origin = f'https://github.com/{old_repo}/blob/{old_commit}/{file}'
        count = 0
        for line in raw.decode('utf-8-sig').splitlines():
            value = line.strip().lower()
            if re.fullmatch(r'[0-9a-f]{64}', value):
                rules[value] = {'sha256': value, 'name': family + ': Unit 42 historical sample',
                                'source': origin, 'action': 'block', 'kind': 'malware-hash'}
                count += 1
        (OUT / (family + '-' + pathlib.PurePosixPath(file).name + '.source')).write_bytes(raw)
        sources.append({'path': file, 'commit': old_commit, 'sha256': hashlib.sha256(raw).hexdigest(),
                        'rows': count, 'policy': 'exact-malware-hash', 'historical': True})

# Современные индикаторы кампаний могут содержать инструменты двойного назначения.
# Они создают предупреждение, но не дают автоматическую блокировку/карантин.
repo = 'PaloAltoNetworks/Unit42-timely-threat-intel'
commit, tree = snapshot(repo, 'main')
archive = get(f'https://codeload.github.com/{repo}/zip/{commit}')
with zipfile.ZipFile(io.BytesIO(archive)) as files:
    for item in files.infolist():
        relative = item.filename.split('/', 1)[-1]
        if not relative.endswith('.txt') or item.file_size > 2_000_000:
            continue
        raw = files.read(item)
        values = []
        for line in raw.decode('utf-8-sig', errors='replace').splitlines():
            match = re.match(r'^\s*([a-fA-F0-9]{64})(?=\s|$)', line)
            if match:
                values.append(match[1].lower())
        for value in values:
            rules.setdefault(value, {'sha256': value, 'name': 'Unit 42: ' + relative[:150],
                                    'source': f'https://github.com/{repo}/blob/{commit}/{relative}',
                                    'action': 'alert', 'kind': 'campaign-ioc'})
        if values:
            sources.append({'path': relative, 'commit': commit, 'sha256': hashlib.sha256(raw).hexdigest(),
                            'rows': len(values), 'policy': 'report-only-campaign-ioc', 'historical': False})
    (OUT / 'LICENSE-Unit42-timely.txt').write_bytes(files.read(next(x.filename for x in files.infolist() if x.filename.endswith('/LICENSE'))))

old_license = get(f'https://raw.githubusercontent.com/{old_repo}/{old_commit}/LICENSE')
(OUT / 'LICENSE-Unit42-historical.txt').write_bytes(old_license)
csa = 'https://www.csa.gov.sg/alerts-and-advisories/alerts/al-2025-082/'
for value in ['8fdee53152ec985ffeeeda3d7a85852eb5c9902d2d480449421b4939b1904aad',
              '27d90611f005db3a25a4211cf8f69fb46097c6c374905d7207b30e87d296e1b3']:
    rules[value] = {'sha256': value, 'name': 'Dire Wolf: CSA published sample', 'source': csa,
                    'action': 'block', 'kind': 'malware-hash'}
database = {'schema': 1, 'version': '2026.11.06-3', 'description': 'Historical malware SHA-256 and report-only current campaign IOC; no completeness claim',
            'hashes': [rules[x] for x in sorted(rules)]}
encoded = json.dumps(database, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
if len(encoded) > 16 * 1024 * 1024:
    raise ValueError('База не помещается в ограничение движка')
(OUT / 'engine-seed.json').write_bytes(encoded)
manifest = {'sha256': hashlib.sha256(encoded).hexdigest(), 'bytes': len(encoded), 'rules': len(rules),
            'blockRules': sum(x['action'] == 'block' for x in rules.values()),
            'alertRules': sum(x['action'] == 'alert' for x in rules.values()), 'sources': sources,
            'historicalRepository': old_repo, 'historicalCommit': old_commit,
            'currentRepository': repo, 'currentCommit': commit, 'realMalwareDownloaded': False}
(OUT / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in manifest.items() if key != 'sources'}))
