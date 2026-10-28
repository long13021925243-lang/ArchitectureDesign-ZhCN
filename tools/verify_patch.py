"""Verify the complete binary patch against a user-supplied original game executable."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', required=True, type=Path)
    parser.add_argument('--output', type=Path, help='Optional new output file; never overwrites an existing file')
    parser.add_argument('--manifest', type=Path, default=ROOT / 'data/manifest.json')
    args = parser.parse_args()
    source = args.original.resolve(strict=True)
    output = args.output.resolve() if args.output else None
    if output is not None and (output == source or output.exists()):
        parser.error('--output must be a new file, different from --original')
    manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
    original = source.read_bytes()
    if hashlib.sha256(original).hexdigest() != manifest['originalSha256']:
        raise ValueError('Unsupported original executable SHA-256; no files changed')
    result = bytearray(original)
    end = 0
    patches = sorted(manifest['patches'], key=lambda item: item['offset'])
    for patch in patches:
        offset = patch['offset']
        old = bytes.fromhex(patch['oldHex'])
        new = bytes.fromhex(patch['newHex'])
        if offset < end or not old or len(old) != len(new) or offset < 0 or offset + len(old) > len(original):
            raise ValueError('Invalid or overlapping patch range')
        if original[offset:offset + len(old)] != old:
            raise ValueError('Original byte mismatch at offset ' + str(offset))
        if result[end:offset] != original[end:offset]:
            raise ValueError('Unexpected mutation outside declared patch ranges')
        result[offset:offset + len(old)] = new
        end = offset + len(old)
    if result[end:] != original[end:]:
        raise ValueError('Unexpected mutation after final patch range')
    result.extend(bytes.fromhex(manifest.get('appendHex', '')))
    actual = hashlib.sha256(result).hexdigest()
    if actual != manifest['patchedSha256']:
        raise ValueError('Generated executable differs from the audited target hash')
    if output is not None:
        with output.open('xb') as stream:
            stream.write(result)
    print(json.dumps({'status': 'PASS', 'patchCount': len(patches), 'bytes': len(result),
                      'patchedSha256': actual, 'wroteOutput': output is not None}, indent=2))

if __name__ == '__main__':
    main()
