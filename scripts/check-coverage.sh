#!/usr/bin/env bash
# Fails the build when line coverage falls below a floor.
#
# A floor, not a target: it exists to catch a regression, not to be gamed to exactly 100%.
# It reads every cobertura report under the given directory and merges them by taking the
# highest hit count per line, because a line can be covered by one suite and missed by
# another — summing or averaging the files would understate the real figure.
#
#   ./scripts/check-coverage.sh ./TestResults 80

set -euo pipefail

RESULTS_DIR="${1:-./TestResults}"
FLOOR="${2:-80}"

if ! find "$RESULTS_DIR" -name 'coverage.cobertura.xml' -print -quit | grep -q .; then
  echo "::error::No coverage report found under $RESULTS_DIR — did the collector run?"
  exit 1
fi

# python3 on CI runners; plain python on a Windows dev box. Each candidate is executed before
# being accepted: Windows ships a "python3" App Execution Alias that resolves on PATH, prints an
# advert for the Store and exits 0 — silently turning this gate into a no-op that always passes.
PYTHON=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import sys" >/dev/null 2>&1; then
    PYTHON="$candidate"
    break
  fi
done

if [ -z "$PYTHON" ]; then
  echo "::error::a working python is required to summarise coverage"
  exit 1
fi

"$PYTHON" - "$RESULTS_DIR" "$FLOOR" <<'PY'
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ET

results_dir, floor = sys.argv[1], float(sys.argv[2])

hits = collections.defaultdict(dict)
for report in glob.glob(os.path.join(results_dir, '**', 'coverage.cobertura.xml'), recursive=True):
    for cls in ET.parse(report).getroot().iter('class'):
        name = cls.get('filename', '').replace('\\', '/')
        for line in cls.iter('line'):
            number = int(line.get('number'))
            hits[name][number] = max(hits[name].get(number, 0), int(line.get('hits')))

covered = sum(1 for lines in hits.values() for h in lines.values() if h > 0)
total = sum(len(lines) for lines in hits.values())
if total == 0:
    print('::error::Coverage report contained no lines')
    raise SystemExit(1)

pct = covered / total * 100
print(f'Line coverage: {pct:.1f}% ({covered}/{total}); floor {floor:.0f}%')

by_layer = collections.defaultdict(lambda: [0, 0])
for name, lines in hits.items():
    layer = name.split('/')[0]
    by_layer[layer][0] += sum(1 for h in lines.values() if h > 0)
    by_layer[layer][1] += len(lines)
for layer, (c, t) in sorted(by_layer.items()):
    print(f'  {layer:<30} {c / t * 100:5.1f}%')

if pct < floor:
    print(f'::error::Line coverage {pct:.1f}% is below the {floor:.0f}% floor')
    raise SystemExit(1)
PY
